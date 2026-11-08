namespace Poser.Application.Scene;

/// <summary>
/// Session-wide save and load choices shared by scene dialogs and library entries.
/// Placement choices are stored per path; ForLoad creates options for one operation.
/// </summary>
public sealed class SceneLoadPreferences
{
    private readonly Dictionary<string, Poser.Domain.Scene.ObjectPlacementMode> _placements = new(StringComparer.OrdinalIgnoreCase);

    public void SetPlacement(string path, Poser.Domain.Scene.ObjectPlacementMode mode) => _placements[path] = mode;

    public Poser.Domain.Scene.ObjectPlacementMode PlacementFor(string path, uint savedTerritory, uint currentTerritory,
        Poser.Domain.Scene.ObjectPlacementMode fallback) =>
        _placements.TryGetValue(path, out var chosen) ? chosen :
        savedTerritory != 0 && savedTerritory == currentTerritory ? Poser.Domain.Scene.ObjectPlacementMode.AsSaved : fallback;

    public SceneLoadOptions ForLoad(string path, uint currentTerritory, Poser.Domain.Scene.ObjectPlacementMode fallback)
    {
        var explicitlyChosen = _placements.TryGetValue(path, out var chosen);
        return Options with
        {
            Placement = explicitlyChosen ? chosen : fallback,
            PlaceRelativeToCurrentOrigin = false,
            DefaultPlacementTerritory = explicitlyChosen ? 0 : currentTerritory,
        };
    }

    public SceneLoadOptions ForLoad(string path, uint currentTerritory,
        Poser.Domain.Scene.ObjectPlacementMode fallback, IPlacementAnchorSource anchors)
    {
        var options = ForLoad(path, currentTerritory, fallback);
        return options.Placement != Poser.Domain.Scene.ObjectPlacementMode.AsSaved &&
            anchors.TryCurrentFor(options.Placement, out var position, out var yaw, out _)
            ? options with { PlacementPosition = position, PlacementYaw = yaw }
            : options with { Placement = Poser.Domain.Scene.ObjectPlacementMode.AsSaved };
    }

    public static SceneLoadOptions ResolveDefault(SceneLoadOptions options, uint savedTerritory) =>
        options.DefaultPlacementTerritory != 0 && options.DefaultPlacementTerritory == savedTerritory
            ? options with { Placement = Poser.Domain.Scene.ObjectPlacementMode.AsSaved, PlaceRelativeToCurrentOrigin = false, DefaultPlacementTerritory = 0 }
            : options with { DefaultPlacementTerritory = 0 };

    /// <summary>Options used as the starting point for the next load.</summary>
    public SceneLoadOptions Options { get; set; } = SceneLoadOptions.Default;

    /// <summary>Options shared by the Inspector and save dialog for the next save.
    /// These are not persisted to configuration, so permission to package modded
    /// appearance is not carried into a later session.
    /// </summary>
    public SceneSaveOptions SaveOptions { get; set; } = SceneSaveOptions.Default;
}
