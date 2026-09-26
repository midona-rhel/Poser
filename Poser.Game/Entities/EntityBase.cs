using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Poser.Core;

namespace Poser.Entities;

public abstract class EntityBase : IEntity, IDisposable
{
    public EntityId Id { get; }
    public virtual string Name { get; set; }
    public virtual Transform Transform { get; set; }

    public IEntity? Parent { get; private set; }

    private readonly List<IEntity> _children = new();

    /// <summary>Live read-only view over <c>_children</c>, allocated once.
    /// <c>List.AsReadOnly()</c> wraps the SAME list, so one wrapper stays
    /// correct for the entity's whole life — building a fresh one per access
    /// only added garbage to paths that read Children (and IsCollapsible) per
    /// entity per frame.</summary>
    private readonly ReadOnlyCollection<IEntity> _childrenView;

    public IReadOnlyCollection<IEntity> Children => _childrenView;

    public bool IsVisible { get; set; } = true;
    public bool IsSelected { get; set; }

    /// <summary>
    /// Override in derived classes to indicate if this entity can be collapsed.
    /// Default returns true if entity has children.
    /// </summary>
    public virtual bool IsCollapsible => Children.Count > 0;

    /// <summary>
    /// Whether this entity is currently collapsed in the UI.
    /// </summary>
    public bool IsCollapsed { get; set; }

    /// <summary>
    /// Override in derived classes to return the entity type.
    /// </summary>
    public virtual EntityType EntityType => EntityType.Generic;

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
