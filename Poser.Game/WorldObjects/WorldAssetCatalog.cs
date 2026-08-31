using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace Poser.Game.WorldObjects;

/// <summary>
/// One spawnable game asset: the path the spawn takes, the LABEL a person
/// searches by — Brio's derived naming, "Type [stem]" — and the context
/// line (expansion · subtype) the picker badges.
/// </summary>
public sealed record WorldAsset(
    string Name, string Path, string Label, string Context);

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

    // ── Brio's derived naming (PathDatabase.PathIndex), ported whole:
    // the expansion, subtype and asset-type token maps that turn an
    // opaque asset code into "Rock [r2f0_rok01a]" with a context line.
    // The maps are facts about the game's own naming scheme.

    private static readonly Dictionary<string, string> ExpansionNames =
        new(StringComparer.Ordinal)
    {
        ["ffxiv"] = "Realm Reborn",
        ["ex1"] = "Heavensward",
        ["ex2"] = "Stormblood",
        ["ex3"] = "Shadowbringers",
        ["ex4"] = "Endwalker",
        ["ex5"] = "Dawntrail",
    };

    private static readonly Dictionary<string, string> SubtypeNames =
        new(StringComparer.Ordinal)
    {
        ["fld"] = "Field", ["dun"] = "Dungeon", ["twn"] = "Town",
        ["rad"] = "Raid", ["evt"] = "Event", ["cnt"] = "Content",
        ["btl"] = "Nier", ["alx"] = "Alexander", ["bah"] = "Bahamut",
        ["chr"] = "Cinematic", ["pvp"] = "PVP", ["ind"] = "Indoor",
        ["ang"] = "Arena", ["nature"] = "Earth", ["jai"] = "Jail",
        ["common"] = "Common",
    };

    private static readonly Dictionary<string, string> AssetTypeNames =
        new(StringComparer.Ordinal)
    {
        ["rck"] = "Rock", ["rock"] = "Rock", ["roc"] = "Rock",
        ["rok"] = "Rock", ["rk"] = "Rock",
        ["wal"] = "Wall", ["wall"] = "Wall",
        ["tre"] = "Tree", ["tree"] = "Tree",
        ["dor"] = "Door", ["door"] = "Door",
        ["cel"] = "Ceiling",
        ["plr"] = "Pillar", ["pil"] = "Pillar", ["pill"] = "Pillar",
        ["flo"] = "Floor", ["flr"] = "Floor", ["flor"] = "Floor",
        ["lmp"] = "Lamp", ["lamp"] = "Lamp",
        ["ligt"] = "Light", ["lig"] = "Light",
        ["gat"] = "Gate", ["gate"] = "Gate",
        ["fen"] = "Fence", ["fnc"] = "Fence", ["fenc"] = "Fence",
        ["tow"] = "Tower", ["obj"] = "Object", ["nat"] = "Nature",
        ["cry"] = "Crystal",
        ["wat"] = "Water", ["sea"] = "Sea",
        ["stc"] = "Structure",
        ["gls"] = "Glass", ["grs"] = "Grass",
        ["box"] = "Box", ["flw"] = "Flower", ["bos"] = "Boss",
        ["boss"] = "Boss", ["wep"] = "Weapon", ["fnt"] = "Furniture",
        ["rub"] = "Rubble", ["cin"] = "Coins", ["lsf"] = "Landscape",
        ["arf"] = "Miscellaneous", ["ter"] = "Terrain",
        ["plt"] = "Foliage", ["bsh"] = "Foliage", ["gren"] = "Foliage",
        ["itm"] = "Item",
        ["chr"] = "Chair", ["chair"] = "Chair", ["cha"] = "Chair",
        ["dsk"] = "Desk", ["desk"] = "Desk",
        ["rug"] = "Rug",
        ["slf"] = "Shelf", ["shelf"] = "Shelf", ["she"] = "Shelf",
        ["win"] = "Window",
        ["tbl"] = "Table", ["table"] = "Table",
        ["bed"] = "Bed",
        ["sof"] = "Sofa", ["sofa"] = "Sofa",
        ["cab"] = "Cabinet",
        ["sign"] = "Sign", ["sgn"] = "Sign",
        ["stl"] = "Stall", ["ban"] = "Banner", ["pot"] = "Pot",
        ["str"] = "Stairs", ["brg"] = "Bridge", ["rof"] = "Roof",
        ["fsh"] = "Fish",
        ["rom"] = "Room", ["room"] = "Room",
        ["bas"] = "Base", ["base"] = "Base",
        ["air"] = "Airship",
        ["step"] = "Steps", ["stp"] = "Steps",
        ["pip"] = "Pipe", ["pol"] = "Pole",
        ["ivy"] = "Ivy", ["tuta"] = "Ivy",
        ["dec"] = "Decoration", ["bui"] = "Building",
        ["sak"] = "Railing", ["saku"] = "Railing",
        ["stn"] = "Stone",
    };

    /// <summary>The derived label for ANY path — the sidebar, hover, and
    /// entry names use the same words the pickers do, so a spawned thing
    /// keeps the name it was found under.</summary>
    public static string LabelFor(string path)
    {
        string stem = System.IO.Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrWhiteSpace(stem)
            ? path
            : LabelOf(path, stem);
    }

    private static string LabelOf(string path, string stem)
    {
        string type = AssetTypeOf(stem);
        return type.Length == 0 ? stem : $"{type} [{stem}]";
    }

    private static string AssetTypeOf(string stem)
    {
        foreach (var part in stem.Split(
            '_', StringSplitOptions.RemoveEmptyEntries))
        {
            int alpha = 0;
            while (alpha < part.Length && char.IsAsciiLetterLower(part[alpha]))
                alpha++;
            if (alpha >= 2
                && AssetTypeNames.TryGetValue(part[..alpha], out var word))
                return word;
        }
        return string.Empty;
    }

    private static string ContextOf(string path)
    {
        var parts = path.Split('/');
        string expansion = "Base Game";
        string subtype = string.Empty;
        if (parts.Length > 1 && parts[0] == "bg"
            && ExpansionNames.TryGetValue(parts[1], out var named))
        {
            expansion = named;
            if (parts.Length > 3)
                subtype = SubtypeNames.TryGetValue(parts[3], out var sub)
                    ? sub
                    : parts[3];
        }
        else if (parts[0] == "bgcommon" && parts.Length > 2)
        {
            subtype = SubtypeNames.TryGetValue(parts[1], out var sub)
                ? sub
                : parts[1];
        }
        return subtype.Length == 0 ? expansion : expansion + " - " + subtype;
    }

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
                string stem =
                    System.IO.Path.GetFileNameWithoutExtension(line);
                assets.Add(new WorldAsset(
                    stem, line, LabelOf(line, stem), ContextOf(line)));
            }
            return assets;
        }
        catch (Exception)
        {
            return Array.Empty<WorldAsset>();
        }
    }
}
