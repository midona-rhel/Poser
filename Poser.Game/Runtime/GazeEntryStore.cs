using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Game;

/// <summary>
/// One managed+native entry per actor. Mutated from the UI thread and read
/// from the hooked game loop, so every access goes through the store's lock.
/// </summary>
internal sealed class GazeEntry
{
    /// <summary>The CONFIGURED mode. Remembered across a full untoggle:
    /// Brio's SetTargetType only rewrites the participation mask and never
    /// touches TargetMode, so re-adding a part resumes the same mode.</summary>
    public GazeTargetMode Mode;
    public bool PoseAware;
    public GazeTargetType Parts = GazeTargetType.All;

    /// <summary>The body this entry drives; the detour's lookup key.</summary>
    public nint Address;

    /// <summary>The remembered Entity target; null = never chosen.
    /// Surviving a full untoggle is the point — it is cleared only by
    /// <see cref="GazeService.ResetGaze"/>, which is Brio's RemoveObjectFromLook.</summary>
    public ActorId? TargetActor;

    /// <summary>The target's GameObjectId: only the value written into
    /// the native target union, never an identity. 0 when unset.</summary>
    public ulong TargetId;

    /// <summary>The remembered target is no longer in the object table.
    /// Exact identity, never an address: the id stays so the refusal can
    /// name it, and reapplying it is refused rather than followed.</summary>
    public bool TargetStale;

    /// <summary>The character target id Poser last wrote natively; 0 when
    /// Poser has written none. Poser only ever clears what it set.</summary>
    public ulong AppliedTargetId;

    public Vector3 Position;            // Position-mode shared world anchor
    public LookAtSource Target;         // per-part native write source
    public bool EyesLocked;
    public bool HeadLocked;
    public bool BodyLocked;

    /// <summary>Channels Poser currently claims — the set the detour is
    /// enforcing. Only a claimed channel can be owed a hand-back.</summary>
    public GazeTargetType ClaimedParts;

    /// <summary>Channels owed ONE disable write. Booked by the transition
    /// that dropped them and delivered by the detour on the native
    /// thread, which is the only place _updateLookAt may be called.</summary>
    public GazeTargetType PendingRelease;
}

/// <summary>
/// The gaze entries, keyed by the binding registry's <see cref="ActorId"/>,
/// with the detour's view of them by body address; plus the pure per-entry
/// policy every transition and the detour share. Every instance member is
/// called with <see cref="Sync"/> held.
/// </summary>
internal sealed class GazeEntryStore
{
    /// <summary>The one lock between the UI-thread commands and the hooked
    /// native loop.</summary>
    public readonly object Sync = new();

    private readonly Dictionary<ActorId, GazeEntry> _entries = new();

    /// <summary>The detour's view of <see cref="_entries"/>, by body address.</summary>
    private readonly Dictionary<nint, GazeEntry> _byAddress = new();

    public int Count => _entries.Count;

    public bool TryGet(ActorId id, [MaybeNullWhen(false)] out GazeEntry entry) =>
        _entries.TryGetValue(id, out entry);

    public bool TryGetByAddress(nint address, [MaybeNullWhen(false)] out GazeEntry entry) =>
        _byAddress.TryGetValue(address, out entry);

    /// <summary>Each entry's id and remembered target, for a reconciliation
    /// pass that resolves them outside the lock.</summary>
    public List<(ActorId Id, ActorId? Target)> SnapshotTargets()
    {
        var snapshot = new List<(ActorId, ActorId?)>(_entries.Count);
        foreach (var (id, entry) in _entries)
            snapshot.Add((id, entry.TargetActor));
        return snapshot;
    }

    public void Clear()
    {
        _entries.Clear();
        _byAddress.Clear();
    }

    /// <summary>The actor's entry, created on first use, with the detour's
    /// address index moved to the body it currently names.</summary>
    public GazeEntry Bind(ActorId id, nint address)
    {
        if (!_entries.TryGetValue(id, out var entry))
            _entries[id] = entry = new GazeEntry();
        if (entry.Address != address)
        {
            Unindex(entry);
            entry.Address = address;
        }
        _byAddress[address] = entry;
        return entry;
    }

    public void Drop(ActorId id)
    {
        if (_entries.Remove(id, out var entry))
            Unindex(entry);
    }

    private void Unindex(GazeEntry entry)
    {
        if (_byAddress.TryGetValue(entry.Address, out var indexed) && ReferenceEquals(indexed, entry))
            _byAddress.Remove(entry.Address);
    }

    // ── per-entry policy ─────────────────────────────────────────────────

    /// <summary>
    /// The mode the entry's stored per-part sources are SEEDED from. Entity
    /// without a usable target seeds nothing; the participation mask is
    /// deliberately not consulted, so untoggling every part leaves the stored
    /// positions and target id exactly as they were (Brio's SetTargetType
    /// rewrites the mask and nothing else).
    /// </summary>
    public static GazeTargetMode SeedMode(GazeEntry entry) =>
        entry.Mode == GazeTargetMode.Entity && (entry.TargetActor is null || entry.TargetStale)
            ? GazeTargetMode.None
            : entry.Mode;

    /// <summary>
    /// What the detour actually enforces. No participating part means Poser
    /// writes nothing at all — a channel outside the mask gets no
    /// _updateLookAt call, exactly as in Brio, where the original loop then
    /// runs unconditionally.
    /// </summary>
    public static GazeTargetMode EffectiveMode(GazeEntry entry) =>
        entry.Parts == GazeTargetType.None
            ? GazeTargetMode.None
            : SeedMode(entry);

    /// <summary>The channels the detour will enforce on its next pass.</summary>
    public static GazeTargetType EnforcedParts(GazeEntry entry) =>
        EffectiveMode(entry) == GazeTargetMode.None
            ? GazeTargetType.None
            : entry.Parts;

    /// <summary>
    /// Books the hand-back this transition owes. Ceasing to write a channel is
    /// NOT a release: _updateLookAt copies into the controller's persistent
    /// per-channel slot (Ktisis names the same native call
    /// <c>ActorLookAt(ActorGaze* writeTo, Gaze* readFrom, GazeControl part)</c>
    /// — Scene/Modules/Actors/ActorModule.cs:231), so a channel Poser stops
    /// writing keeps aiming at the last target it was given. Each dropped
    /// channel is therefore owed exactly one INACTIVE write; Brio spells that
    /// released value out in StopLookAt as LookMode.None on every part
    /// (Brio/Game/Actor/ActorLookAtService.cs:101-108) and Ktisis calls the
    /// same value GazeMode.Disabled (Ktisis/Structs/Actors/ActorGaze.cs:75).
    /// A channel that comes straight back cancels its debt, because the active
    /// write supersedes the disable. Callers hold <see cref="Sync"/>.
    /// </summary>
    public static void BookRelease(GazeEntry entry)
    {
        var enforced = EnforcedParts(entry);
        entry.PendingRelease =
            (entry.PendingRelease | (entry.ClaimedParts & ~enforced)) & ~enforced;
        entry.ClaimedParts = enforced;
    }

    public static void ApplyPartLock(GazeEntry entry, GazeTargetType part, Vector3 position)
    {
        var target = new LookAtTarget { LookMode = LookMode.Position, Position = position };
        if (part.HasFlag(GazeTargetType.Eyes)) entry.EyesLocked = true;
        if (part.HasFlag(GazeTargetType.Head)) entry.HeadLocked = true;
        if (part.HasFlag(GazeTargetType.Body)) entry.BodyLocked = true;
        WritePart(entry, part, target);
    }

    public static void ClearPartLock(GazeEntry entry, GazeTargetType part)
    {
        if (part.HasFlag(GazeTargetType.Eyes)) entry.EyesLocked = false;
        if (part.HasFlag(GazeTargetType.Head)) entry.HeadLocked = false;
        if (part.HasFlag(GazeTargetType.Body)) entry.BodyLocked = false;
    }

    public static void WritePart(GazeEntry entry, GazeTargetType part, LookAtTarget target)
    {
        if (part.HasFlag(GazeTargetType.Eyes)) entry.Target.Eyes.LookAtTarget = target;
        if (part.HasFlag(GazeTargetType.Head)) entry.Target.Head.LookAtTarget = target;
        if (part.HasFlag(GazeTargetType.Body)) entry.Target.Body.LookAtTarget = target;
    }

    /// <summary>
    /// The single-flag part's stored target position; null when the flag is
    /// not exactly one known part (so callers can fall back to the anchor).
    /// </summary>
    public static Vector3? PartPosition(GazeEntry entry, GazeTargetType part) => part switch
    {
        GazeTargetType.Eyes => entry.Target.Eyes.LookAtTarget.Position,
        GazeTargetType.Head => entry.Target.Head.LookAtTarget.Position,
        GazeTargetType.Body => entry.Target.Body.LookAtTarget.Position,
        _ => null,
    };
}
