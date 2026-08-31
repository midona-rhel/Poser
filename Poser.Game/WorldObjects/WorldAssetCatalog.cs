using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace Poser.Game.WorldObjects;

/// <summary>
/// One spawnable game asset: the label a person searches by, and the path
/// the spawn takes.
/// </summary>
public sealed record WorldAsset(string Name, string Path);

/// <summary>
/// Every spawnable world asset the game data holds, by path list: the BG
/// models and the world effects. The lists are the community path dump both
/// references ship (Stagehand embeds the same <c>paths.json</c>; Brio packs
/// the same dump as its path store) — the game's own index carries only
/// hashes, so a bundled list is the ONE way to browse it by name.
///
/// <para>Loaded lazily and once: a session that never opens a picker never
/// touches the resources. The label is the file's own stem — asset codes
/// are opaque, and the full path stays searchable and shown as the row's
/// badge context elsewhere.</para>
/// </summary>
public sealed class WorldAssetCatalog
{
    private const string ModelsResource =
        "Poser.Game.Data.WorldModelPaths.txt.gz";

    private const string EffectsResource = "Poser.Game.Data.VfxPaths.txt.gz";

    private IReadOnlyList<WorldAsset>? _models;
    private IReadOnlyList<WorldAsset>? _effects;

    /// <summary>Every spawnable BG model path in the game data.</summary>
    public IReadOnlyList<WorldAsset> Models => _models ??= Load(ModelsResource);

    /// <summary>Every world effect (.avfx) path in the game data.</summary>
    public IReadOnlyList<WorldAsset> Effects =>
        _effects ??= Load(EffectsResource);

    private static IReadOnlyList<WorldAsset> Load(string resource)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(resource);
            if (stream == null)
                return Array.Empty<WorldAsset>();
            using var unzipped = new GZipStream(
                stream, CompressionMode.Decompress);
            using var reader = new StreamReader(unzipped);
            var assets = new List<WorldAsset>(120_000);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0)
                    continue;
                assets.Add(new WorldAsset(
                    System.IO.Path.GetFileNameWithoutExtension(line), line));
            }
            return assets;
        }
        catch (Exception)
        {
            return Array.Empty<WorldAsset>();
        }
    }
}
