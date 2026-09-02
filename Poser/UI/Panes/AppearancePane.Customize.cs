using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Poser.Application.Integration;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Services;

namespace Poser.UI;

/// <summary>
/// The Appearance view: the actor's customization, through Glamourer,
/// with the choices the character-making sheet offers its clan and
/// gender. Body first (race, clan and gender redraw, so they wear the
/// disruptive step), then the face as icon tiles and named options, the
/// colours as palette tiles that open the game's own palettes, and the
/// face paint. Every value is a journal step; a slider folds while it
/// drags. Without Glamourer everything disables in place and says why.
/// </summary>
public sealed partial class AppearancePane
{
    private bool _openBody = true;
    private bool _openFace = true;
    private bool _openColours = true;
    private bool _openPaint = true;

    private static readonly TimeSpan CustomizeInterval = TimeSpan.FromSeconds(1);
    private ActorId? _customizeActor;
    private DateTime _customizeAt = DateTime.MinValue;
    private CustomizeState? _customizeState;
    private string? _customizeDetail;

    private static readonly string[] GenderLabels = ["Male", "Female"];

    // The palette picker and what opened it.
    private readonly Crystarium.PalettePicker _palette = new("appearance-palette");
    private CustomizeKey _paletteKey;
    private ActorId? _paletteActor;
    private readonly Dictionary<uint[], List<Vector4>> _paletteColors = new(ReferenceEqualityComparer.Instance);

    // One tile picker per clan, gender and feature: its dropped ids are
    // its own, and a clan's faces are not another's.
    private readonly Dictionary<(byte Clan, byte Gender, CustomizeKey Key), Crystarium.TexturePicker> _tilePickers = new();
    private readonly Dictionary<(byte Clan, byte Gender, CustomizeKey Key), Dictionary<uint, uint>> _tileIcons = new();

    // ── the view ────────────────────────────────────────────────────────

    private void DrawCustomizeView(Crystarium.PageScope page, ActorId actor)
    {
        var glamourer = _integration.Glamourer;
        bool ready = glamourer.Available;
        string? blocked = ready ? null : glamourer.Detail;
        var state = ready ? ReadCustomize(actor) : null;
        byte clan = (byte)(state?.Value(CustomizeKey.Clan) ?? 0);
        byte gender = (byte)(state?.Value(CustomizeKey.Gender) ?? 0);
        var menu = state is null ? null : _customize.Menu(clan, gender);
        bool live = ready && state is not null;
        string? why = !ready ? blocked : state is null ? (_customizeDetail ?? "The look could not be read.") : null;

        page.Section("Body", _openBody, next => _openBody = next, form =>
        {
            if (why is not null)
                form.Status(why);
            BodyRows(form, actor, state, live, why);
            form.Slider("Height", state?.Value(CustomizeKey.Height) ?? 0, 0f, 100f,
                value => Set(actor, CustomizeKey.Height, (int)MathF.Round(value), "Set height"),
                help: live ? "How tall" : why, disabled: !live, onBegin: _customizeSession.Seal);
            if (menu?.Feature(CustomizeKey.BustSize) is { } bust)
                form.Slider(bust.Name, state?.Value(CustomizeKey.BustSize) ?? 0, 0f, 100f,
                    value => Set(actor, CustomizeKey.BustSize, (int)MathF.Round(value), "Set bust size"),
                    help: live ? "Bust size" : why, disabled: !live, onBegin: _customizeSession.Seal);
            if (menu?.Feature(CustomizeKey.MuscleMass) is { } muscle)
                form.Slider(muscle.Name, state?.Value(CustomizeKey.MuscleMass) ?? 0, 0f, 100f,
                    value => Set(actor, CustomizeKey.MuscleMass, (int)MathF.Round(value), "Set muscle"),
                    help: live ? "Muscle mass" : why, disabled: !live, onBegin: _customizeSession.Seal);
        }, divider: false);

        page.Section("Face", _openFace, next => _openFace = next, form =>
        {
            form.Pair(
                "Face", cell => TileField(cell, actor, menu, state, CustomizeKey.Face, live, why),
                "Hair", cell => TileField(cell, actor, menu, state, CustomizeKey.Hairstyle, live, why),
                help: "The face and the hair, off the game's own tiles");
            form.PairRows();
            OptionRow(form, actor, menu, state, CustomizeKey.Eyebrows, "Eyebrows", live, why);
            OptionRow(form, actor, menu, state, CustomizeKey.EyeShape, "Eyes", live, why);
            OptionRow(form, actor, menu, state, CustomizeKey.Nose, "Nose", live, why);
            OptionRow(form, actor, menu, state, CustomizeKey.Jaw, "Jaw", live, why);
            OptionRow(form, actor, menu, state, CustomizeKey.Mouth, "Mouth", live, why);
            form.Switch("Small iris", (state?.Value(CustomizeKey.SmallIris) ?? 0) != 0,
                on => Set(actor, CustomizeKey.SmallIris, on ? Flag(CustomizeKey.SmallIris) : 0, on ? "Small iris" : "Large iris"),
                help: live ? "Smaller irises" : why, disabled: !live);
            form.EndPair();
            FeatureRow(form, actor, menu, state, live, why);
            if (menu?.Feature(CustomizeKey.TailShape) is { } tail)
                form.Pair(
                    tail.Name, cell => TileField(cell, actor, menu, state, CustomizeKey.TailShape, live, why),
                    string.Empty, _ => { },
                    help: "The ears or the tail");
        });

        page.Section("Colours", _openColours, next => _openColours = next, form =>
        {
            form.Pair(
                "Skin", cell => ColorField(cell, actor, state, CustomizeKey.SkinColor, menu?.SkinColors, live, why),
                "Hair", cell => ColorField(cell, actor, state, CustomizeKey.HairColor, menu?.HairColors, live, why),
                help: "The skin and the hair");
            bool highlights = (state?.Value(CustomizeKey.Highlights) ?? 0) != 0;
            form.Pair(
                "Highlights", cell => cell.Switch("appearance-highlights", highlights,
                    on => Set(actor, CustomizeKey.Highlights, on ? Flag(CustomizeKey.Highlights) : 0, on ? "Highlights on" : "Highlights off"),
                    !live, live ? "Highlight the hair" : why),
                "Tint", cell => ColorField(cell, actor, state, CustomizeKey.HighlightsColor,
                    _customize.Palettes.Highlights, live && highlights,
                    live && !highlights ? "Highlights are off" : why),
                help: "Hair highlights and their colour");
            form.Pair(
                "Right eye", cell => ColorField(cell, actor, state, CustomizeKey.EyeColorRight, _customize.Palettes.Eyes, live, why),
                "Left eye", cell => ColorField(cell, actor, state, CustomizeKey.EyeColorLeft, _customize.Palettes.Eyes, live, why),
                help: "Each eye has its own colour");
            bool lipstick = (state?.Value(CustomizeKey.Lipstick) ?? 0) != 0;
            form.Pair(
                "Lipstick", cell => cell.Switch("appearance-lipstick", lipstick,
                    on => Set(actor, CustomizeKey.Lipstick, on ? Flag(CustomizeKey.Lipstick) : 0, on ? "Lipstick on" : "Lipstick off"),
                    !live, live ? "Colour the lips" : why),
                "Lips", cell => ColorField(cell, actor, state, CustomizeKey.LipColor,
                    _customize.Palettes.Lips, live && lipstick,
                    live && !lipstick ? "Lipstick is off" : why),
                help: "Lipstick and its colour");
            form.Pair(
                "Tattoo", cell => ColorField(cell, actor, state, CustomizeKey.TattooColor, _customize.Palettes.Tattoo, live, why),
                string.Empty, _ => { },
                help: "The colour of the facial features and tattoos");
        });

        page.Section("Face paint", _openPaint, next => _openPaint = next, form =>
        {
            bool reversed = (state?.Value(CustomizeKey.FacePaintReversed) ?? 0) != 0;
            form.Pair(
                "Paint", cell => TileField(cell, actor, menu, state, CustomizeKey.FacePaint, live, why),
                "Reversed", cell => cell.Switch("appearance-paint-reversed", reversed,
                    on => Set(actor, CustomizeKey.FacePaintReversed, on ? Flag(CustomizeKey.FacePaintReversed) : 0, on ? "Reverse face paint" : "Face paint forward"),
                    !live, live ? "Mirror the paint" : why),
                help: "The face paint and its mirror");
            form.Pair(
                "Colour", cell => ColorField(cell, actor, state, CustomizeKey.FacePaintColor, _customize.Palettes.FacePaint, live, why),
                string.Empty, _ => { },
                help: "The colour of the face paint");
        });
    }

    // ── body ────────────────────────────────────────────────────────────

    /// <summary>Race, clan and gender: each redraws the actor, so each is a
    /// disruptive step whose inverse is the triple read before.</summary>
    private void BodyRows(
        Crystarium.FormScope form, ActorId actor, CustomizeState? state, bool live, string? why)
    {
        var races = _customize.Races;
        var clans = _customize.Clans;
        int race = state?.Value(CustomizeKey.Race) ?? 0;
        int clan = state?.Value(CustomizeKey.Clan) ?? 0;
        int gender = state?.Value(CustomizeKey.Gender) ?? 0;

        var raceNames = new string[races.Count];
        int raceIndex = -1;
        for (int i = 0; i < races.Count; i++)
        {
            raceNames[i] = races[i].Name;
            if (races[i].Race == race)
                raceIndex = i;
        }
        form.Dropdown("Race", raceNames, raceIndex, index =>
        {
            var chosen = races[index];
            byte firstClan = 0;
            foreach (var entry in clans)
                if (entry.Race == chosen.Race)
                {
                    firstClan = entry.Clan;
                    break;
                }
            Body(actor, state, "Change race", new Dictionary<CustomizeKey, int>
            {
                [CustomizeKey.Race] = chosen.Race,
                [CustomizeKey.Clan] = firstClan,
            });
        }, help: live ? "Redraws the actor" : why, disabled: !live);

        var clanRows = new List<ClanEntry>();
        foreach (var entry in clans)
            if (entry.Race == race)
                clanRows.Add(entry);
        var clanNames = new string[clanRows.Count];
        int clanIndex = -1;
        for (int i = 0; i < clanRows.Count; i++)
        {
            clanNames[i] = clanRows[i].Name;
            if (clanRows[i].Clan == clan)
                clanIndex = i;
        }
        form.Dropdown("Clan", clanNames, clanIndex, index =>
            Body(actor, state, "Change clan", new Dictionary<CustomizeKey, int>
            {
                [CustomizeKey.Race] = clanRows[index].Race,
                [CustomizeKey.Clan] = clanRows[index].Clan,
            }),
            help: live ? "Redraws the actor" : why, disabled: !live || clanRows.Count == 0);

        form.Dropdown("Gender", GenderLabels, Math.Clamp(gender, 0, 1), index =>
            Body(actor, state, "Change gender", new Dictionary<CustomizeKey, int>
            {
                [CustomizeKey.Gender] = index,
            }),
            help: live ? "Redraws the actor" : why, disabled: !live);
    }

    private void Body(
        ActorId actor, CustomizeState? state, string description,
        IReadOnlyDictionary<CustomizeKey, int> next)
    {
        var before = new Dictionary<CustomizeKey, int>();
        foreach (var key in next.Keys)
            before[key] = state?.Value(key) ?? 0;
        ReportExternal(_disruptive.Run(actor, description,
            () => _customizeSession.Apply(actor, next),
            () => _customizeSession.Apply(actor, before)), description);
        InvalidateCustomize();
    }

    // ── rows ────────────────────────────────────────────────────────────

    /// <summary>A named option — eyebrows, eyes, nose — as a dropdown of
    /// the values the sheet offers.</summary>
    private void OptionRow(
        Crystarium.FormScope form, ActorId actor, CustomizeMenu? menu,
        CustomizeState? state, CustomizeKey key, string label, bool live, string? why)
    {
        var feature = menu?.Feature(key);
        var options = feature?.Options ?? Array.Empty<CustomizeOption>();
        var names = new string[options.Count];
        int current = state?.Value(key) ?? 0;
        int selected = -1;
        for (int i = 0; i < options.Count; i++)
        {
            names[i] = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (options[i].Value == current)
                selected = i;
        }
        form.Dropdown(label, names, selected,
            index => Set(actor, key, options[index].Value, $"Set {label.ToLowerInvariant()}"),
            help: live ? (feature?.Name is { Length: > 0 } name ? name : label) : why,
            disabled: !live || options.Count == 0);
    }

    /// <summary>A face, hair, tail or face paint off its tiles: the
    /// texture picker's shape, fed the sheet's icons.</summary>
    private void TileField(
        in Crystarium.FormPairCell cell, ActorId actor, CustomizeMenu? menu,
        CustomizeState? state, CustomizeKey key, bool live, string? why)
    {
        if (menu is null || menu.Feature(key) is not { } feature)
        {
            cell.Text("—", unavailable: true);
            return;
        }
        var picker = TilePicker(menu, feature);
        uint current = (uint)(state?.Value(key) ?? 0);
        picker.Field(cell, current,
            next => Set(actor, key, (int)next, $"Set {feature.Name.ToLowerInvariant()}"),
            disabled: !live,
            help: live ? feature.Name : why);
    }

    /// <summary>The seven facial features and the legacy tattoo as icon
    /// tiles, each a toggle.</summary>
    private void FeatureRow(
        Crystarium.FormScope form, ActorId actor, CustomizeMenu? menu,
        CustomizeState? state, bool live, string? why)
    {
        var theme = Crystarium.ActiveTheme;
        float side = theme.Controls.FormRowHeight * 1.5f;
        float gap = theme.Page.ActionGap;
        form.Custom("Features", side * 2f + gap, row =>
        {
            float s = row.Scale;
            byte face = (byte)(state?.Value(CustomizeKey.Face) ?? 0);
            uint[] icons = Array.Empty<uint>();
            if (menu is not null && !menu.FaceFeatureIcons.TryGetValue(face, out icons!))
                foreach (var any in menu.FaceFeatureIcons.Values)
                {
                    icons = any;
                    break;
                }
            icons ??= Array.Empty<uint>();
            int perLine = 4;
            for (int i = 0; i < 8; i++)
            {
                var key = i < 7 ? CustomizeKey.FacialFeature1 + i : CustomizeKey.LegacyTattoo;
                bool on = (state?.Value(key) ?? 0) != 0;
                nint texture = i < 7
                    ? (i < icons.Length ? ResolveIcon(icons[i]) : 0)
                    : LegacyTattooHandle();
                var at = row.ControlOrigin + new Vector2(
                    (i % perLine) * (side + gap) * s,
                    (i / perLine) * (side + gap) * s);
                ImGui.SetCursorScreenPos(at);
                int index = i;
                Crystarium.ImageTile(
                    $"appearance-feature-{i}",
                    texture,
                    side,
                    () => Set(actor, key, on ? 0 : Flag(key),
                        index < 7 ? $"Feature {index + 1} {(on ? "off" : "on")}" : $"Legacy tattoo {(on ? "off" : "on")}"),
                    help: live ? (index < 7 ? $"Feature {index + 1}" : "Legacy tattoo") : why,
                    disabled: !live,
                    selected: on);
            }
        }, help: "The facial features and the legacy tattoo");
    }

    /// <summary>A colour off its palette: the tile shows the colour and
    /// opens the grid under itself.</summary>
    private void ColorField(
        in Crystarium.FormPairCell cell, ActorId actor, CustomizeState? state,
        CustomizeKey key, uint[]? palette, bool live, string? why)
    {
        var theme = Crystarium.ActiveTheme;
        var colors = palette is { Length: > 0 } ? PaletteColors(palette) : null;
        int current = state?.Value(key) ?? 0;
        Vector4? color = colors is not null && current >= 0 && current < colors.Count ? colors[current] : null;
        bool enabled = live && colors is not null;
        ImGui.SetCursorScreenPos(cell.Center(theme.Controls.WorkspaceHeight));
        bool opened = Crystarium.ColorTile(
            $"appearance-color-{key}",
            color,
            cell.Width / cell.Scale,
            theme.Controls.WorkspaceHeight,
            null,
            label: color is null ? "—" : null,
            help: enabled ? "Choose a colour" : why,
            disabled: !enabled);
        if (opened && colors is not null)
        {
            _paletteKey = key;
            _paletteActor = actor;
            _palette.Open(colors, current, ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
        }
    }

    // ── state and catalogs ──────────────────────────────────────────────

    private CustomizeState? ReadCustomize(ActorId actor)
    {
        var now = DateTime.UtcNow;
        if (_customizeActor is { } cached && cached.Equals(actor)
            && now - _customizeAt < CustomizeInterval)
            return _customizeState;
        _customizeActor = actor;
        _customizeAt = now;
        var read = _customizeSession.Read(actor);
        _customizeState = read.Success ? read.Value : null;
        _customizeDetail = read.Success ? null : read.Detail;
        return _customizeState;
    }

    private void InvalidateCustomize() => _customizeAt = DateTime.MinValue;

    private void Set(ActorId actor, CustomizeKey key, int value, string description)
    {
        ReportExternal(_customizeSession.Set(actor, key, value, description), description);
        InvalidateCustomize();
    }

    private List<Vector4> PaletteColors(uint[] palette)
    {
        if (_paletteColors.TryGetValue(palette, out var colors))
            return colors;
        colors = new List<Vector4>(palette.Length);
        foreach (uint packed in palette)
            colors.Add(ImGui.ColorConvertU32ToFloat4(packed));
        _paletteColors[palette] = colors;
        return colors;
    }

    private Crystarium.TexturePicker TilePicker(CustomizeMenu menu, CustomizeFeature feature)
    {
        var key = (menu.Clan, menu.Gender, feature.Key);
        if (_tilePickers.TryGetValue(key, out var picker))
            return picker;
        var icons = new Dictionary<uint, uint>();
        uint top = 0;
        foreach (var option in feature.Options)
        {
            icons[option.Value] = option.Icon;
            top = Math.Max(top, option.Value);
        }
        _tileIcons[key] = icons;
        picker = new Crystarium.TexturePicker(
            $"appearance-{menu.Clan}-{menu.Gender}-{feature.Key}",
            (uint id, out nint handle, out Vector2 pixels) =>
            {
                pixels = Vector2.Zero;
                handle = 0;
                if (!icons.TryGetValue(id, out uint icon) || icon == 0)
                    return TextureProbe.Missing;
                handle = ResolveIcon(icon);
                if (handle != 0)
                    return TextureProbe.Ready;
                return _missingIcons.Contains(icon) ? TextureProbe.Missing : TextureProbe.Pending;
            },
            top + 1);
        _tilePickers[key] = picker;
        return picker;
    }

    private nint LegacyTattooHandle()
    {
        try
        {
            var wrap = _textures.GetFromGame(_customize.LegacyTattooTexture).GetWrapOrDefault();
            return wrap is null ? 0 : (nint)wrap.Handle.Handle;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>Runs the palette and the tile pickers of the current menu
    /// and lands their picks on the actor captured when they opened.</summary>
    private void DrainCustomizePickers()
    {
        if (_palette.Draw() is { } index && _paletteActor is { } actor)
            Set(actor, _paletteKey, index, $"Set {ColorName(_paletteKey)}");
        foreach (var (key, picker) in _tilePickers)
        {
            if (picker.Draw() is not { } id || _customizeActor is not { } owner)
                continue;
            Set(owner, key.Key, (int)id, $"Set {key.Key.ToString().ToLowerInvariant()}");
        }
    }

    /// <summary>The value a flag key takes when on: a facial feature is
    /// its own bit, the rest are the high bit — as Glamourer reads them
    /// back (a one is ignored; probed live 2026-09-02).</summary>
    private static int Flag(CustomizeKey key) => key switch
    {
        CustomizeKey.FacialFeature1 => 1,
        CustomizeKey.FacialFeature2 => 2,
        CustomizeKey.FacialFeature3 => 4,
        CustomizeKey.FacialFeature4 => 8,
        CustomizeKey.FacialFeature5 => 16,
        CustomizeKey.FacialFeature6 => 32,
        CustomizeKey.FacialFeature7 => 64,
        _ => 128,
    };

    private static string ColorName(CustomizeKey key) => key switch
    {
        CustomizeKey.SkinColor => "skin colour",
        CustomizeKey.HairColor => "hair colour",
        CustomizeKey.HighlightsColor => "highlight colour",
        CustomizeKey.EyeColorRight => "right eye colour",
        CustomizeKey.EyeColorLeft => "left eye colour",
        CustomizeKey.LipColor => "lip colour",
        CustomizeKey.TattooColor => "tattoo colour",
        CustomizeKey.FacePaintColor => "face paint colour",
        _ => "colour",
    };
}
