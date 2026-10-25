namespace Poser.Application.Scene;

/// <summary>
/// What the user has told the next scene load to do, held ONCE for the whole
/// session. It is a preference and not state of any operation, which is why it
/// does not live on <see cref="ISceneWorkflow"/>; it is shared and not per-pane,
/// because Poser starts a load from more than one surface — the scene
/// workspace's dialogs and the library's scene tiles — and a preference the
/// user set on one of them that the other quietly ignored would be worse than
/// having no preference at all.
///
/// <para>Both references hold it the same way: Brio's import options are
/// configuration (<c>SceneDestoryActorsBeforeImport</c>), read by the import
/// wherever it starts, and Ktisis's per-category checkboxes live on the one
/// scene editor that owns every load it has. The band that EDITS this stands
/// under the load dialog, which is the only surface that shows a listing to
/// choose from.</para>
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

    /// <summary>Defaults to the load Poser had before options existed.
    /// </summary>
    public SceneLoadOptions Options { get; set; } = SceneLoadOptions.Default;

    /// <summary>What the NEXT save is asked to include. It lives beside the
    /// load's answer for the same reason: the Inspector and the save dialog
    /// are two mounts of ONE answer, not two answers.
    ///
    /// <para>Deliberately session-scoped and never written to configuration.
    /// The appearance switch is consent to package somebody's mods, and a
    /// switch remembered across sessions is consent inferred from a previous
    /// scene, which the issue forbids by name.</para>
    /// </summary>
    public SceneSaveOptions SaveOptions { get; set; } = SceneSaveOptions.Default;
}
