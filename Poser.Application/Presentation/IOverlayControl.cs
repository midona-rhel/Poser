using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Presentation;

namespace Poser.Application.Presentation;

public sealed record OverlayReading(OverlayId Id, OverlayNodeState State);

/// <summary>Detached overlay values and exact-generation editor commands.</summary>
public interface IOverlayControl
{
    OverlayReading? Read(OverlayId id);
    void Seal();
    ValueWriteResult Set<T>(OverlayId id, EntityProperty<OverlayId, T> property, T value);
    ValueWriteResult Update<T>(OverlayId id, EntityProperty<OverlayId, T> property, Func<T, T> change);

    /// <summary>Changes the overlay's current collider; refuses an overlay
    /// that has none.</summary>
    ValueWriteResult EditCollider(OverlayId id, Func<IkCollider, IkCollider> change);
}
