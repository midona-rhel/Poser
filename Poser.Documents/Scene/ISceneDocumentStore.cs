using System.Collections.Generic;
using System.IO;
using Poser.Files;

namespace Poser.Scene;

public sealed record SceneDocumentRead(SceneReadOutcome Outcome, IReadOnlyList<string> Notes);
public sealed record SceneDocumentWrite(SceneWriteOutcome Outcome, IReadOnlyList<string> Notes);

/// <summary>Scene persistence, including format conversion and its loss notes.
/// No native execution, framework dispatch or scene mutation authority.</summary>
public interface ISceneDocumentStore
{
    SceneDocumentRead Read(string path);
    SceneDocumentWrite Write(SceneFile scene, string path);

    /// <summary>Opens an embedded appearance payload. The caller owns the stream.</summary>
    Stream? OpenAppearance(string path, string entry);
}
