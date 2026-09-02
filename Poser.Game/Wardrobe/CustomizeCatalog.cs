using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Poser.Domain.Integration;
using Poser.Services;

namespace Poser.Game.Wardrobe;

/// <summary>
/// The character-making data from the game: the races and clans from
/// their sheets, the menu per clan and gender from CharaMakeType (with
/// the long hair and face-paint lists from CharaMakeCustomize), and the
/// colours from the human colour file the game's own UI reads. Built
/// once on first use.
///
/// <para>The colour file is blocks of 256 packed ABGR colours. The first
/// five blocks are shader colour sets, the next five the UI palettes —
/// eyes, highlights, then lips, tattoo and face paint further in — and
/// from block 18 on, per clan and gender, five blocks of which the
/// fourth is skin and the fifth hair (the Ktisis reading, which shows
/// what the game's own picker shows).</para>
/// </summary>
public sealed class CustomizeCatalog : ICustomizeCatalog
{
    private const string HumanColors = "chara/xls/charamake/human.cmp";
    private const int Block = 256;
    private const int UiLength = 192;
    private const int AlphaLength = 128;
    private const int ExtendedLength = 208;

    private readonly IDataManager _data;
    private readonly IPluginLog _log;
    private List<RaceEntry>? _races;
    private List<ClanEntry>? _clans;
    private Dictionary<(byte Clan, byte Gender), CustomizeMenu>? _menus;
    private CustomizePalettes? _palettes;

    private readonly object _gate = new();

    public CustomizeCatalog(IDataManager data, IPluginLog log)
    {
        _data = data;
        _log = log;
    }

    /// <summary>Reads the sheets and the colour file now, off the draw
    /// thread, so the first Appearance view pays nothing.</summary>
    public void Warm()
    {
        LoadNames();
        LoadMenus();
    }

    public string LegacyTattooTexture => "chara/common/texture/decal_equip/_stigma.tex";

    public IReadOnlyList<RaceEntry> Races
    {
        get { LoadNames(); return _races!; }
    }

    public IReadOnlyList<ClanEntry> Clans
    {
        get { LoadNames(); return _clans!; }
    }

    public CustomizeMenu? Menu(byte clan, byte gender)
    {
        LoadMenus();
        lock (_gate)
        {
            if (!_menus!.TryGetValue((clan, gender), out var menu))
                return null;
            if (_discovered.Add((clan, gender)))
                _menus[(clan, gender)] = menu = Discover(menu);
            return menu;
        }
    }

    private readonly HashSet<(byte, byte)> _discovered = new();

    /// <summary>The faces and hairs the sheet does not list but the model
    /// files hold — the NPC ones — found by probing the file paths, as
    /// Ktisis does. They list without an icon, after the sheet's own, in
    /// order. Four clans share their faces with a hundred added, and those
    /// echoes are left out.</summary>
    private CustomizeMenu Discover(CustomizeMenu menu)
    {
        ushort dataId = DataIdFor(menu.Clan, menu.Gender);
        var features = new Dictionary<CustomizeKey, CustomizeFeature>(menu.Features);
        if (features.TryGetValue(CustomizeKey.Face, out var face))
        {
            bool echoes = menu.Clan is 4 or 6 or 8 or 10;
            var known = new HashSet<byte>();
            foreach (var option in face.Options)
            {
                known.Add(option.Value);
                if (echoes)
                    known.Add((byte)(option.Value + 100));
            }
            var found = new List<CustomizeOption>(face.Options);
            for (int i = 0; i <= byte.MaxValue; i++)
            {
                if (known.Contains((byte)i))
                    continue;
                if (_data.FileExists(string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "chara/human/c{0:D4}/obj/face/f{1:D4}/model/c{0:D4}f{1:D4}_fac.mdl", dataId, i)))
                    found.Add(new CustomizeOption((byte)i, 0));
            }
            features[CustomizeKey.Face] = face with { Options = found };
        }
        if (features.TryGetValue(CustomizeKey.Hairstyle, out var hair))
        {
            var known = new HashSet<byte>();
            foreach (var option in hair.Options)
                known.Add(option.Value);
            var found = new List<CustomizeOption>(hair.Options);
            for (int i = 0; i <= byte.MaxValue; i++)
            {
                if (known.Contains((byte)i))
                    continue;
                if (_data.FileExists(string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "chara/human/c{0:D4}/obj/hair/h{1:D4}/model/c{0:D4}h{1:D4}_hir.mdl", dataId, i)))
                    found.Add(new CustomizeOption((byte)i, 0));
            }
            features[CustomizeKey.Hairstyle] = hair with { Options = found };
        }
        return menu with { Features = features };
    }

    /// <summary>The model folder a clan and gender draw from.</summary>
    private static ushort DataIdFor(byte clan, byte gender)
    {
        bool feminine = gender == 1;
        int race = (clan + 1) / 2;
        int id = clan switch
        {
            1 => feminine ? 201 : 101,
            2 => feminine ? 401 : 301,
            _ => race switch
            {
                2 => feminine ? 601 : 501,
                4 => feminine ? 801 : 701,
                5 => feminine ? 1001 : 901,
                3 => feminine ? 1201 : 1101,
                _ => 1301 + (race - 6) * 200 + (feminine ? 100 : 0),
            },
        };
        return (ushort)id;
    }

    public CustomizePalettes Palettes
    {
        get { LoadMenus(); return _palettes!; }
    }

    // ── names ───────────────────────────────────────────────────────────

    private void LoadNames()
    {
        lock (_gate)
        {
        if (_races is not null)
            return;
        var races = new List<RaceEntry>();
        var clans = new List<ClanEntry>();
        try
        {
            foreach (var row in _data.GetExcelSheet<Race>())
            {
                string name = row.Masculine.ExtractText();
                if (row.RowId == 0 || string.IsNullOrWhiteSpace(name))
                    continue;
                races.Add(new RaceEntry((byte)row.RowId, name));
            }
            foreach (var row in _data.GetExcelSheet<Tribe>())
            {
                string name = row.Masculine.ExtractText();
                if (row.RowId == 0 || string.IsNullOrWhiteSpace(name))
                    continue;
                clans.Add(new ClanEntry((byte)row.RowId, (byte)((row.RowId + 1) / 2), name));
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"Customize: the race sheets could not be read: {ex.Message}");
        }
        _clans = clans;
        _races = races;
        }
    }

    // ── menus ───────────────────────────────────────────────────────────

    /// <summary>The customize byte the sheet names, as Glamourer's key.
    /// Null for what the view does not offer.</summary>
    private static CustomizeKey? KeyOf(uint index) => index switch
    {
        0 => CustomizeKey.Race,
        1 => CustomizeKey.Gender,
        2 => CustomizeKey.BodyType,
        3 => CustomizeKey.Height,
        4 => CustomizeKey.Clan,
        5 => CustomizeKey.Face,
        6 => CustomizeKey.Hairstyle,
        7 => CustomizeKey.Highlights,
        8 => CustomizeKey.SkinColor,
        9 => CustomizeKey.EyeColorRight,
        10 => CustomizeKey.HairColor,
        11 => CustomizeKey.HighlightsColor,
        12 => CustomizeKey.FacialFeature1,
        13 => CustomizeKey.TattooColor,
        14 => CustomizeKey.Eyebrows,
        15 => CustomizeKey.EyeColorLeft,
        16 => CustomizeKey.EyeShape,
        17 => CustomizeKey.Nose,
        18 => CustomizeKey.Jaw,
        19 => CustomizeKey.Mouth,
        20 => CustomizeKey.LipColor,
        21 => CustomizeKey.MuscleMass,
        22 => CustomizeKey.TailShape,
        23 => CustomizeKey.BustSize,
        24 => CustomizeKey.FacePaint,
        25 => CustomizeKey.FacePaintColor,
        _ => null,
    };

    private void LoadMenus()
    {
        lock (_gate)
        {
        if (_menus is not null)
            return;
        var menus = new Dictionary<(byte, byte), CustomizeMenu>();
        var skins = new Dictionary<(byte, byte), uint[]>();
        var hairs = new Dictionary<(byte, byte), uint[]>();
        try
        {
            var customize = _data.GetExcelSheet<CharaMakeCustomize>();
            foreach (var row in _data.GetExcelSheet<CharaMakeType>())
            {
                byte clan = (byte)row.Tribe.RowId;
                byte gender = (byte)row.Gender;
                if (clan == 0)
                    continue;
                var features = new Dictionary<CustomizeKey, CustomizeFeature>();
                foreach (var make in row.CharaMakeStruct)
                {
                    if (make.Customize == 0 || KeyOf(make.Customize) is not { } key)
                        continue;
                    if (features.ContainsKey(key))
                        continue;
                    string name = make.Menu.ValueNullable?.Text.ExtractText() ?? string.Empty;
                    bool icons = make.SubMenuType == 1;
                    bool longList = icons && make.SubMenuNum > 10;
                    var options = new List<CustomizeOption>();
                    if (longList)
                    {
                        // The hair and face-paint lists live in CharaMakeCustomize,
                        // from the row the first parameter points at (less two).
                        int first = key == CustomizeKey.FacePaint ? 1 : 0;
                        uint start = make.SubMenuParam[first] - 2;
                        uint count = key == CustomizeKey.Hairstyle ? 99u : 49u;
                        for (uint i = start; i < start + count; i++)
                        {
                            if (!customize.HasRow(i))
                                continue;
                            var entry = customize.GetRow(i);
                            if (entry.FeatureID == 0 && entry.Icon == 0)
                                continue;
                            options.Add(new CustomizeOption(entry.FeatureID, entry.Icon));
                        }
                    }
                    else if (make.SubMenuType <= 1)
                    {
                        int count = Math.Min(make.SubMenuNum, make.SubMenuGraphic.Count);
                        for (int i = 0; i < count; i++)
                            options.Add(new CustomizeOption(
                                make.SubMenuGraphic[i],
                                icons ? make.SubMenuParam[i] : 0u));
                    }
                    features[key] = new CustomizeFeature(key, name, options, icons);
                }

                // The seven facial-feature icons, per face the row offers.
                var faceIcons = new Dictionary<byte, uint[]>();
                if (features.TryGetValue(CustomizeKey.Face, out var faces)
                    && row.FacialFeatureOption.Count == 8)
                {
                    for (int x = 0; x < faces.Options.Count && x < 8; x++)
                    {
                        var option = row.FacialFeatureOption[x];
                        faceIcons[faces.Options[x].Value] = new[]
                        {
                            (uint)option.Option1, (uint)option.Option2, (uint)option.Option3,
                            (uint)option.Option4, (uint)option.Option5, (uint)option.Option6,
                            (uint)option.Option7,
                        };
                    }
                }
                menus[(clan, gender)] = new CustomizeMenu(
                    clan, gender, features, faceIcons, Array.Empty<uint>(), Array.Empty<uint>());
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"Customize: the character-making sheet could not be read: {ex.Message}");
        }

        var palettes = new CustomizePalettes(
            Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(),
            Array.Empty<uint>(), Array.Empty<uint>());
        try
        {
            var file = _data.GetFile(HumanColors);
            if (file is null)
                throw new InvalidOperationException("the file is missing");
            var colors = new uint[file.Data.Length / 4];
            Buffer.BlockCopy(file.Data, 0, colors, 0, colors.Length * 4);
            palettes = new CustomizePalettes(
                Slice(colors, 5 * Block, UiLength),
                Slice(colors, 6 * Block, ExtendedLength),
                Slice(colors, 11 * Block, AlphaLength),
                Slice(colors, 12 * Block, ExtendedLength),
                Slice(colors, 13 * Block, AlphaLength));
            foreach (var (clan, gender) in menus.Keys.ToArray())
            {
                int index = Math.Max(0, (clan - 1) * 2 + gender);
                int baseAt = 4608 + index * 1280;
                bool extendedHair = clan is not (13 or 14);
                skins[(clan, gender)] = Slice(colors, baseAt + 3 * Block, UiLength);
                hairs[(clan, gender)] = Slice(colors, baseAt + 4 * Block, extendedHair ? ExtendedLength : UiLength);
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"Customize: the colour file could not be read: {ex.Message}");
        }

        foreach (var (key, menu) in menus.ToArray())
            menus[key] = menu with
            {
                SkinColors = skins.TryGetValue(key, out var skin) ? skin : Array.Empty<uint>(),
                HairColors = hairs.TryGetValue(key, out var hair) ? hair : Array.Empty<uint>(),
            };
        _palettes = palettes;
        _menus = menus;
        }
    }

    private static uint[] Slice(uint[] colors, int start, int length)
    {
        if (start >= colors.Length)
            return Array.Empty<uint>();
        int take = Math.Min(length, colors.Length - start);
        var slice = new uint[take];
        Array.Copy(colors, start, slice, 0, take);
        return slice;
    }
}
