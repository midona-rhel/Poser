using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Poser.Core;
using Poser.Entities.Capabilities;

namespace Poser.Entities;

/// <summary>
/// A virtual bone representing a calculated pivot point for a group of bones.
/// Used when selecting bone categories.
/// Position comes from the pivot bone (if set) or average of constituent bones.
/// </summary>
public class VirtualBone : EntityBase, IBone, ITransformable
{
    private readonly List<IBone> _constituentBones;
    private readonly ISkeleton _skeleton;
    private readonly string _boneName;
    private readonly IBone? _pivotBone;

    /// <summary>
    /// Creates a virtual bone from a collection of constituent bones.
    /// </summary>
    /// <param name="name">Display name for this virtual bone.</param>
    /// <param name="skeleton">The skeleton this virtual bone belongs to.</param>
    /// <param name="constituentBones">The real bones this virtual bone represents.</param>
    /// <param name="pivotBone">Optional bone to use for pivot position (e.g., neck for Head category).</param>
    public VirtualBone(string name, ISkeleton skeleton, IEnumerable<IBone> constituentBones, IBone? pivotBone = null)
        : base(EntityId.New(), name)
    {
        _skeleton = skeleton;
        _boneName = name;
        _constituentBones = constituentBones.ToList();
        _pivotBone = pivotBone;
    }

    /// <summary>
    /// Gets the transform dynamically calculated.
    /// If pivot bone is set, uses its position; otherwise averages constituent bones.
    /// </summary>
    public override Transform Transform
    {
        get
        {
            if (_constituentBones.Count == 0)
                return new Transform(Vector3.Zero, Quaternion.Identity, Vector3.One);

            // Use pivot bone if available
            if (_pivotBone != null)
            {
                return new Transform(
                    _pivotBone.LastTransform.Position,
                    _pivotBone.LastTransform.Rotation,
                    Vector3.One);
            }

            // Calculate average position from constituent bones' LastTransform
            var avgPos = Vector3.Zero;
            foreach (var bone in _constituentBones)
            {
                avgPos += bone.LastTransform.Position;
            }
            avgPos /= _constituentBones.Count;

            // Use first bone's rotation as reference orientation
            var rotation = _constituentBones[0].LastTransform.Rotation;

            return new Transform(avgPos, rotation, Vector3.One);
        }
        set
        {
            // Virtual bones don't store transforms - they're calculated
        }
    }

    /// <summary>
    /// The pivot bone used for gizmo position (may be null for averaged categories).
    /// </summary>
    public IBone? PivotBone => _pivotBone;

    /// <summary>
    /// The bones this virtual bone represents.
    /// </summary>
    public IReadOnlyList<IBone> ConstituentBones => _constituentBones.AsReadOnly();

    #region IBone Implementation

    public int BoneIndex => -1; // Indicates virtual
    public string BoneName => _boneName;
    public int PartialId => 0;
    public IBone? ParentBone => null;
    public IReadOnlyList<IBone> ChildBones => _constituentBones;
    public ISkeleton Skeleton => _skeleton;
    public bool IsPartialRoot => false;
    public bool IsSkeletonRoot => false;
    public bool IsHiddenBone => false;
    public Transform LastTransform => Transform;
    public Transform LastRawTransform => Transform; // Virtual bones don't have reparenting

    #endregion

    #region ITransformable Implementation

    public bool ShowGizmo => IsVisible;

    #endregion

    #region EntityBase Overrides

    public override EntityType EntityType => EntityType.VirtualBone;
    public override bool IsCollapsible => false;

    #endregion
}
