using System.Collections.Generic;
using Poser.Core;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;

namespace Poser.Entities;

public interface IEntity
{
    EntityId Id { get; }
    string Name { get; set; }
    Transform Transform { get; set; }

    IEntity? Parent { get; }
    IReadOnlyCollection<IEntity> Children { get; }

    bool IsVisible { get; set; }

    void AttachChild(IEntity child);
    void DetachChild(IEntity child);

    void OnAttached();
    void OnDetached();
}
