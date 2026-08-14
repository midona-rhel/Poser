using System.Collections.Generic;
using System.Numerics;

namespace Poser.Game.WorldObjects;

/// <summary>
/// One BG/layout object as the last walk of the world's scene graph found it:
/// a pointer-free row carrying the address the graph reached it at, the model
/// path that names it to a human, where it stood, and the draw flags it stood
/// with.
///
/// <para>The placement and the flags are captured AT WALK TIME rather than
/// read on demand, because they are what an adoption's restore puts back —
/// Ktisis captures the same pair in its <c>WorldObject</c> constructor
/// (<c>Ktisis/Structs/Objects/WorldObject.cs:27-41</c>: <c>InitialTransform</c>
/// from Position/Rotation/Scale, <c>InitialFlags</c> from the BgObject) so that
/// the value a reset writes back can never be one the user already moved.
/// </para>
/// </summary>
public readonly record struct WorldObjectRow(
    nint Address,
    string Path,
    Transform Placement,
    byte Flags);

/// <summary>
/// One BG object the world holds and the scene has not adopted, as an
/// overlay-facing listing row: the address that adopts it, a name to say, and
/// the world point a handle projects from.
///
/// <para>It carries NO distance, unlike <c>WorldLightCandidate</c> beside it.
/// The adoption range is measured from the camera and is shared by all three
/// classes, so it is the overlay's listing pass that owns it; a distance stated
/// here could only be from the player, and would be recomputed and
/// thrown away.</para>
/// </summary>
public readonly record struct WorldObjectCandidate(
    nint Address,
    string Path,
    string Name,
    Vector3 Position = default);

/// <summary>
/// The NATIVE seam under <see cref="WorldObjectService"/>: the walk of the
/// game's own scene graph, and the reads and writes to the BG objects it
/// finds. Nothing else in Poser touches a map object.
///
/// <para>THE SAFETY RULE this seam owns, and which is NOT the actor band's
/// rule: a world object is not a character, so the 201–439 GPose object-index
/// gate says nothing about it. The rule here is OWNERSHIP BY ADOPTION —
/// <see cref="Enumerate"/> reads the whole graph, but <see cref="Write"/> and
/// <see cref="WriteFlags"/> are only ever reached through an adopted handle,
/// and every adopted handle is restored before it is forgotten. Poser never
/// creates a BG object and never destroys one; the only objects it writes are
/// the ones the user clicked, and each of those is put back.</para>
///
/// <para>THE CONTRACT every implementation owes:</para>
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
    /// <para>It lives on this seam rather than beside the light service because
    /// the world's scene graph has exactly one walk, and Ktisis reaches its
    /// lights through that same walk and no other — one recursion, partitioned
    /// by <c>ObjectType</c> at the end of it
    /// (<c>Ktisis/Services/Game/WorldService.cs:39-42</c>: BG objects and
    /// lights are two <c>Where</c> clauses over one <c>RecurseWorld()</c>).
    /// The address is the graph node's own, which is the light: Ktisis casts it
    /// straight through (<c>Scene/Entities/World/LightEntity.cs:114</c>,
    /// <c>Scene/Modules/Lights/LightModule.cs:74</c>).</para>
    ///
    /// <para>Bare addresses rather than rows because a light's interesting
    /// state is not the BG object's — no model path, no culling volume — and
    /// the light service already knows how to read one from its handle.</para>
    /// </summary>
    IReadOnlyList<nint> EnumerateLights();

    /// <summary>Whether this address is still a BG object this port can
    /// address. An adopted object whose address has gone is inert, never
    /// written and never restored onto.</summary>
    bool IsAlive(nint address);

    /// <summary>Reads one object's current placement. False leaves
    /// <paramref name="placement"/> at its default and means the address is
    /// not readable.</summary>
    bool TryRead(nint address, out Transform placement);

    /// <summary>Writes one object's placement and re-states the render and
    /// culling caches that hang off it.</summary>
    void Write(nint address, in Transform placement);

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
}
