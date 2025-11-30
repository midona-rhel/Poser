using System;
using System.Collections.Generic;
using Poser.Core;

namespace Poser.Entities;

public abstract class EntityBase : IEntity, IDisposable
{
    public EntityId Id { get; }
    public virtual string Name { get; set; }
    public virtual Transform Transform { get; set; }

    public IEntity? Parent { get; private set; }

    private readonly List<IEntity> _children = new();
    public IReadOnlyCollection<IEntity> Children => _children.AsReadOnly();

    public bool IsVisible { get; set; } = true;
    public bool IsSelected { get; set; }

    protected EntityBase(EntityId id, string name)
    {
        Id = id;
        Name = name;
        Transform = Transform.Identity;
    }

    public void AttachChild(IEntity child)
    {
        if (child is EntityBase entityBase)
        {
            // Detach from previous parent if any
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

    public virtual void OnSelected()
    {
        IsSelected = true;
    }

    public virtual void OnDeselected()
    {
        IsSelected = false;
    }

    public virtual void Dispose()
    {
        // Dispose all children
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
