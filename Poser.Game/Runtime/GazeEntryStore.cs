using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Game;

/// <summary>
/// Gaze settings and pending native writes for one actor. Callers hold the store lock.
/// </summary>
internal sealed class GazeEntry
{
    /// <summary>
    /// Configured mode, retained when every body part is disabled.
    /// </summary>
    public GazeTargetMode Mode;
    public bool PoseAware;
    public GazeTargetType Parts = GazeTargetType.All;

    /// <summary>The body this entry drives; the detour's lookup key.</summary>
    public nint Address;

    /// <summary>
    /// Remembered actor target. Disabling gaze retains it; Reset clears it.
    /// </summary>
    public ActorId? TargetActor;

    /// <summary>
    /// GameObjectId written to the native target field. ActorId identifies the target in Poser.
    /// </summary>
    public ulong TargetId;

    /// <summary>
    /// The remembered target failed reconciliation. Selecting a live target clears this flag.
    /// </summary>
    public bool TargetStale;

    /// <summary>
    /// Last target ID scheduled for a native write. This is updated before the write runs.
    /// </summary>
    public ulong AppliedTargetId;

    public Vector3 Position;            // Position-mode shared world anchor
    public LookAtSource Target;         // per-part native write source
    public bool EyesLocked;
    public bool HeadLocked;
    public bool BodyLocked;

    /// <summary>
    /// Parts enabled by the most recent transition. Used to decide which parts need disabling.
    /// </summary>
    public GazeTargetType ClaimedParts;

    /// <summary>
    /// Parts waiting for a native disable write in the hooked look-at loop.
    /// </summary>
    public GazeTargetType PendingRelease;
}

/// <summary>
/// Stores gaze settings by ActorId and indexes them by address for the native hook.
/// Callers must hold Sync when accessing entries; the store does not acquire the lock itself.
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

    // Shared mode and part settings.

    /// <summary>
    /// Returns the configured mode, except Entity mode requires a usable target.
    /// Part participation is checked separately by EffectiveMode.
    /// </summary>
    public static GazeTargetMode SeedMode(GazeEntry entry) =>
        entry.Mode == GazeTargetMode.Entity && (entry.TargetActor is null || entry.TargetStale)
            ? GazeTargetMode.None
            : entry.Mode;

    /// <summary>
    /// Returns None when all parts are disabled or an Entity target is unavailable.
    /// Pending disable writes may still run even when this returns None.
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
    /// Queues a disable write for each part no longer controlled by Poser. The native
    /// controller retains its last target until explicitly disabled. Re-enabling a part
    /// cancels its pending disable. Brio StopLookAt and Ktisis GazeMode.Disabled use
    /// the same native disabled value. Callers hold Sync.
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
