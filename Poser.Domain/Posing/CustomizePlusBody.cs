using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using Poser.Domain.Transforms;

namespace Poser.Domain.Posing;

/// <summary>One bone's edit in a Customize+ profile, as Customize+ hands it out.</summary>
public sealed record CustomizePlusBoneEdit(
    Vector3 Translation,
    Vector3 RotationDegrees,
    Vector3 Scaling,
    Vector3 ChildScaling,
    bool ChildScalingIndependent,
    bool PropagateTranslation,
    bool PropagateRotation,
    bool PropagateScale)
{
    /// <summary>Customize+ skips a bone whose edit changes nothing (BoneTransform.IsEdited).</summary>
    public bool IsEdited =>
        !Near(Translation, Vector3.Zero, 0.00001f)
        || !Near(RotationDegrees, Vector3.Zero, 0.1f)
        || !Near(Scaling, Vector3.One, 0.00001f)
        || ChildScalingIndependent && !Near(ChildScaling, Vector3.One, 0.00001f);

    /// <summary>The edit's turn. Customize+ reads the X angle as yaw, Y as pitch and Z as roll.</summary>
    public Quaternion Turn => Quaternion.CreateFromYawPitchRoll(
        RotationDegrees.X * MathF.PI / 180, RotationDegrees.Y * MathF.PI / 180, RotationDegrees.Z * MathF.PI / 180);

    private static bool Near(Vector3 a, Vector3 b, float margin) =>
        MathF.Abs(a.X - b.X) < margin && MathF.Abs(a.Y - b.Y) < margin && MathF.Abs(a.Z - b.Z) < margin;
}

/// <summary>
/// The bone edits of an actor's Customize+ profile, and where Customize+ will show a bone
/// once it has applied them. Customize+ edits the pose after Poser has posed it, so this is
/// how Poser knows where a hand or foot will appear before it is drawn.
/// Copied from Customize+ 2.2.1.3, <c>ArmatureManager.ApplyPiecewiseTransformation</c> and
/// <c>ModelBone.ApplyModelTransform</c>. Keep it the same as that code, including its quirks.
/// </summary>
public sealed class CustomizePlusBody
{
    private readonly Dictionary<string, CustomizePlusBoneEdit> _bones;

    private CustomizePlusBody(Dictionary<string, CustomizePlusBoneEdit> bones) => _bones = bones;

    public IReadOnlyDictionary<string, CustomizePlusBoneEdit> Bones => _bones;

    /// <summary>Reads the profile JSON Customize+ gives out (its IPC profile format).
    /// Null when the text is not such a profile or edits no bone.</summary>
    public static CustomizePlusBody? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("Bones", out var bones) || bones.ValueKind != JsonValueKind.Object)
                return null;
            var edits = new Dictionary<string, CustomizePlusBoneEdit>(StringComparer.Ordinal);
            foreach (var bone in bones.EnumerateObject())
            {
                var value = bone.Value;
                bool independent = Flag(value, "ChildScaleIndependent");
                var childScaling = Vector(value, "ChildScaling", Vector3.One);
                // Customize+ reads a missing child scale as one unless it is in use.
                if (childScaling == Vector3.Zero && !independent)
                    childScaling = Vector3.One;
                var edit = new CustomizePlusBoneEdit(
                    Vector(value, "Translation", Vector3.Zero),
                    Vector(value, "Rotation", Vector3.Zero),
                    // Customize+ leaves out every value equal to its default, so a missing scale is zero.
                    Vector(value, "Scaling", Vector3.Zero),
                    childScaling,
                    independent,
                    Flag(value, "PropagateTranslation"),
                    Flag(value, "PropagateRotation"),
                    Flag(value, "PropagateScale"));
                if (edit.IsEdited)
                    edits[bone.Name] = edit;
            }
            return edits.Count == 0 ? null : new CustomizePlusBody(edits);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>True when any bone on <paramref name="path"/> has an edit.</summary>
    public bool Edits(IReadOnlyList<string> path)
    {
        foreach (var name in path)
            if (name != RootBone && _bones.ContainsKey(name))
                return true;
        return false;
    }

    /// <summary>
    /// Where Customize+ shows the last bone of <paramref name="path"/>. The path runs from the
    /// top bone of the skeleton down to that bone, parent before child, and
    /// <paramref name="posed"/> holds each bone's model transform as Poser posed it.
    /// Only a bone's parents and the bone itself change where it is shown.
    /// Also returns the bone's rotation after its parents' edits and before its own.
    /// </summary>
    public (PoseTransform Shown, Quaternion RotationBeforeOwnEdit) Show(
        IReadOnlyList<string> path, IReadOnlyList<PoseTransform> posed)
    {
        var bones = new PoseTransform[posed.Count];
        for (int i = 0; i < bones.Length; i++)
            bones[i] = posed[i];
        int last = bones.Length - 1;
        Quaternion? beforeOwn = null;
        for (int i = 0; i < bones.Length; i++)
        {
            // The root bone's edit goes to the whole model, not to the pose.
            if (path[i] == RootBone || !_bones.TryGetValue(path[i], out var edit))
                continue;
            if (i == last)
                beforeOwn = bones[i].Rotation;
            var initial = bones[i];
            var modified = Modify(initial, edit);
            bones[i] = modified;
            if (!(edit.PropagateTranslation || edit.PropagateRotation || edit.PropagateScale))
                continue;
            var childScale = edit.ChildScalingIndependent ? initial.Scale * edit.ChildScaling : modified.Scale;
            // These two tests use exact comparisons, as Customize+ does.
            bool propagateScale = edit.PropagateScale
                && (edit.Scaling != Vector3.One || edit.ChildScalingIndependent && edit.ChildScaling != Vector3.One);
            bool propagateRotation = edit.PropagateRotation && edit.RotationDegrees != Vector3.Zero;
            // The bones below always follow the bone's move once any Propagate box is ticked:
            // Customize+ passes its Propagate Translation choice on but never reads it.
            var source = modified.Position;
            var deltaPosition = source - initial.Position;
            var deltaRotation = modified.Rotation / initial.Rotation;
            var deltaScale = childScale / initial.Scale;
            for (int j = i + 1; j < bones.Length; j++)
            {
                var offset = bones[j].Position - source;
                var matrix = Matrix4x4.CreateScale(bones[j].Scale)
                    * Matrix4x4.CreateFromQuaternion(bones[j].Rotation)
                    * Matrix4x4.CreateTranslation(bones[j].Position);
                if (propagateScale)
                {
                    var scale = Matrix4x4.CreateScale(deltaScale, Vector3.Zero);
                    matrix *= scale;
                    offset = Vector3.Transform(offset, scale);
                }
                if (propagateRotation)
                {
                    matrix *= Matrix4x4.CreateFromQuaternion(deltaRotation);
                    offset = Vector3.Transform(offset, deltaRotation);
                }
                matrix.Translation = deltaPosition + source + offset;
                bones[j] = Matrix4x4.Decompose(matrix, out var size, out var turn, out var place)
                    ? new PoseTransform(place, turn, size)
                    : bones[j] with { Position = matrix.Translation };
            }
        }
        return (bones[last], beforeOwn ?? bones[last].Rotation);
    }

    /// <summary>The bone's own edit: scale, then turn, then a move along the turned axes
    /// (<c>BoneTransform.ModifyExistingTransform</c>).</summary>
    private static PoseTransform Modify(PoseTransform bone, CustomizePlusBoneEdit edit)
    {
        var rotation = bone.Rotation * edit.Turn;
        return new PoseTransform(
            bone.Position + Vector3.Transform(edit.Translation, rotation),
            rotation,
            bone.Scale * edit.Scaling);
    }

    private const string RootBone = "n_root";

    private static Vector3 Vector(JsonElement owner, string name, Vector3 fallback) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? new Vector3(Number(value, "X"), Number(value, "Y"), Number(value, "Z"))
            : fallback;

    private static float Number(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetSingle() : 0f;

    private static bool Flag(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
