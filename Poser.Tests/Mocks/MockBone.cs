using System.Collections.Generic;
using System.Numerics;
using Poser.Core;
using Poser.Entities;

namespace Poser.Tests.Mocks;

public class MockBone : IBone
{
    private readonly List<IEntity> _children = new();
    private readonly List<IBone> _childBones = new();

    public MockBone(string name, int boneIndex = 0, int partialId = 0)
    {
        Id = EntityId.New();
        Name = name;
        BoneName = name;
        BoneIndex = boneIndex;
        PartialId = partialId;
        LastTransform = new Transform
        {
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            Scale = Vector3.One
        };
    }

    public EntityId Id { get; }
    public string Name { get; set; }
    public Transform Transform { get; set; } = new();

    public IEntity? Parent { get; set; }
    public IReadOnlyCollection<IEntity> Children => _children.AsReadOnly();

    public bool IsVisible { get; set; } = true;
    public bool IsSelected { get; set; }
    public bool IsCollapsible => _childBones.Count > 0;
    public bool IsCollapsed { get; set; }
    public EntityType EntityType => EntityType.Bone;

    public int BoneIndex { get; }
    public string BoneName { get; }
    public int PartialId { get; }
    public IBone? ParentBone { get; set; }
    public IReadOnlyList<IBone> ChildBones => _childBones.AsReadOnly();
    public ISkeleton Skeleton { get; set; } = null!;
    public bool IsPartialRoot { get; set; }
    public bool IsSkeletonRoot { get; set; }
    public bool IsHiddenBone { get; set; }
    public Transform LastTransform { get; set; }

    public void AttachChild(IEntity child)
    {
        _children.Add(child);
        if (child is IBone bone)
        {
            _childBones.Add(bone);
        }
    }

    public void DetachChild(IEntity child)
    {
        _children.Remove(child);
        if (child is IBone bone)
        {
            _childBones.Remove(bone);
        }
    }

    public void OnAttached() { }
    public void OnDetached() { }
    public void OnSelected() { }
    public void OnDeselected() { }
}
