using Poser.Scene;
using System.Collections.Generic;
using Poser.Files;

namespace Poser.Game.Scene;

/// <summary>
/// What the save OPTIONS do to a captured document, applied once between the
/// capture and the write. The capture always takes everything it can see —
/// there is one capture, not six — and this narrows it, so a category the user
/// excluded is absent from the file rather than captured and ignored on load.
///
/// <para>Appearance is the case with teeth. With the consent switch off, every
/// appearance entry is REMOVED, including the reference form: a scene that
/// names another player's package path is appearance data whether or not it
/// carries the bytes. With it on, only a PORTABLE entry survives — a reference
/// the sealing step could not turn into bytes is dropped with a note, because
/// "saved as portable" must not be a scene that only knows where the mods used
/// to be.</para>
/// </summary>
internal static class SceneSavePolicy
{
    /// <summary>Applies the options, and answers how many actors asked for a
    /// portable appearance and did not get one. A save that quietly drops what
    /// the user explicitly asked for must not report plain success — that is
    /// the shape of the defect where an oversized package vanished and the
    /// load then had nothing to apply.</summary>
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
        {
            scene.Overlays = null;
            scene.WorldObjects = null;
        }

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
            // Consent was given and the payload could not be sealed. The
            // sealing step already said WHY, by actor; dropping the leftover
            // reference here is what stops a "portable" save from shipping a
            // path to a machine that has never seen it.
            actor.Mcdf = null;
            unsealed++;
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
}
