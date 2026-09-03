using System.Collections.Generic;
using Poser.Services;
using System.Numerics;

namespace Poser.Game.WorldObjects;

/// <summary>
/// One BG/layout object as the last walk of the world's scene graph found it:
/// a pointer-free row carrying the address the graph reached it at, the model
/// path that names it to a human, where it stood, and the draw flags it stood
/// with.
///
/// <para>Placement and flags are captured during the walk because adoption
/// restores those values. Reading them later could capture a placement that
/// the user has already changed.</para>
/// </summary>
public readonly record struct WorldObjectRow(
    nint Address,
    string Path,
    Transform Placement,
    byte Flags,
    bool IsEffect = false);

/// <summary>The native instance identity captured by a claim. Address alone
/// is not sufficient: the game can recycle an object slot while an old
/// handle is still in Poser's list.</summary>
public readonly record struct WorldObjectIncarnation(
    nint Address,
    long Generation,
    nint ResourceIdentity,
    bool IsVfx = false);

public enum VfxPlaybackState
{
    Unavailable,
    Playing,
    Paused,
    Inactive,
}

public readonly record struct VfxStateSnapshot(
    System.Numerics.Vector4 Color,
    System.Numerics.Vector3 Intensity,
    float Speed,
    VfxPlaybackState Playback);

/// <summary>
/// The NATIVE seam under <see cref="WorldObjectService"/>: the walk of the
/// game's own scene graph, and the reads and writes to the BG objects it
/// finds. Nothing else in Poser touches a map object.
///
/// <para>Enumeration may read the whole graph, but writes are reached only
/// through an adopted or spawned handle. The map's own objects are never
/// created or destroyed — each is restored before its handle is forgotten.
/// Objects POSER spawned are the one exception: they are Poser's to destroy,
/// and are never restored onto.</para>
///
/// <para>Every implementation must:</para>
/// <list type="number">
/// <item><description><see cref="Enumerate"/> returns rows whose placement and
/// flags are the values the world held at the moment of the walk, and never
/// throws — a graph it cannot read is an empty listing.</description></item>
/// <item><description><see cref="IsAlive"/> answers false for any address the
/// implementation cannot still see as a BG object, and every read and write
/// below is a no-op for such an address.</description></item>
/// <item><description><see cref="Write"/> leaves the object drawn where it was
/// put — the game caches culling and render state off the transform, so the
/// write is not complete until that cache is re-stated.</description></item>
/// </list>
/// </summary>
public interface IWorldObjectPort
{
    /// <summary>Whether the world's scene graph can be reached at all right
    /// now. False makes <see cref="Enumerate"/> answer empty rather than walk
    /// a null root.</summary>
    bool IsAvailable { get; }

    /// <summary>Walks the world's scene graph and returns every BG object in
    /// it. The whole listing, unfiltered and unsorted — what is adoptable is
    /// the service's question, not the graph's.</summary>
    IReadOnlyList<WorldObjectRow> Enumerate();

    /// <summary>The same walk, filtered to the graph's LIGHT-typed nodes, as
    /// bare addresses.
    ///
    /// <para>The address is the graph node's own. Lights do not have BG model
    /// paths or culling state, and the light service reads them from these
    /// addresses.</para>
    /// </summary>
    IReadOnlyList<nint> EnumerateLights();

    /// <summary>Whether this address is still a BG object this port can
    /// address. An adopted object whose address has gone is inert, never
    /// written and never restored onto.</summary>
    bool IsAlive(nint address);

    /// <summary>Reads the current native incarnation. Implementations that
    /// cannot expose a resource identity may return a stable address token;
    /// native VFX/BG ports should provide their generation/resource pair.</summary>
    bool TryReadIncarnation(
        nint address, out WorldObjectIncarnation incarnation)
    {
        incarnation = new WorldObjectIncarnation(address, 0, nint.Zero);
        return IsAlive(address);
    }

    /// <summary>Reads one object's current placement. False leaves
    /// <paramref name="placement"/> at its default and means the address is
    /// not readable.</summary>
    bool TryRead(nint address, out Transform placement);

    /// <summary>Writes one object's placement and re-states the render and
    /// culling caches that hang off it.</summary>
    void Write(nint address, in Transform placement);

    /// <summary>Writes and refreshes a VFX transform without an implicit
    /// replay. Playback commands are separate so a drag cannot restart a
    /// playing effect; paused and inactive effects remain stopped.</summary>
    void WriteVfxTransform(nint address, in Transform placement) =>
        Write(address, placement);

    bool TryWriteVfxTransform(nint address, in Transform placement)
    {
        WriteVfxTransform(address, placement);
        return true;
    }

    /// <summary>Reads one object's draw flags — the byte that carries, among
    /// other things, whether it is drawn at all.</summary>
    bool TryReadFlags(nint address, out byte flags);

    /// <summary>Writes one object's draw flags back wholesale. The restore
    /// path's write: a captured byte goes back as it was read.</summary>
    void WriteFlags(nint address, byte flags);

    /// <summary>Whether one object is drawn. Stated in its own right rather
    /// than read out of a bit of <see cref="TryReadFlags"/>: which bit of the
    /// flags byte carries it is the game's business, and this seam's callers
    /// must not have to know it.</summary>
    bool TryReadVisible(nint address, out bool visible);

    /// <summary>Shows or hides one object, leaving every other draw flag as it
    /// was.</summary>
    void WriteVisible(nint address, bool visible);

    /// <summary>Reads one object's outline byte — the game's own
    /// selection-highlight state, the same mark a quest interactable wears.
    /// </summary>
    bool TryReadOutline(nint address, out byte outline);

    /// <summary>Writes one object's outline byte. Paired with
    /// <see cref="TryReadOutline"/> and never with a literal restore value:
    /// the byte carries more than the colour, so what a hover puts back is
    /// what the hover found.</summary>
    void WriteOutline(nint address, byte outline);

    /// <summary>Creates a NEW BG object from a model path at the given
    /// placement — Brio's spawn-by-path (<c>BgObject.Create</c>), the way
    /// its world-object clone works. Zero when the game refuses. A spawned
    /// object is Poser's own: destroyed through <see cref="Destroy"/>,
    /// never restored.</summary>
    nint Spawn(string path, in Transform placement);

    /// <summary>Sets a spawned VFX's playback speed. A no-op on anything
    /// that is not a live VFX.</summary>
    void SetVfxSpeed(nint address, float speed);

    bool TrySetVfxSpeed(nint address, float speed)
    {
        SetVfxSpeed(address, speed);
        return true;
    }

    /// <summary>Writes a VFX's colour multiplier (RGB; the effect's alpha
    /// stays the opacity's). A no-op on a BG object — model staining needs
    /// natives this port does not carry yet.</summary>
    void WriteVfxTint(nint address, System.Numerics.Vector3 tint);

    /// <summary>One uniform brightness on the effect's intensity triple.
    /// </summary>
    void SetVfxIntensity(nint address, float intensity);

    /// <summary>Freezes the effect mid-frame (pause native + speed 0).
    /// </summary>
    void PauseVfx(nint address);

    bool TryPauseVfx(nint address)
    {
        PauseVfx(address);
        return true;
    }

    /// <summary>Plays a paused effect again at the stated speed.</summary>
    void ResumeVfx(nint address, float speed);

    bool TryResumeVfx(nint address, float speed)
    {
        ResumeVfx(address, speed);
        return true;
    }

    /// <summary>Whether the effect is still playing.</summary>
    bool IsVfxActive(nint address);

    /// <summary>Captures the effect state an adoption may edit.</summary>
    bool TryReadVfxState(
        nint address,
        out System.Numerics.Vector4 color,
        out System.Numerics.Vector3 intensity,
        out float speed);

    /// <summary>Reads the complete state needed for exact VFX rollback.
    /// Returning false means playback is not safely observable.</summary>
    bool TryReadVfxSnapshot(
        nint address, out VfxStateSnapshot snapshot)
    {
        snapshot = default;
        if (!TryReadVfxState(address, out var color, out var intensity,
                out var speed)
            || !TryReadVfxPlayback(address, out var playback))
            return false;
        snapshot = new VfxStateSnapshot(color, intensity, speed, playback);
        return true;
    }

    /// <summary>Reads Playing, Paused or Inactive separately. Unavailable is
    /// returned as false because it is a refusal to capture.</summary>
    bool TryReadVfxPlayback(
        nint address, out VfxPlaybackState playback)
    {
        playback = VfxPlaybackState.Unavailable;
        return false;
    }

    /// <summary>Puts a captured effect state back on release.</summary>
    void RestoreVfxState(
        nint address,
        System.Numerics.Vector4 color,
        System.Numerics.Vector3 intensity,
        float speed,
        bool resume);

    /// <summary>Exact rollback including inactive/paused state. The legacy
    /// method remains for ports that only support the earlier bool contract.
    /// </summary>
    bool TryRestoreVfxState(
        nint address, VfxStateSnapshot snapshot)
    {
        RestoreVfxState(address, snapshot.Color, snapshot.Intensity,
            snapshot.Speed, snapshot.Playback == VfxPlaybackState.Playing);
        return true;
    }

    /// <summary>Dyes a BG object; null clears to white. False while the
    /// model has not produced its stain buffer yet — retry next tick.
    /// </summary>
    bool WriteBgTint(nint address, System.Numerics.Vector3? tint);

    /// <summary>Whether a BG object's model has fully streamed in.</summary>
    bool IsBgReady(nint address);

    /// <summary>The instance's day/night state byte: true = night (a raw
    /// spawn's default). Null for effects.</summary>
    /// <summary>Whether the BG model can take dye (its stain buffer
    /// exists); null while it is still streaming.</summary>
    bool? CanDyeBg(nint address);

    bool? ReadBgNightState(nint address);

    /// <summary>Sets an animated BG object's playback speed; false until
    /// its skeleton's controls exist.</summary>
    bool WriteBgAnimationSpeed(nint address, float speed);

    byte? ReadBgTailByte(nint address, int offset);

    void WriteBgTailByte(nint address, int offset, byte value);

    /// <summary>Diagnostic: the instance's animation topology.</summary>
    string DescribeBgAnimation(nint address);

    bool TryReadBgTail(nint address, byte[] into);

    void WriteBgTailHeld(nint address, byte[] values);

    ulong? ReadBgObjectFlags(nint address);

    void WriteBgObjectFlags(nint address, ulong flags);

    void WriteBgNightState(nint address, bool night);

    /// <summary>Writes the drawn opacity, 1 fully drawn through 0 gone: a
    /// VFX's alpha, a BG object's dither transparency.</summary>
    void WriteOpacity(nint address, float opacity);

    /// <summary>Destroys a spawned object — BG or VFX; the vtable serves
    /// both. Never called with an adopted address — the map's own objects
    /// are always restored instead.</summary>
    void Destroy(nint address);

    /// <summary>Reports whether teardown actually completed. The default
    /// preserves the old void seam for test/fallback ports.</summary>
    bool TryDestroy(nint address)
    {
        Destroy(address);
        return true;
    }

    /// <summary>Destroys only the exact VFX incarnation named by a claim.
    /// A replacement at the same address must never be destroyed.</summary>
    bool TryDestroyVfx(WorldObjectIncarnation incarnation)
    {
        if (!TryReadIncarnation(incarnation.Address, out var current)
            || current != incarnation)
            return false;
        return TryDestroy(incarnation.Address);
    }

    /// <summary>Releases an exact VFX claim after its native object vanished,
    /// without touching a replacement at the same address.</summary>
    bool TryReleaseVfxClaim(WorldObjectIncarnation incarnation) => false;
}

