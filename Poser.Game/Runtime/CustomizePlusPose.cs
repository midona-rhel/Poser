using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using Poser.Domain.Posing;
using Poser.Domain.Transforms;
using Poser.Game.Entities;

namespace Poser.Game;

/// <summary>
/// How Customize+ moves one bone from where Poser posed it to where it is shown, with the
/// bones above it held where they are. Customize+ moves a bone's position by a fixed scale,
/// turn and shift taken from the bones above it, so the position part is exact both ways.
/// The rotation part turns by the parents' edits before and by the bone's own edit after.
/// </summary>
internal readonly record struct CustomizePlusShift(
    Matrix4x4 Linear, Matrix4x4 Inverse, Vector3 Offset, Quaternion Before, Quaternion After)
{
    public static readonly CustomizePlusShift None = new(
        Matrix4x4.Identity, Matrix4x4.Identity, Vector3.Zero, Quaternion.Identity, Quaternion.Identity);

    public Vector3 Shown(Vector3 posed) => Vector3.Transform(posed, Linear) + Offset;
    public Vector3 Posed(Vector3 shown) => Vector3.Transform(shown - Offset, Inverse);
    public Vector3 ShownStep(Vector3 posedStep) => Vector3.TransformNormal(posedStep, Linear);
    public Vector3 PosedStep(Vector3 shownStep) => Vector3.TransformNormal(shownStep, Inverse);
    public Quaternion Shown(Quaternion posed) => Quaternion.Normalize(Before * posed * After);
    public Quaternion Posed(Quaternion shown) =>
        Quaternion.Normalize(Quaternion.Inverse(Before) * shown * Quaternion.Inverse(After));
}

/// <summary>
/// Predicts what Customize+ does to an actor's pose, from the actor's Customize+ profile.
/// IK aims at where a hand or foot is shown, so it converts that point to the pose it solves
/// in, and a drag on screen to a move in that pose, through here.
/// The prediction starts from the pose the last apply pass produced (<c>LastRawTransform</c>),
/// so it trails the solve by one frame and settles within a few.
/// Only Customize+ is predicted. Changes other posing plugins make are not.
/// </summary>
internal sealed class CustomizePlusPose
{
    /// <summary>How often a profile is read again while IK uses it. Customize+ says nothing
    /// when a profile is edited on an actor in GPose.</summary>
    private const long RefreshMilliseconds = 1000;
    /// <summary>How long a profile is kept after IK stopped asking for it.</summary>
    private const long ForgetMilliseconds = 10_000;

    private sealed class Entry(IActor actor)
    {
        public readonly IActor Actor = actor;
        public string? Json;
        public CustomizePlusBody? Body;
        public long Asked;
        public long Read;
    }

    private readonly PoseStackStore _stacks;
    private readonly Func<IActor, string?> _readProfile;
    private readonly ConcurrentDictionary<string, Entry> _actors = new();

    /// <param name="readProfile">The actor's Customize+ profile JSON, or null when it has none
    /// that can be read.</param>
    public CustomizePlusPose(PoseStackStore stacks, Func<IActor, string?> readProfile)
    {
        _stacks = stacks;
        _readProfile = readProfile;
    }

    /// <summary>Reads again the profiles IK has asked for. Runs on the framework update:
    /// the pose pass is a game hook, which never calls other plugins.</summary>
    public void Refresh()
    {
        long now = System.Environment.TickCount64;
        foreach (var (key, entry) in _actors)
        {
            if (now - entry.Asked > ForgetMilliseconds)
                _actors.TryRemove(key, out _);
            else if (now - entry.Read >= RefreshMilliseconds)
                Read(entry, now);
        }
    }

    /// <summary>Reads the actor's profile now if it has not been read yet. Called when IK is
    /// set up, outside the pose pass, so the first solve already knows the body.</summary>
    public void Prepare(IBone bone)
    {
        var entry = Ask(bone);
        if (entry.Read == 0)
            Read(entry, System.Environment.TickCount64);
    }

    private void Read(Entry entry, long now)
    {
        entry.Read = now;
        var text = _readProfile(entry.Actor);
        if (text == entry.Json)
            return;
        entry.Json = text;
        entry.Body = text == null ? null : CustomizePlusBody.Parse(text);
    }

    /// <summary>How Customize+ moves <paramref name="bone"/>, with the bones above it where
    /// the last pass posed them. No change when the actor has no profile, or nothing on the
    /// way down to this bone is edited.</summary>
    public CustomizePlusShift For(IBone bone)
    {
        if (Body(bone) is not { } body)
            return CustomizePlusShift.None;
        var names = new List<string>();
        var posed = new List<PoseTransform>();
        for (var current = bone; current != null; current = current.ParentBone)
        {
            names.Add(current.BoneName);
            var raw = _stacks.ToApplySpace(current, current.LastRawTransform);
            posed.Add(new PoseTransform(raw.Position, raw.Rotation, raw.Scale));
        }
        names.Reverse();
        posed.Reverse();
        if (!body.Edits(names))
            return CustomizePlusShift.None;
        int last = posed.Count - 1;
        var start = posed[last];
        var (shown, beforeOwn) = body.Show(names, posed);
        // The shown position is an affine function of the posed one, so three nudges give it exactly.
        const float nudge = 0.1f;
        Vector3 Row(Vector3 axis)
        {
            posed[last] = start with { Position = start.Position + axis * nudge };
            return (body.Show(names, posed).Shown.Position - shown.Position) / nudge;
        }
        var x = Row(Vector3.UnitX);
        var y = Row(Vector3.UnitY);
        var z = Row(Vector3.UnitZ);
        var linear = new Matrix4x4(
            x.X, x.Y, x.Z, 0,
            y.X, y.Y, y.Z, 0,
            z.X, z.Y, z.Z, 0,
            0, 0, 0, 1);
        // A zero scale in the profile hides the limb; there is nothing to aim at then.
        if (!Matrix4x4.Invert(linear, out var inverse) || !float.IsFinite(inverse.GetDeterminant()))
            return CustomizePlusShift.None;
        var before = Quaternion.Normalize(beforeOwn * Quaternion.Inverse(start.Rotation));
        var after = body.Bones.TryGetValue(bone.BoneName, out var own) ? own.Turn : Quaternion.Identity;
        return new CustomizePlusShift(linear, inverse,
            shown.Position - Vector3.Transform(start.Position, linear), before, after);
    }

    private CustomizePlusBody? Body(IBone bone) => Ask(bone).Body;

    private Entry Ask(IBone bone)
    {
        var actor = bone.Skeleton.Actor;
        // An actor that is replaced keeps its key but gets a fresh entry.
        var entry = _actors.AddOrUpdate(actor.Id.Unique, _ => new Entry(actor),
            (_, known) => ReferenceEquals(known.Actor, actor) ? known : new Entry(actor));
        entry.Asked = System.Environment.TickCount64;
        return entry;
    }
}
