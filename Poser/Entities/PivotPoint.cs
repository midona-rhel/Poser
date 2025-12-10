using System.Numerics;
using Poser.Core;
using Poser.Entities.Capabilities;

namespace Poser.Entities;

/// <summary>
/// A pivot point entity that can be used as an orbit center for bone rotations.
/// Can be parented to a bone to follow its movement.
/// </summary>
public class PivotPoint : EntityBase, ITransformable
{
    private Vector3 _localPosition;
    private IBone? _parentBone;

    /// <summary>
    /// The bone this pivot point is parented to (if any).
    /// When parented, the pivot follows the bone's position plus LocalOffset.
    /// </summary>
    public IBone? ParentBone
    {
        get => _parentBone;
        set
        {
            if (_parentBone == value)
                return;

            _parentBone = value;

            // When parenting, capture current world position as offset from new parent
            if (_parentBone != null)
            {
                _localPosition = WorldPosition - _parentBone.LastTransform.Position;
            }
        }
    }

    /// <summary>
    /// Local offset from parent bone (when parented) or world position (when not parented).
    /// </summary>
    public Vector3 LocalPosition
    {
        get => _localPosition;
        set => _localPosition = value;
    }

    /// <summary>
    /// The effective world position of this pivot point.
    /// If parented, returns parent position + local offset.
    /// If not parented, returns the local position directly.
    /// </summary>
    public Vector3 WorldPosition
    {
        get
        {
            if (_parentBone != null)
                return _parentBone.LastTransform.Position + _localPosition;
            return _localPosition;
        }
        set
        {
            if (_parentBone != null)
                _localPosition = value - _parentBone.LastTransform.Position;
            else
                _localPosition = value;
        }
    }

    /// <summary>
    /// Gets or sets the transform. Position is the world position.
    /// </summary>
    public override Transform Transform
    {
        get => new Transform(WorldPosition, Quaternion.Identity, Vector3.One);
        set => WorldPosition = value.Position;
    }

    /// <summary>
    /// Pivot points show gizmo when selected.
    /// </summary>
    public bool ShowGizmo => IsSelected;

    /// <summary>
    /// Pivot points cannot be collapsed.
    /// </summary>
    public override bool IsCollapsible => false;

    /// <summary>
    /// Entity type for UI.
    /// </summary>
    public override EntityType EntityType => EntityType.PivotPoint;

    public PivotPoint(Vector3 position, string name = "Pivot Point")
        : base(EntityId.New(), name)
    {
        _localPosition = position;
        IsVisible = true;
    }

    /// <summary>
    /// Creates a pivot point at a bone's position, optionally parented to it.
    /// </summary>
    public static PivotPoint AtBone(IBone bone, bool parented = true, string? name = null)
    {
        var pivot = new PivotPoint(bone.LastTransform.Position, name ?? $"Pivot ({bone.BoneName})");
        if (parented)
        {
            pivot.ParentBone = bone;
            pivot._localPosition = Vector3.Zero; // Exactly at bone position
        }
        return pivot;
    }
}
