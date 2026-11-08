using System;
using System.Collections.Generic;
using Poser.Domain.Identity;
using Poser.Domain.Posing.BoneInfo;
using Poser.Domain.Transforms;

namespace Poser.Game.Entities;

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
    /// Current Havok model-space baseline captured during the pose apply/cache
    /// pipeline. Absolute editor and import targets calculate deltas against it.
    /// Persistent posing state remains in BonePoseInfo, not this observation cache.
    /// </summary>
    public Transform LastRawTransform { get; internal set; } = Transform.Identity;

    public System.Numerics.Vector3? PartialRootScale { get; set; }

    /// <summary>
    /// Gets the current transform of this bone (from LastTransform cache).
    /// </summary>
    public override Transform Transform
    {
        get => LastTransform;
        set => LastTransform = value;
    }

    /// <summary>
    /// Uses the translated name cached at construction when friendly names are
    /// enabled. Reads the setting on each access so changing it needs no rebuild.
    /// </summary>
    public override string Name =>
        _showFriendlyNames() ? _displayName : BoneName;

    private readonly string _displayName;
    private readonly Func<bool> _showFriendlyNames;

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
    /// Hidden bones include partial roots other than the skeleton root,
    /// the superseded jaw, and the Viera ear sets the character does not
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

    public Bone(ISkeleton skeleton, int partialId, int boneIndex, string boneName,
        Func<bool> showFriendlyNames)
        : base(EntityId.New(), boneName)
    {
        Skeleton = skeleton;
        PartialId = partialId;
        BoneIndex = boneIndex;
        BoneName = boneName;
        _childBonesView = _childBones.AsReadOnly();
        _displayName = BoneInfoService.GetDisplayName(boneName);
        _showFriendlyNames = showFriendlyNames;

        // Start visible; IsHiddenBone separately excludes structural and filtered bones.
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
}
