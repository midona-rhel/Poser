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

    /// <summary>Live view over <c>_childBones</c>, allocated once — see the
    /// same pattern on EntityBase.Children.</summary>
    private readonly System.Collections.ObjectModel.ReadOnlyCollection<IBone> _childBonesView;

    public int BoneIndex { get; }
    public string BoneName { get; }
    public int PartialId { get; }
    public IBone? ParentBone { get; internal set; }
    public IReadOnlyList<IBone> ChildBones => _childBonesView;
    public ISkeleton Skeleton { get; }
    public bool IsPartialRoot { get; internal set; }
    public bool IsSkeletonRoot { get; internal set; }
    private Transform _lastTransform = Transform.Identity;
    private long _transformReadFrame;

    /// <summary>
    /// The finalize hook's last snapshot of this bone. READING it is the
    /// demand signal: the hook copies only bones read within the last couple
    /// of frames, so every consumer pays for exactly the bones it displays —
    /// a skeleton is hundreds of bones and an overlay mask shows dozens.
    /// </summary>
    public Transform LastTransform
    {
        get
        {
            System.Threading.Volatile.Write(
                ref _transformReadFrame, BoneReadClock.Frame);
            BoneReadClock.MarkRead();
            return _lastTransform;
        }
        internal set => _lastTransform = value;
    }

    /// <summary>Whether a reader touched this bone recently enough for the
    /// hook to keep its snapshot fresh.</summary>
    public bool TransformWanted =>
        BoneReadClock.Frame -
            System.Threading.Volatile.Read(ref _transformReadFrame) <= 2;

    /// <summary>
    /// Current Havok model-space baseline captured during the Brio-style apply/cache
    /// pipeline. Absolute editor and import targets calculate deltas against it.
    /// Persistent posing state remains in BonePoseInfo, not this observation cache.
    /// </summary>
    public Transform LastRawTransform { get; internal set; } = Transform.Identity;

    public System.Numerics.Vector3? PartialRootScale { get; set; }

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
    /// The TRANSLATION is resolved once at construction: the bone tables are
    /// immutable after BoneInfoService.Initialize (which runs before the
    /// plugin's DI container exists, so no bone can be built ahead of them),
    /// while this property is read per bone per frame by every tree, overlay
    /// and descriptor rebuild.
    ///
    /// <para>WHICH of the two names is handed back is a live read of a static
    /// field (Ktisis' <c>ShowFriendlyBoneNames</c>), because the switch has to
    /// take effect on the next frame rather than the next skeleton rebuild.
    /// </para>
    /// </summary>
    public override string Name =>
        BoneInfoService.ShowFriendlyNames ? _displayName : BoneName;

    /// <summary>
    /// Gets the category for this bone.
    /// </summary>
    public BoneCategory Category => _category;

    private readonly string _displayName;
    private readonly BoneCategory _category;

    /// <summary>
    /// The legacy-dedupe and race-feature verdicts, decided once per skeleton
    /// rebuild by <see cref="Skeleton.ApplyBoneFilters"/>. They are stored
    /// rather than computed here because both need facts about the WHOLE
    /// skeleton (is there a modern jaw) or about the actor (which ear set),
    /// and <see cref="IsHiddenBone"/> is read per bone per frame.
    /// </summary>
    internal bool FilteredOut { get; set; }

    /// <summary>
    /// Whether this bone should be hidden in the UI.
    /// Hidden bones include partial roots (except skeleton root when attached),
    /// the superseded jaw, and the three Viera ear sets the character does not
    /// wear.
    /// </summary>
    public bool IsHiddenBone
    {
        get
        {
            // Partial roots are generally hidden (they're structural, not user-facing)
            if (IsPartialRoot && !IsSkeletonRoot)
                return true;

            return FilteredOut;
        }
    }

    public Bone(ISkeleton skeleton, int partialId, int boneIndex, string boneName)
        : base(EntityId.New(), boneName)
    {
        Skeleton = skeleton;
        PartialId = partialId;
        BoneIndex = boneIndex;
        BoneName = boneName;
        _childBonesView = _childBones.AsReadOnly();
        _displayName = BoneInfoService.GetDisplayName(boneName);
        _category = BoneInfoService.GetCategory(boneName);

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
