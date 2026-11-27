using System;
using System.Numerics;
using Poser.Domain.Presentation;

namespace Poser.Game.Overlays;

/// <summary>
/// Builds, redraws and frees the game UI nodes that draw overlays (dialog
/// boxes, speech balloons and status lines). <see cref="OverlayNodeService"/>
/// uses it; <see cref="KamiToolKitOverlayDrawer"/> is the one in the game, and
/// tests pass their own.
///
/// <para>A node is handed to the game's UI to draw, so it has to be freed
/// exactly once, and before the game takes that UI down. A node freed twice, or
/// left behind when Poser unloads, crashes the game.</para>
/// </summary>
public interface IOverlayNodeDrawer : IDisposable
{
    /// <summary>False when the overlay library could not start or Poser is
    /// unloading. <see cref="Create"/> then returns null.</summary>
    bool IsAvailable { get; }

    /// <summary>Builds one node with the given values and hands it to the game
    /// to draw. Null when it could not be built; nothing is left behind then.
    /// </summary>
    object? Create(OverlayNodeState state);

    /// <summary>Redraws a node with the given values. Every overlay edit goes
    /// through here.</summary>
    void Apply(object node, OverlayNodeState state);

    /// <summary>Takes a node back from the game and frees it. Freeing one that
    /// is already freed does nothing.</summary>
    void Destroy(object node);

    /// <summary>
    /// Called when the player finishes dragging a node on screen, with the node
    /// and where it now is. The game has already moved the node. A drag is the
    /// only change to a node that Poser did not make, so the stored position
    /// has to be updated from this, or the next edit would put the node back.
    /// Called on the game's thread, in the frame the drag ended.
    /// </summary>
    Action<object, Vector2>? Moved { get; set; }
}
