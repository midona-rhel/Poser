using Poser.Documents.Files;

namespace Poser.Application.Scene;

/// <summary>
/// How a loaded scene gives each actor its look.
///
/// An MCDF is an appearance that never changes: it is never edited. A scene is a project
/// you save, load and keep working on, so saving and loading it never locks an
/// appearance you own:
///
/// <list type="bullet">
/// <item>An actor that had an MCDF applied when the scene was saved gets that MCDF
/// again, and its appearance can't be edited.</item>
/// <item>Every other actor gets its scene appearance applied again and stays editable, on
/// the install that saved the scene.</item>
/// <item>A scene saved by another install (one someone shared with you) has none of that
/// install's mods, so its scene appearances load as MCDFs, locked.</item>
/// </list>
///
/// Older scene files do not record who saved them or which saved looks were MCDFs. They count as yours; see <see cref="SceneFile.HasLoadedMcdf"/> for how
/// their MCDFs are told apart.
/// </summary>
public static class SceneAppearanceRule
{
    public const string PackedPrefix = SceneFile.PackedAppearancePrefix;

    /// <summary>True when the actor's saved look should be applied editable: it is a
    /// scene appearance, and this install saved the scene.</summary>
    public static bool LoadsEditable(SceneFile scene, SceneActor actor, Guid thisInstall)
    {
        if (actor.Mcdf is null)
            return false;
        if (scene.SavedBy is { } savedBy && savedBy != thisInstall)
            return false;
        return !scene.HasLoadedMcdf(actor);
    }
}
