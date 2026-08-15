using System;
using System.Numerics;
using Poser.Domain.Presentation;

namespace Poser.Game.Overlays;

/// <summary>
/// The NATIVE seam under <see cref="OverlayNodeService"/>: everything that
/// touches the game's own UI node tree, and nothing else.
///
/// <para>An overlay node is a real <c>AtkResNode</c> subtree the plugin
/// builds, hands to the game's UI to draw, and must take back before the game
/// tears that UI down. Every one of those acts is behind this interface for
/// the reason every other native seam in Poser.Game is: the OWNERSHIP rules —
/// one create per node, exactly one destroy, nothing touched after the
/// destroy — are provable against a fake, and a leaked or double-freed native
/// node is a crash in the game's renderer rather than a failed assertion.
/// </para>
///
/// <para>THE TEARDOWN CONTRACT, which every implementation owes and every
/// caller may rely on:</para>
/// <list type="number">
/// <item><description><see cref="Create"/> either returns a token that must
/// later be destroyed exactly once, or returns null having created
/// nothing.</description></item>
/// <item><description><see cref="Destroy"/> is idempotent per token and never
/// throws; a token that has been destroyed is inert.</description></item>
/// <item><description><see cref="Dispose"/> destroys every token this port
/// still holds — it is the last line of defence for the plugin-unload edge,
/// and it must leave the game's UI exactly as it found it.</description></item>
/// </list>
/// </summary>
public interface IOverlayNodePort : IDisposable
{
    /// <summary>Whether the game's UI can host nodes at all right now. False
    /// makes every <see cref="Create"/> answer null rather than half-build a
    /// subtree.</summary>
    bool IsAvailable { get; }

    /// <summary>Builds one node in the stated state and attaches it. Null when
    /// the node could not be built, in which case NOTHING was attached.
    /// </summary>
    object? Create(OverlayNodeState state);

    /// <summary>Re-states an existing node from its document. Every editable
    /// field goes through here — there is no per-field native setter — so one
    /// write is one re-state.</summary>
    void Apply(object node, OverlayNodeState state);

    /// <summary>Detaches and frees one node. Safe to call twice.</summary>
    void Destroy(object node);

    /// <summary>
    /// The pointer itself finished dragging a node, told the node's token and
    /// where it now sits. The one INBOUND edge of this port: a drag is the only
    /// way a node's state changes without a caller asking for it, and a
    /// document that does not hear about it re-states the old position on the
    /// next write of any field.
    ///
    /// <para>Raised on the game's own thread, inside the frame the drag ended
    /// in. Set by the service above; a port with nothing listening simply drops
    /// the news.</para>
    /// </summary>
    Action<object, Vector2>? Moved { get; set; }
}
