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
/// Scenes saved before this rule did not record who saved them or which packed looks
/// were MCDFs. They count as yours, and a packed look named after its actor, or named
/// like Poser's own packing, counts as a scene appearance.
/// </summary>
public static class SceneAppearanceRule
{
    /// <summary>The name Poser gives a look it packs from the actor while saving. Older
    /// scenes kept this name when an earlier load had put the packed look on as an MCDF.</summary>
    public const string PackedPrefix = "poser-scene-appearance-";

    /// <summary>True when the actor's packed look should go back on editable: it is a
    /// scene appearance, and this install saved the scene.</summary>
    public static bool LoadsEditable(SceneFile scene, SceneActor actor, Guid thisInstall)
    {
        if (actor.Mcdf is not { } packed)
            return false;
        if (scene.SavedBy is { } savedBy && savedBy != thisInstall)
            return false;
        bool loadedOnto = scene.SavedBy is null
            ? !PackedByPoser(actor.Name, packed.FileName)
            : packed.LoadedOnto;
        return !loadedOnto;
    }

    private static bool PackedByPoser(string actorName, string fileName) =>
        string.Equals(fileName, actorName + ".mcdf", StringComparison.OrdinalIgnoreCase)
        || fileName.StartsWith(PackedPrefix, StringComparison.OrdinalIgnoreCase);
}
