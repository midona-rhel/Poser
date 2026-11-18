using Poser.Domain.Identity;

namespace Poser.Application.Presentation;

/// <summary>Every value the interface sets on a prop. Changing the model spawns
/// the prop again, so that is a command on <see cref="ISceneObjectControl"/> instead.</summary>
public static class PropProperties
{
    public static readonly EntityProperty<PropId, string> Name = new("Name", "Rename prop");
    public static readonly EntityProperty<PropId, bool> Visible = new("Visible", on => on ? "Show prop" : "Hide prop");
    public static readonly EntityProperty<PropId, System.Numerics.Vector4> Tint = new("Tint", "Tint prop");

    public static readonly IReadOnlyList<EntityProperty> All = [Name, Visible, Tint];
}
