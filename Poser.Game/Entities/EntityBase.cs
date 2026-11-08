using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;

namespace Poser.Game.Entities;

public abstract class EntityBase : IEntity, IDisposable
{
    public EntityId Id { get; }
    public virtual string Name { get; set; }
    public virtual Transform Transform { get; set; }

    public IEntity? Parent { get; private set; }

    private readonly List<IEntity> _children = new();

    /// <summary>Cached read-only view of the live child list.</summary>
    private readonly ReadOnlyCollection<IEntity> _childrenView;

    public IReadOnlyCollection<IEntity> Children => _childrenView;

    public bool IsVisible { get; set; } = true;

    protected EntityBase(EntityId id, string name)
    {
        Id = id;
        Name = name;
        Transform = Transform.Identity;
        _childrenView = _children.AsReadOnly();
    }

    public void AttachChild(IEntity child)
    {
        if (child is EntityBase entityBase)
        {
            if (entityBase.Parent is EntityBase oldParent)
            {
                oldParent._children.Remove(child);
                entityBase.OnDetached();
            }

            entityBase.Parent = this;
            _children.Add(child);
            entityBase.OnAttached();
        }
    }

    public void DetachChild(IEntity child)
    {
        if (child is EntityBase entityBase && _children.Contains(child))
        {
            _children.Remove(child);
            entityBase.Parent = null;
            entityBase.OnDetached();
        }
    }

    public virtual void OnAttached() { }

    public virtual void OnDetached() { }

    public virtual void Dispose()
    {
        foreach (var child in _children.ToArray())
        {
            if (child is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        _children.Clear();

        GC.SuppressFinalize(this);
    }
}
