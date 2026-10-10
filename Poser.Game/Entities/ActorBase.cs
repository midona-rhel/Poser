using System.Linq;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Poser.Core;

namespace Poser.Entities;

public class ActorBase : EntityBase, IActor
{
    public nint Address { get; }
    public ActorKind ActorKind { get; }

    /// <summary>
    /// Gets the current world transform of this actor.
    /// </summary>
    public override Transform Transform
    {
        get => new(Position, Rotation, Vector3.One);
        set { /* Actors use IPosingService for transform changes */ }
    }

    /// <summary>
    /// Whether animation controls are available for this entity.
    /// Companions (minions, mounts) have limited animation control.
    /// </summary>
    public bool CanControlAnimation => !IsCompanion;

    /// <summary>
    /// The skeleton owned by this actor, or null if not available.
    /// </summary>
    public ISkeleton? Skeleton => Children.OfType<ISkeleton>().FirstOrDefault();

    public ActorBase(EntityId id, string name, nint address, ActorKind actorKind = ActorKind.None)
        : base(id, name)
    {
        Address = address;
        ActorKind = actorKind;
    }

    /// <summary>
    /// Returns true if this actor is a companion (minion, mount, pet).
    /// </summary>
    public bool IsCompanion => ActorKind == ActorKind.Companion ||
                               ActorKind == ActorKind.Mount ||
                               ActorKind == ActorKind.Ornament;

    /// <summary>
    /// Returns true if this actor is a player character.
    /// </summary>
    public bool IsPlayer => ActorKind == ActorKind.Player;

    /// <summary>
    /// Returns true if this actor is an NPC (battle or event).
    /// </summary>
    public bool IsNpc => ActorKind == ActorKind.BattleNpc || ActorKind == ActorKind.EventNpc;

    /// <summary>
    /// Gets the world position of this actor from game memory.
    /// </summary>
    public unsafe Vector3 Position
    {
        get
        {
            if (Address == nint.Zero)
                return Vector3.Zero;

            var gameObject = (GameObject*)Address;
            return gameObject->Position;
        }
    }

    /// <summary>
    /// Gets the rotation of this actor from game memory.
    /// </summary>
    public unsafe Quaternion Rotation
    {
        get
        {
            if (Address == nint.Zero)
                return Quaternion.Identity;

            var gameObject = (GameObject*)Address;
            // GameObject.Rotation is a float (Y rotation), we need to convert to quaternion
            return Quaternion.CreateFromAxisAngle(Vector3.UnitY, gameObject->Rotation);
        }
    }
}
