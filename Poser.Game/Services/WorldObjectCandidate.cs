using System.Numerics;

namespace Poser.Services;

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
