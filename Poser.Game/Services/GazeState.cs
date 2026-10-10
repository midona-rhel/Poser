using System.Numerics;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Entities;
using Poser.Domain.Scene;

namespace Poser.Services;


/// <summary>
/// Read snapshot of an actor's managed gaze state. Durable identity is never
/// an <see cref="IActor"/> reference: the service keys state by the binding
/// registry's <see cref="ActorId"/> and remembers the Entity target the same
/// way, because a GPose clone shares its source's GameObjectId.
/// </summary>
public class GazeState
{
    public bool PoseAware { get; set; }
    /// <summary>
    /// The CONFIGURED mode, which is remembered across a full untoggle — it is
    /// not a claim that anything is being enforced. Ask <see cref="Active"/>
    /// for that.
    /// </summary>
    public GazeTargetMode Mode { get; set; } = GazeTargetMode.None;

    /// <summary>
    /// Whether Poser is enforcing any channel right now. False whenever every
    /// part is untoggled or the remembered target is stale, even though the
    /// mode and target are still remembered.
    /// </summary>
    public bool Active { get; set; }

    /// <summary>The remembered Entity target has left the scene: it is kept by
    /// id so a reapply can be refused by name rather than followed.</summary>
    public bool TargetStale { get; set; }

    public GazeTargetType TargetType { get; set; } = GazeTargetType.All;

    /// <summary>The Entity-mode target's stable identity; null when unset.</summary>
    public ActorId? TargetActor { get; set; }

    /// <summary>The Entity-mode target's GameObjectId, as written natively;
    /// 0 when unset. Not an identity: clones share it.</summary>
    public ulong TargetId { get; set; }

    /// <summary>The shared Position-mode anchor — what the world gizmo grabs.</summary>
    public Vector3 Position { get; set; }

    /// <summary>The eyes' live target position.</summary>
    public Vector3 EyesPosition { get; set; }

    /// <summary>The head's live target position.</summary>
    public Vector3 HeadPosition { get; set; }

    /// <summary>The body's live target position.</summary>
    public Vector3 BodyPosition { get; set; }
}
