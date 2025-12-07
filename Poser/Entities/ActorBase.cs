using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Poser.Core;

namespace Poser.Entities;

public class ActorBase : EntityBase, IActor
{
    public nint Address { get; }
    public bool IsPosing { get; private set; }
    public bool IsEditMode { get; set; }
    public ActorKind ActorKind { get; }

    public ActorBase(EntityId id, string name, nint address, ActorKind actorKind = ActorKind.None)
        : base(id, name)
    {
        Address = address;
        ActorKind = actorKind;
        IsCollapsed = true; // Start collapsed by default
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
    /// Actors are always collapsible (they will have skeleton children).
    /// </summary>
    public override bool IsCollapsible => true;

    /// <summary>
    /// Returns the entity type based on ObjectKind.
    /// </summary>
    public override EntityType EntityType
    {
        get
        {
            if (IsPlayer) return EntityType.Player;
            if (IsNpc) return EntityType.Npc;
            if (IsCompanion) return EntityType.Companion;
            return EntityType.Generic;
        }
    }

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

    public ActorBase(string name, nint address, ActorKind actorKind = ActorKind.None)
        : this(EntityId.New(), name, address, actorKind)
    {
    }

    public void BeginPosing()
    {
        if (!IsPosing)
        {
            IsPosing = true;
            OnBeginPosing();
        }
    }

    public void EndPosing()
    {
        if (IsPosing)
        {
            IsPosing = false;
            OnEndPosing();
        }
    }

    public virtual void ResetPose()
    {
        // Override in derived classes to implement pose reset
    }

    protected virtual void OnBeginPosing()
    {
        // Override in derived classes for custom behavior
    }

    protected virtual void OnEndPosing()
    {
        // Override in derived classes for custom behavior
    }
}
