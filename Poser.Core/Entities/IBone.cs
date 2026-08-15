using System.Collections.Generic;
using Poser.Core;
using Poser.Entities.Capabilities;

namespace Poser.Entities;

/// <summary>
/// Represents a bone in a skeleton hierarchy.
/// Extends ITransformable for compile-time type checking.
/// </summary>
public interface IBone : IEntity, ITransformable
{
    /// <summary>
    /// The index of this bone within its partial skeleton.
    /// </summary>
    int BoneIndex { get; }

    /// <summary>
    /// The internal name of the bone (e.g., "j_kosi", "j_te_r").
    /// </summary>
    string BoneName { get; }

    /// <summary>
    /// The partial skeleton ID this bone belongs to.
    /// </summary>
    int PartialId { get; }

    /// <summary>
    /// The parent bone, if any.
    /// </summary>
    IBone? ParentBone { get; }

    /// <summary>
    /// Child bones.
    /// </summary>
    IReadOnlyList<IBone> ChildBones { get; }

    /// <summary>
    /// The skeleton this bone belongs to.
    /// </summary>
    ISkeleton Skeleton { get; }

    /// <summary>
    /// Whether this is a root bone of a partial skeleton.
    /// </summary>
    bool IsPartialRoot { get; }

    /// <summary>
    /// Whether this is the root bone of the entire skeleton.
    /// </summary>
    bool IsSkeletonRoot { get; }

    /// <summary>
    /// Whether this bone should be hidden in the UI.
    /// </summary>
    bool IsHiddenBone { get; }

    /// <summary>
    /// The last cached transform of this bone (after partial reparenting).
    /// </summary>
    Transform LastTransform { get; }

    /// <summary>
    /// Current Havok model-space baseline captured by the apply/cache pipeline.
    /// Used when an absolute editor or import target must be converted to a pose delta.
    /// </summary>
    Transform LastRawTransform { get; }
}
