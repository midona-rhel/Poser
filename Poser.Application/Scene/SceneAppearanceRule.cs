using Poser.Documents.Files;

namespace Poser.Application.Scene;

/// <summary>
/// How a loaded scene gives each actor its look.
///
/// An MCDF is a promise: the actor looks exactly like the file, and the look cannot be
/// edited. A scene is a project you keep working on. So:
///
/// <list type="bullet">
/// <item>An actor that had an MCDF loaded onto it when the scene was saved gets that
/// MCDF again, locked.</item>
/// <item>Every other actor gets its scene appearance put back and stays editable, on the
/// install that saved the scene.</item>
/// <item>A scene saved by another install (one someone shared with you) has none of that
/// install's mods, so its scene appearances load as MCDFs, locked.</item>
/// </list>
///
/// Scenes saved before this rule did not record who saved them or which saved looks
/// were MCDFs. They count as yours; see <see cref="SceneFile.HasLoadedMcdf"/> for how
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
