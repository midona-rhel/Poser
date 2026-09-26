using Poser.Application.Scene;
using Poser.Config;
using Poser.Domain.Identity;
using Poser.Files;
using Poser.Library;

namespace Poser.Application.Library;

public interface ILibrarySceneSave
{
    SceneActionResult SaveEntry(SelectionId target, string name);
    SceneActionResult SaveGroup(IReadOnlyList<SelectionId> members, string name);
    SceneActionResult SaveScene(string name, SceneSaveOptions options);
}

/// <summary>One library-save admission policy; Documents allocates paths and SceneWorkflow owns capture/write.</summary>
public sealed class LibrarySceneSave(ISceneWorkflow workflow, SceneSession scene,
    ConfigurationService config, IPoseLibraryService library, Action<string> reportNotice) : ILibrarySceneSave
{
    public SceneActionResult SaveEntry(SelectionId target, string name)
    {
        if (scene.Resolve(target) != target) return SceneActionResult.Fail("The entity is no longer available.");
        (SceneSaveOptions Options, string Extension)? entry = target switch
        {
            { Kind: SceneEntityKind.Actor, Actor: { } actor } =>
                (SceneSaveOptions.ActorEntry(actor.LogicalId), SceneFile.ActorEntryExtension),
            { Kind: SceneEntityKind.Light, Light: { } light } =>
                (SceneSaveOptions.LightEntry(light.LogicalId), SceneFile.LightEntryExtension),
            { Kind: SceneEntityKind.Camera, Camera: { } camera } =>
                (SceneSaveOptions.CameraEntry(camera.LogicalId), SceneFile.CameraEntryExtension),
            { Kind: SceneEntityKind.Prop, Prop: { } prop } =>
                (SceneSaveOptions.PropEntry(prop.LogicalId), SceneFile.PropEntryExtension),
            { Kind: SceneEntityKind.WorldObject, WorldObject: { } world } =>
                (SceneSaveOptions.WorldObjectEntry(world.LogicalId), SceneFile.WorldObjectEntryExtension),
            { Kind: SceneEntityKind.Overlay, Overlay: { } overlay } =>
                (SceneSaveOptions.OverlayEntry(overlay.LogicalId), SceneFile.OverlayEntryExtension),
            _ => null,
        };
        if (entry == null) return SceneActionResult.Fail("This selection cannot be saved as an entity.");
        if (target.Actor is { } id && scene.Snapshot.FindActor(id) is not { IsOwned: true })
            return SceneActionResult.Fail("Only an actor you spawned or your own character can be saved to the library.");
        return Save(config.Config.Library.ResolveObjectsRoot(), name, entry.Value.Extension,
            entry.Value.Options with { EntryName = name });
    }

    public SceneActionResult SaveGroup(IReadOnlyList<SelectionId> members, string name)
    {
        if (members.Any(member => scene.Resolve(member) != member))
            return SceneActionResult.Fail("A group member is no longer available.");
        var keys = members.Select(member => member.Actor?.LogicalId ?? member.Prop?.LogicalId
            ?? member.WorldObject?.LogicalId ?? member.Light?.LogicalId ?? member.Camera?.LogicalId
            ?? member.Overlay?.LogicalId).OfType<Guid>().Distinct().ToArray();
        if (keys.Length < 2) return SceneActionResult.Fail("The group needs at least two members to save.");
        bool owned = members.All(member => member.Actor is not { } actor
            || scene.Snapshot.FindActor(actor) is { IsOwned: true });
        if (!owned) reportNotice("The group holds an actor that is not yours; it is saved without appearance.");
        return Save(config.Config.Library.ResolveObjectsRoot(), name, SceneFile.GroupEntryExtension,
            SceneSaveOptions.GroupEntry(keys) with { EntryName = name, IncludeModdedAppearance = owned });
    }

    public SceneActionResult SaveScene(string name, SceneSaveOptions options) =>
        Save(config.Config.Library.ResolveSceneRoot(), name, SceneFile.Extension, options);

    private SceneActionResult Save(string root, string name, string extension, SceneSaveOptions options)
    {
        if (!LibraryConfiguration.TryEnsureDirectory(root, out var detail))
        {
            library.RequestScan();
            return SceneActionResult.Fail(detail);
        }
        var path = LibraryConfiguration.NewEntryPath(root, name, extension);
        return workflow.BeginSave(path, null, options);
    }
}
