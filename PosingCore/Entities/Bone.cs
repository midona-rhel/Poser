using System;
using System.Collections.Generic;
using Poser.Core;
using Poser.Core.BoneInfo;

namespace Poser.Entities;

/// <summary>
/// Represents a bone in a skeleton hierarchy.
/// </summary>
public class Bone : EntityBase, IBone
{
    private readonly List<IBone> _childBones = new();

    public int BoneIndex { get; }
    public string BoneName { get; }
    public int PartialId { get; }
    public IBone? ParentBone { get; internal set; }
    public IReadOnlyList<IBone> ChildBones => _childBones.AsReadOnly();
    public ISkeleton Skeleton { get; }
    public bool IsPartialRoot { get; internal set; }
    public bool IsSkeletonRoot { get; internal set; }
    public Transform LastTransform { get; internal set; } = Transform.Identity;

    /// <summary>
    /// Current Havok model-space baseline captured during the Brio-style apply/cache
    /// pipeline. Absolute editor and import targets calculate deltas against it.
    /// Persistent posing state remains in BonePoseInfo, not this observation cache.
    /// </summary>
    public Transform LastRawTransform { get; internal set; } = Transform.Identity;

    #region ITransformable

    /// <summary>
    /// Gets the current transform of this bone (from LastTransform cache).
    /// </summary>
    public override Transform Transform
    {
        get => LastTransform;
        set => LastTransform = value;
    }

    /// <summary>
    /// Bones show gizmo when visible.
    /// </summary>
    public bool ShowGizmo => IsVisible;

    /// <summary>
    /// Bone transforms can be set directly (updates LastTransform cache).
    /// </summary>
    public bool CanSetTransform => true;

    #endregion

    /// <summary>
    /// Bones are collapsible if they have children.
    /// </summary>
    public override bool IsCollapsible => _childBones.Count > 0;

    /// <summary>
    /// Entity type is Bone.
    /// </summary>
    public override EntityType EntityType => EntityType.Bone;

    /// <summary>
    /// Gets the display name with translation: "Translation (internal_name)" or just "internal_name".
    /// </summary>
    public override string Name => BoneInfoService.GetDisplayName(BoneName);

    /// <summary>
    /// Gets the category for this bone.
    /// </summary>
    public BoneCategory Category => BoneInfoService.GetCategory(BoneName);

    /// <summary>
    /// Whether this bone should be hidden in the UI.
    /// Hidden bones include partial roots (except skeleton root when attached).
    /// </summary>
    public bool IsHiddenBone
    {
        get
        {
            // Partial roots are generally hidden (they're structural, not user-facing)
            if (IsPartialRoot && !IsSkeletonRoot)
                return true;

            return false;
        }
    }

    public Bone(ISkeleton skeleton, int partialId, int boneIndex, string boneName)
        : base(EntityId.New(), boneName)
    {
        Skeleton = skeleton;
        PartialId = partialId;
        BoneIndex = boneIndex;
        BoneName = boneName;

        // Collapsed by default (tree semantics). VISIBLE by default — the
        // overlay must show the skeleton out of the box (Ktisis/Brio parity);
        // hiding is the opt-out filter, and the legacy tree that used to flip
        // visibility on is gone. IsHiddenBone still filters curated junk bones.
        IsCollapsed = true;
        IsVisible = true;
    }

    /// <summary>
    /// Adds a child bone to this bone.
    /// </summary>
    internal void AddChildBone(Bone? child)
    {
        if (child == null)
            return;

        if (!_childBones.Contains(child))
        {
            _childBones.Add(child);
            child.ParentBone = this;
            AttachChild(child);
        }
    }

    /// <summary>
    /// Gets a friendly display name for the bone (translation only, no internal name).
    /// </summary>
    public string GetFriendlyName()
    {
        return BoneInfoService.GetTranslation(BoneName) ?? BoneName;
    }
}
