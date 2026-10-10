using Poser.Application.Scene;
using Poser.Config;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Files;
using Poser.Library;

namespace Poser.Application.Library;

public interface ILibrarySceneSave
{
    Outcome SaveEntry(SelectionId target, string name);
    /// <summary>The same entry save written to a chosen path — a pane's
    /// "Save to file…". The entity keeps its own name.</summary>
    Outcome SaveEntryTo(SelectionId target, string path);
    Outcome SaveGroup(IReadOnlyList<SelectionId> members, string name);
    Outcome SaveScene(string name, SceneSaveOptions options);
}

/// <summary>One library-save admission policy; Documents allocates paths and SceneWorkflow owns capture/write.</summary>
public sealed class LibrarySceneSave(ISceneWorkflow workflow, SceneSession scene,
    ConfigurationService config, IPoseLibraryService library, Presentation.IUserNotices notices) : ILibrarySceneSave
{
    public Outcome SaveEntry(SelectionId target, string name) =>
        Entry(target, out var options, out var extension) is { } refused
            ? refused
            : Save(config.Config.Library.ResolveObjectsRoot(), name, extension,
                options with { EntryName = name });

    public Outcome SaveEntryTo(SelectionId target, string path) =>
        Entry(target, out var options, out _) is { } refused
            ? refused
            : workflow.BeginSave(path, null, options);

    /// <summary>One entity's entry: its category, its key, its extension.
    /// Returns the refusal, or null with the options filled.</summary>
    private Outcome? Entry(SelectionId target, out SceneSaveOptions options, out string extension)
    {
        options = SceneSaveOptions.Default;
        extension = string.Empty;
        if (scene.Resolve(target) != target) return Outcome.Fail("The entity is no longer available.");
        (SceneCategories Category, Guid Key, string Extension)? entry = target switch
        {
            { Kind: SceneEntityKind.Actor, Actor: { } actor } =>
                (SceneCategories.Actors, actor.LogicalId, SceneFile.ActorEntryExtension),
            { Kind: SceneEntityKind.Light, Light: { } light } =>
                (SceneCategories.Lights, light.LogicalId, SceneFile.LightEntryExtension),
            { Kind: SceneEntityKind.Camera, Camera: { } camera } =>
                (SceneCategories.Cameras, camera.LogicalId, SceneFile.CameraEntryExtension),
            { Kind: SceneEntityKind.Prop, Prop: { } prop } =>
                (SceneCategories.Props, prop.LogicalId, SceneFile.PropEntryExtension),
            { Kind: SceneEntityKind.WorldObject, WorldObject: { } world } =>
                (SceneCategories.WorldObjects, world.LogicalId, SceneFile.WorldObjectEntryExtension),
            { Kind: SceneEntityKind.Overlay, Overlay: { } overlay } =>
                (SceneCategories.Overlays, overlay.LogicalId, SceneFile.OverlayEntryExtension),
            _ => null,
        };
        if (entry is not { } chosen) return Outcome.Fail("This selection cannot be saved as an entity.");
        if (target.Actor is { } id && scene.Snapshot.FindActor(id) is not { IsOwned: true })
            return Outcome.Fail("Only an actor you spawned or your own character can be saved to the library.");
        options = SceneSaveOptions.Only(chosen.Category, new[] { chosen.Key })
            with { IncludeModdedAppearance = chosen.Category == SceneCategories.Actors };
        extension = chosen.Extension;
        return null;
    }

    public Outcome SaveGroup(IReadOnlyList<SelectionId> members, string name)
    {
        if (members.Any(member => scene.Resolve(member) != member))
            return Outcome.Fail("A group member is no longer available.");
        var keys = members.Select(member => member.Actor?.LogicalId ?? member.Prop?.LogicalId
            ?? member.WorldObject?.LogicalId ?? member.Light?.LogicalId ?? member.Camera?.LogicalId
            ?? member.Overlay?.LogicalId).OfType<Guid>().Distinct().ToArray();
        if (keys.Length < 2) return Outcome.Fail("The group needs at least two members to save.");
        bool owned = members.All(member => member.Actor is not { } actor
            || scene.Snapshot.FindActor(actor) is { IsOwned: true });
        if (!owned) notices.Note("The group holds an actor that is not yours; it is saved without appearance.");
        return Save(config.Config.Library.ResolveObjectsRoot(), name, SceneFile.GroupEntryExtension,
            SceneSaveOptions.Only(SceneCategories.All & ~SceneCategories.Environment, keys) with
            {
                IncludeStructure = true, EntryName = name, IncludeModdedAppearance = owned,
            });
    }

    public Outcome SaveScene(string name, SceneSaveOptions options) =>
        Save(config.Config.Library.ResolveSceneRoot(), name, SceneFile.Extension, options);

    private Outcome Save(string root, string name, string extension, SceneSaveOptions options)
    {
        if (!LibraryConfiguration.TryEnsureDirectory(root, out var detail))
        {
            library.RequestScan();
            return Outcome.Fail(detail);
        }
        var path = LibraryConfiguration.NewEntryPath(root, name, extension);
        return workflow.BeginSave(path, null, options);
    }
}
