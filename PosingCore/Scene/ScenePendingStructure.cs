using System;
using System.Collections.Generic;
using Poser.Files;

namespace Poser.Scene;

/// <summary>A completed load's structure — the document's groups and
/// root order plus the file-key → runtime-token map — waiting for the
/// sidebar. The spawned entities bind on the next snapshot publish;
/// the sidebar resolves the tokens then and clears this.</summary>
public sealed class ScenePendingStructure
{
    public required IReadOnlyList<SceneGroupEntry> Groups { get; init; }
    public required IReadOnlyList<SceneStructureRef>? RootOrder { get; init; }
    public required IReadOnlyDictionary<Guid, object> Tokens { get; init; }
}
