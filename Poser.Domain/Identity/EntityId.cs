using System;

namespace Poser.Core;

public record struct EntityId(string Unique)
{
    public static EntityId New() => new(Guid.NewGuid().ToString());

    public static implicit operator EntityId(string id) => new(id);

    public override string ToString() => Unique;
}
