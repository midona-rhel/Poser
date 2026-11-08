using Poser.Application.Scene;
using System.Collections.Generic;
using Poser.Documents.Files;

namespace Poser.Application.Scene;

/// <summary>
/// Removes excluded categories from a captured document before writing it.
/// Modded appearance is removed unless requested and packaged as embedded data;
/// an unpackaged file reference is removed and reported in the save notes.
/// </summary>
internal static class SceneSavePolicy
{
    /// <summary>Applies category and appearance options. Returns the number of
    /// requested appearance packages removed because they were not portable,
    /// so the caller can report a partial save.</summary>
    public static int Apply(
        SceneFile scene,
        SceneSaveOptions options,
        List<string> notes)
    {
        if (!options.IncludeActors)
            scene.Actors.Clear();
        if (!options.IncludeProps)
            scene.Props.Clear();
        if (!options.IncludeLights)
            scene.Lights.Clear();
        if (!options.IncludeCameras)
            scene.Cameras.Clear();
        if (!options.IncludeEnvironment)
        {
            scene.Environment = null;
            scene.World = null;
        }
        if (!options.IncludeOverlays)
            scene.Overlays = null;
        if (!options.IncludeWorldObjects)
            scene.WorldObjects = null;

        int excluded = 0;
        int unsealed = 0;
        foreach (var actor in scene.Actors)
        {
            if (actor.Mcdf is not { } appearance)
                continue;
            if (!options.IncludeModdedAppearance)
            {
                actor.Mcdf = null;
                excluded++;
                continue;
            }
            if (appearance.IsPortable)
                continue;
            // Remove the file reference when its appearance package could not be built.
            actor.Mcdf = null;
            unsealed++;
        }

        // Update each actor's appearance status after filtering the packages.
        foreach (var actor in scene.Actors)
        {
            if (actor.Mcdf is not null)
                actor.AppearanceNotSaved = false;
            else if (actor.AppearanceNotSaved)
                notes.Add(AppearanceNotSavedNote(actor.Name));
        }

        if (excluded == 1)
            notes.Add("Modded appearance was not saved.");
        else if (excluded > 1)
            notes.Add($"Modded appearance was not saved for {excluded} actors.");

        if (unsealed == 1)
            notes.Add(
                "One actor's appearance could not be packaged, so the scene "
                + "saved without it rather than recording where the mods were.");
        else if (unsealed > 1)
            notes.Add(
                $"{unsealed} actors' appearance could not be packaged, so the "
                + "scene saved without it rather than recording where the mods "
                + "were.");

        return unsealed;
    }

    /// <summary>Explains the appearance fallback when saving and loading an actor.</summary>
    public static string AppearanceNotSavedNote(string actor) =>
        $"Actor '{actor}''s appearance was not saved, so it loads wearing your character's appearance.";
}
