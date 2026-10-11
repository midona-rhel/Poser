using Poser.Domain.Identity;

namespace Poser.Application.Presentation;

/// <summary>Every value a surface sets on a prop. A model change respawns
/// the prop, so it is a command on <see cref="ISceneObjectControl"/>.</summary>
public static class PropProperties
{
    public static readonly EntityProperty<PropId, string> Name = new("Name", "Rename prop");
    public static readonly EntityProperty<PropId, bool> Visible = new("Visible", on => on ? "Show prop" : "Hide prop");

    public static readonly IReadOnlyList<EntityProperty> All = [Name, Visible];
}
