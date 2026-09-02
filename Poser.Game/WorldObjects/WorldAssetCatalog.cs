using Poser.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace Poser.Game.WorldObjects;

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
    /// <summary>The user's own names, beside the config: a plain JSON
    /// object of path → label, overlaid on the derived names at catalog
    /// load — names grow with use instead of shipping a curated database
    /// nobody has. Created empty on first run so it is findable; read
    /// once per session.</summary>
    public const string NamesFileName = "asset-names.json";

    private static string? _namesPath;
    private static Dictionary<string, string>? _overrides;

    public WorldAssetCatalog(
        Dalamud.Plugin.IDalamudPluginInterface pluginInterface)
    {
        _namesPath = System.IO.Path.Combine(
            pluginInterface.GetPluginConfigDirectory(), NamesFileName);
    }

    private static Dictionary<string, string> Overrides
    {
        get
        {
            if (_overrides is { } loaded)
                return loaded;
            var names = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            try
            {
                if (_namesPath is { } path)
                {
                    if (!File.Exists(path))
                        File.WriteAllText(path, "{" + global::System.Environment.NewLine + "}" + global::System.Environment.NewLine);
                    else if (System.Text.Json.JsonSerializer
                        .Deserialize<Dictionary<string, string>>(
                            File.ReadAllText(path)) is { } read)
                        foreach (var (key, value) in read)
                            if (!string.IsNullOrWhiteSpace(value))
                                names[key] = value.Trim();
                }
            }
            catch (Exception)
            {
                // A malformed file names nothing; the derived labels stand.
            }
            return _overrides = names;
        }
    }

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
        // The romaji half of the game's naming, mined from the stems the
        // English map left unmatched (half the catalog): taru is a
        // barrel, iwa a rock, kabe a wall — plus the clipped English the
        // original map missed.
        ["taru"] = "Barrel", ["iwa"] = "Rock", ["kabe"] = "Wall",
        ["yama"] = "Mountain", ["yuka"] = "Floor", ["ita"] = "Planks",
        ["hako"] = "Box", ["isu"] = "Chair", ["tana"] = "Shelf",
        ["tsubo"] = "Jar", ["ishi"] = "Stone", ["kaidan"] = "Stairs",
        ["mado"] = "Window", ["hei"] = "Fence", ["yane"] = "Roof",
        ["fune"] = "Ship", ["take"] = "Bamboo", ["sakura"] = "Cherry tree",
        ["matsu"] = "Pine", ["hashi"] = "Bridge", ["has"] = "Bridge",
        ["kusa"] = "Grass", ["hana"] = "Flower", ["toge"] = "Thorns",
        ["hou"] = "House", ["sip"] = "Ship", ["mon"] = "Gate",
        ["wod"] = "Wood", ["cor"] = "Corridor", ["bil"] = "Building",
        ["sta"] = "Statue", ["dai"] = "Platform", ["kin"] = "Gold",
        ["grl"] = "Grille", ["gare"] = "Rubble", ["grk"] = "Rubble",
        ["frm"] = "Frame", ["twr"] = "Tower",
        ["tnt"] = "Tent", ["tent"] = "Tent",
        ["hal"] = "Hall", ["roof"] = "Roof", ["tabl"] = "Table",
        ["flag"] = "Flag", ["flg"] = "Flag", ["root"] = "Roots",
        ["plan"] = "Plant", ["blk"] = "Block", ["ston"] = "Stone",
        ["ndl"] = "Needle", ["rail"] = "Railing", ["arc"] = "Arch",
        ["net"] = "Net", ["dom"] = "Dome", ["debr"] = "Debris",
        ["mus"] = "Mushroom", ["xma"] = "Starlight", ["aet"] = "Aether",
        ["evt"] = "Event", ["gmc"] = "Gimmick", ["tbx"] = "Chest",
        ["vfog"] = "Fog", ["vfg"] = "Fog",
        ["cas"] = "Castle", ["bri"] = "Bridge", ["stg"] = "Stage",
        // Best-effort wave over the biggest remaining groups (user
        // 2026-08-31: name them and kill the remainder). Guessed from
        // context, correctable one word at a time.
        ["fun"] = "Vent", ["gar"] = "Rubble",
        ["all"] = "Assembly", ["emp"] = "Imperial",
        ["rod"] = "Rod", ["cut"] = "Cinematic", ["rid"] = "Ridge",
        ["stir"] = "Stairs", ["grd"] = "Ground", ["hng"] = "Hanging",
        ["mist"] = "Mist", ["dust"] = "Dust", ["kumo"] = "Cloud",
        ["lit"] = "Light",
    };

    /// <summary>The EFFECT vocabulary, mined from the 8k stems: FFXIV
    /// names its world effects in a mix of English clips and romaji —
    /// taki is a waterfall, kumo a cloud, kira a sparkle, igene/ogene
    /// the indoor/outdoor ambient loops. Checked before the model map
    /// for .avfx paths.</summary>
    private static readonly Dictionary<string, string> VfxTypeNames =
        new(StringComparer.Ordinal)
    {
        ["igene"] = "Ambient", ["ogene"] = "Ambient",
        ["yuka"] = "Floor",
        ["fire"] = "Fire", ["fir"] = "Fire",
        ["watr"] = "Water", ["wtr"] = "Water", ["wat"] = "Water",
        ["water"] = "Water",
        ["bari"] = "Barrier",
        ["mete"] = "Meteor", ["cmet"] = "Meteor",
        ["taki"] = "Waterfall", ["tak"] = "Waterfall",
        ["smok"] = "Smoke", ["smk"] = "Smoke", ["smoke"] = "Smoke",
        ["sky"] = "Sky",
        ["yug"] = "Steam",
        ["thud"] = "Thunder", ["thd"] = "Thunder", ["thund"] = "Thunder",
        ["kumo"] = "Cloud", ["clud"] = "Cloud", ["cloud"] = "Cloud",
        ["kira"] = "Sparkle",
        ["brak"] = "Debris", ["brek"] = "Debris", ["brk"] = "Debris",
        ["fog"] = "Fog",
        ["aet"] = "Aether",
        ["bolt"] = "Lightning", ["elec"] = "Lightning",
        ["mist"] = "Mist",
        ["dust"] = "Dust",
        ["expl"] = "Explosion",
        ["beam"] = "Beam", ["bem"] = "Beam",
        ["snow"] = "Snow",
        ["iceb"] = "Ice", ["ice"] = "Ice",
        ["scrn"] = "Screen",
        ["elev"] = "Elevator",
        ["swic"] = "Switch",
        ["jump"] = "Jump",
        ["leaf"] = "Leaves", ["lef"] = "Leaves",
        ["bub"] = "Bubbles",
        ["torch"] = "Torch", ["trch"] = "Torch",
        ["wind"] = "Wind",
        ["sand"] = "Sand",
        ["rain"] = "Rain",
        ["star"] = "Stars",
        ["glow"] = "Glow",
        ["aura"] = "Aura",
    };

    /// <summary>The derived label for ANY path — the sidebar, hover, and
    /// entry names use the same words the pickers do, so a spawned thing
    /// keeps the name it was found under.</summary>
    public static string LabelFor(string path)
    {
        if (Overrides.TryGetValue(path, out var custom))
            return custom;
        string stem = System.IO.Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrWhiteSpace(stem)
            ? path
            : LabelOf(path, stem);
    }

    private static string LabelOf(string path, string stem)
    {
        bool effect = path.EndsWith(
            ".avfx", StringComparison.OrdinalIgnoreCase);
        string type = AssetTypeOf(stem, effect);
        return type.Length == 0 ? stem : $"{type} [{stem}]";
    }

    private static string AssetTypeOf(string stem, bool effect)
    {
        foreach (var part in stem.Split(
            '_', StringSplitOptions.RemoveEmptyEntries))
        {
            int alpha = 0;
            while (alpha < part.Length && char.IsAsciiLetterLower(part[alpha]))
                alpha++;
            if (alpha < 2)
                continue;
            string token = part[..alpha];
            if (effect && VfxTypeNames.TryGetValue(token, out var vfxWord))
                return vfxWord;
            if (AssetTypeNames.TryGetValue(token, out var word))
                return word;
        }
        return string.Empty;
    }

    private static string ContextOf(string path)
    {
        var parts = path.Split('/');
        string expansion = "Base Game";
        string subtype = string.Empty;
        string zone = string.Empty;
        if (parts.Length > 1 && parts[0] == "bg"
            && ExpansionNames.TryGetValue(parts[1], out var named))
        {
            expansion = named;
            if (parts.Length > 3)
                subtype = SubtypeNames.TryGetValue(parts[3], out var sub)
                    ? sub
                    : parts[3];
            // The ZONE segment (fst_f1, roc_r2) narrows the badge to the
            // place the asset dresses — the one honest fact an opaque
            // stem always carries.
            if (parts.Length > 2 && parts[2] != "common")
                zone = parts[2];
        }
        else if (parts[0] == "bgcommon" && parts.Length > 2)
        {
            subtype = SubtypeNames.TryGetValue(parts[1], out var sub)
                ? sub
                : parts[1];
        }
        string context = subtype.Length == 0
            ? expansion
            : expansion + " - " + subtype;
        return zone.Length == 0 ? context : context + " · " + zone;
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
                string label = Overrides.TryGetValue(line, out var custom)
                    ? custom
                    : LabelOf(line, stem);
                assets.Add(new WorldAsset(
                    stem, line, label, ContextOf(line)));
            }
            // RECOGNIZED entries lead (user 2026-08-31: "sort the ones
            // matching first"): a row with a real word sorts before a raw
            // stem, alphabetical within each half.
            assets.Sort(static (a, b) =>
            {
                // NAMED means the label says more than the raw stem — a
                // derived word or a user override (the old length compare
                // misfiled a custom name of the stem's exact length).
                bool aNamed = !string.Equals(
                    a.Label, a.Name, StringComparison.Ordinal);
                bool bNamed = !string.Equals(
                    b.Label, b.Name, StringComparison.Ordinal);
                if (aNamed != bNamed)
                    return aNamed ? -1 : 1;
                return string.Compare(
                    a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
            });
            return assets;
        }
        catch (Exception)
        {
            return Array.Empty<WorldAsset>();
        }
    }
}
