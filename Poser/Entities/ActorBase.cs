using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Poser.Core;

namespace Poser.Entities;

public class ActorBase : EntityBase, IActor
{
    public nint Address { get; }
    public bool IsPosing { get; private set; }

    public ActorBase(EntityId id, string name, nint address)
        : base(id, name)
    {
        Address = address;
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

    public ActorBase(string name, nint address)
        : this(EntityId.New(), name, address)
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
