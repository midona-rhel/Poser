using System.Collections.Generic;
using System.IO;
using Poser.Scene;

namespace Poser.Files;

/// <summary>One format-routing store for Poser scenes and Stagehand documents.</summary>
public sealed class SceneDocumentStore : ISceneDocumentStore
{
    private readonly SceneFileStore _scenes;

    public SceneDocumentStore() : this(SceneFileStore.Default) { }
    public SceneDocumentStore(SceneFileStore scenes) => _scenes = scenes;

    public Stream? OpenAppearance(string path, string entry) => _scenes.OpenAppearance(path, entry);

    public SceneDocumentRead Read(string path)
    {
        var notes = new List<string>();
        var outcome = StageFile.IsStagePath(path) ? StageFile.Read(path, notes) : _scenes.Read(path);
        return new(outcome, notes.AsReadOnly());
    }

    public SceneDocumentWrite Write(SceneFile scene, string path)
    {
        var notes = new List<string>();
        var outcome = StageFile.IsStagePath(path) ? StageFile.Write(scene, path, notes) : _scenes.Write(scene, path);
        return new(outcome, notes.AsReadOnly());
    }
}
