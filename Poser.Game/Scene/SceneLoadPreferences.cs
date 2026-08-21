namespace Poser.Game.Scene;

/// <summary>
/// What the user has told the next scene load to do, held ONCE for the whole
/// session. It is a preference and not state of any operation, which is why it
/// does not live on <see cref="SceneWorkflow"/>; it is shared and not per-pane,
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
