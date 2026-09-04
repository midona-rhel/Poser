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
/// with the choices the game offers its clan and gender — the sheet's,
/// plus the faces and hairs found by probing the model files, as Ktisis
/// lists them. Body: race and clan on one row with the gender symbol as
/// a swap, then height beside the bust or muscle slider. Face: the four
/// tiles (face, hair, tail or ears, face paint) in one row, each with a
/// plus and minus that step only through valid values; the named options
/// three per row as plus and minus wells; the features as one row of
/// small tiles. Colours three per row, the switches under them. Every
/// value is a journal step; a slider folds while it drags. Without
/// Glamourer everything disables in place and says why.
/// </summary>
public sealed partial class AppearancePane
{
    private bool _openBody = true;
    private bool _openFace = true;
    private bool _openColours = true;

    private static readonly TimeSpan CustomizeInterval = TimeSpan.FromSeconds(1);
    private ActorId? _customizeActor;
    private DateTime _customizeAt = DateTime.MinValue;
    private CustomizeState? _customizeState;
    private string? _customizeDetail;

    private static readonly CustomizeKey[] TileKeys =
    {
        CustomizeKey.Face, CustomizeKey.Hairstyle, CustomizeKey.TailShape, CustomizeKey.FacePaint,
    };

    // The palette picker and what opened it.
    private readonly Crystarium.PalettePicker _palette = new("appearance-palette");
    private CustomizeKey _paletteKey;
    private ActorId? _paletteActor;
    private readonly Dictionary<uint[], List<Vector4>> _paletteColors = new(ReferenceEqualityComparer.Instance);

    // One tile picker per clan, gender and feature: its dropped ids are
    // its own, and a clan's faces are not another's.
    private readonly Dictionary<(byte Clan, byte Gender, CustomizeKey Key), Crystarium.TexturePicker> _tilePickers = new();

    // ── the view ────────────────────────────────────────────────────────

    private void DrawCustomizeView(Crystarium.PageScope page, ActorId actor)
    {
        var glamourer = _integration.Glamourer;
        bool ready = glamourer.Available && _appearanceAccess.CanEdit;
        string? blocked = !_appearanceAccess.CanEdit ? _appearanceAccess.Detail : ready ? null : glamourer.Detail;
        var state = ready ? ReadCustomize(actor) : null;
        byte clan = (byte)(state?.Value(CustomizeKey.Clan) ?? 0);
        byte gender = (byte)(state?.Value(CustomizeKey.Gender) ?? 0);
        var menu = state is null ? null : _customize.Menu(clan, gender);
        bool live = ready && state is not null;
        string? why = !ready ? blocked : state is null ? (_customizeDetail ?? "The look could not be read.") : null;

        page.Section("Body", _openBody, next => _openBody = next, form =>
        {
            if (why is not null && _appearanceAccess.CanEdit)
                form.Status(why);
            BodyRow(form, actor, state, live, why);
            form.PairRows();
            form.Slider("Height", state?.Value(CustomizeKey.Height) ?? 0, 0f, 100f,
                value => Set(actor, CustomizeKey.Height, (int)MathF.Round(value), "Set height"),
                help: live ? "How tall" : why, disabled: !live, onBegin: _customizeSession.Seal);
            if (menu?.Feature(CustomizeKey.BustSize) is { } bust)
                form.Slider("Bust", state?.Value(CustomizeKey.BustSize) ?? 0, 0f, 100f,
                    value => Set(actor, CustomizeKey.BustSize, (int)MathF.Round(value), "Set bust size"),
                    help: live ? bust.Name : why, disabled: !live, onBegin: _customizeSession.Seal);
            if (menu?.Feature(CustomizeKey.MuscleMass) is { } muscle)
                form.Slider("Muscle", state?.Value(CustomizeKey.MuscleMass) ?? 0, 0f, 100f,
                    value => Set(actor, CustomizeKey.MuscleMass, (int)MathF.Round(value), "Set muscle"),
                    help: live ? muscle.Name : why, disabled: !live, onBegin: _customizeSession.Seal);
            form.EndPair();
        }, divider: false);

        page.Section("Face", _openFace, next => _openFace = next, form =>
        {
            TilesRow(form, actor, menu, state, live, why);
            form.Cells(cells =>
            {
                cells.Cell("Brows", cell => OptionCell(cell, actor, menu, state, CustomizeKey.Eyebrows, live, why));
                cells.Cell("Eyes", cell => OptionCell(cell, actor, menu, state, CustomizeKey.EyeShape, live, why));
                cells.Cell("Nose", cell => OptionCell(cell, actor, menu, state, CustomizeKey.Nose, live, why));
            }, help: "Step through the shapes the clan offers");
            form.Cells(cells =>
            {
                cells.Cell("Jaw", cell => OptionCell(cell, actor, menu, state, CustomizeKey.Jaw, live, why));
                cells.Cell("Mouth", cell => OptionCell(cell, actor, menu, state, CustomizeKey.Mouth, live, why));
                cells.Cell("Small iris", cell => cell.Switch("appearance-small-iris",
                    (state?.Value(CustomizeKey.SmallIris) ?? 0) != 0,
                    on => Set(actor, CustomizeKey.SmallIris, on ? Flag(CustomizeKey.SmallIris) : 0, on ? "Small iris" : "Large iris"),
                    !live, live ? "Smaller irises" : why));
            }, help: "Step through the shapes the clan offers");
            FeatureRow(form, actor, menu, state, live, why);
        });

        page.Section("Colours", _openColours, next => _openColours = next, form =>
        {
            bool highlights = (state?.Value(CustomizeKey.Highlights) ?? 0) != 0;
            bool lipstick = (state?.Value(CustomizeKey.Lipstick) ?? 0) != 0;
            form.Cells(cells =>
            {
                cells.Cell("Skin", cell => ColorCell(cell, actor, state, CustomizeKey.SkinColor, menu?.SkinColors, live, why));
                cells.Cell("Hair", cell => ColorCell(cell, actor, state, CustomizeKey.HairColor, menu?.HairColors, live, why));
                cells.Cell("Highlight", cell => ColorCell(cell, actor, state, CustomizeKey.HighlightsColor,
                    _customize.Palettes.Highlights, live && highlights, live && !highlights ? "Highlights are off" : why));
            }, help: "Each opens the game's own palette");
            form.Cells(cells =>
            {
                cells.Cell("Right eye", cell => ColorCell(cell, actor, state, CustomizeKey.EyeColorRight, _customize.Palettes.Eyes, live, why));
                cells.Cell("Left eye", cell => ColorCell(cell, actor, state, CustomizeKey.EyeColorLeft, _customize.Palettes.Eyes, live, why));
                cells.Cell("Lips", cell => ColorCell(cell, actor, state, CustomizeKey.LipColor,
                    _customize.Palettes.Lips, live && lipstick, live && !lipstick ? "Lipstick is off" : why));
            }, help: "Each eye has its own colour");
            form.Cells(cells =>
            {
                cells.Cell("Tattoo", cell => ColorCell(cell, actor, state, CustomizeKey.TattooColor, _customize.Palettes.Tattoo, live, why));
                cells.Cell("Paint", cell => ColorCell(cell, actor, state, CustomizeKey.FacePaintColor, _customize.Palettes.FacePaint, live, why));
            }, help: "The facial features' and the face paint's colour");
            form.Checkboxes("Options",
                new Crystarium.CheckItem("Highlights", highlights,
                    on => Set(actor, CustomizeKey.Highlights, on ? Flag(CustomizeKey.Highlights) : 0, on ? "Highlights on" : "Highlights off"),
                    live ? "Highlight the hair" : why, !live),
                new Crystarium.CheckItem("Lipstick", lipstick,
                    on => Set(actor, CustomizeKey.Lipstick, on ? Flag(CustomizeKey.Lipstick) : 0, on ? "Lipstick on" : "Lipstick off"),
                    live ? "Colour the lips" : why, !live),
                new Crystarium.CheckItem("Reversed paint", (state?.Value(CustomizeKey.FacePaintReversed) ?? 0) != 0,
                    on => Set(actor, CustomizeKey.FacePaintReversed, on ? Flag(CustomizeKey.FacePaintReversed) : 0, on ? "Reverse face paint" : "Face paint forward"),
                    live ? "Mirror the face paint" : why, !live));
        });
    }

    // ── body ────────────────────────────────────────────────────────────

    /// <summary>One dropdown of every clan — the race follows from it —
    /// then a Gender caption and the symbol that swaps it. Each redraws,
    /// so each is a disruptive step whose inverse is the values read
    /// before.</summary>
    private void BodyRow(
        Crystarium.FormScope form, ActorId actor, CustomizeState? state, bool live, string? why)
    {
        var clans = _customize.Clans;
        int clan = state?.Value(CustomizeKey.Clan) ?? 0;
        int gender = state?.Value(CustomizeKey.Gender) ?? 0;
        var clanNames = new string[clans.Count];
        int clanIndex = -1;
        for (int i = 0; i < clans.Count; i++)
        {
            clanNames[i] = clans[i].Name;
            if (clans[i].Clan == clan)
                clanIndex = i;
        }

        var theme = Crystarium.ActiveTheme;
        form.Custom("Clan", theme.Controls.FormRowHeight, row =>
        {
            float s = row.Scale;
            float gap = theme.Page.ActionGap * s;
            float square = theme.Controls.WorkspaceHeight;
            var captionStyle = new TextStyle
            {
                Size = theme.Typography.LabelSize,
                Color = theme.FormLabel,
                Disabled = !live,
            };
            const string caption = "Gender";
            float captionW = Crystarium.MeasureText(caption, captionStyle).X;
            float dropW = MathF.Max(1f, row.ControlWidth - gap - captionW - gap - square * s);
            var seat = row.CenterControl(square);
            ImGui.SetCursorScreenPos(seat);
            Crystarium.Dropdown("appearance-clan", clanNames, clanIndex, index =>
                Body(actor, state, "Change clan", new Dictionary<CustomizeKey, int>
                {
                    [CustomizeKey.Race] = clans[index].Race,
                    [CustomizeKey.Clan] = clans[index].Clan,
                }),
                ControlStyle.Workspace with { Width = UiWidth.Fixed(dropW / s) },
                !live || clans.Count == 0, live ? "The clan · redraws" : why,
                disruptive: true);
            Crystarium.TextInBand(
                new Vector2(seat.X + dropW + gap, row.Origin.Y),
                new Vector2(captionW, row.RowHeight * s),
                caption, captionStyle);
            ImGui.SetCursorScreenPos(new Vector2(seat.X + dropW + gap + captionW + gap, seat.Y));
            Crystarium.IconButton(
                gender == 1 ? TablerIcon.GenderFemale : TablerIcon.GenderMale,
                () => Body(actor, state, "Swap gender", new Dictionary<CustomizeKey, int>
                {
                    [CustomizeKey.Gender] = gender == 1 ? 0 : 1,
                }),
                ControlStyle.Square(square), !live,
                live ? (gender == 1 ? "Female · swap" : "Male · swap") : why,
                id: "appearance-gender", disruptive: true);
        }, help: "The clan and the gender redraw the actor");
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

    // ── tiles ───────────────────────────────────────────────────────────

    /// <summary>Face, hair, tail or ears, and face paint as cards two per
    /// line, the equipment card's shape: the icon two rows tall opens the
    /// grid; beside it the feature's own name on the first line and the
    /// plus and minus well on the second, stepping only through the
    /// values the clan has.</summary>
    private void TilesRow(
        Crystarium.FormScope form, ActorId actor, CustomizeMenu? menu,
        CustomizeState? state, bool live, string? why)
    {
        form.PairRows();
        foreach (var key in TileKeys)
            TileCard(form, actor, menu, state, key, live, why);
        form.EndPair();
    }

    private void TileCard(
        Crystarium.FormScope form, ActorId actor, CustomizeMenu? menu,
        CustomizeState? state, CustomizeKey key, bool live, string? why)
    {
        var theme = Crystarium.ActiveTheme;
        float tile = theme.Controls.FormRowHeight * 2f;
        form.Custom(TileName(key), tile, row =>
        {
            float s = row.Scale;
            float gap = theme.Page.ActionGap * s;
            float side = tile * s;
            float half = side * 0.5f;
            var origin = row.ControlOrigin;
            var feature = menu?.Feature(key);
            int current = state?.Value(key) ?? 0;
            var option = feature is null ? null : OptionOf(feature, current);
            bool has = live && feature is not null;
            string name = feature?.Name is { Length: > 0 } n ? n : TileName(key);

            bool redraws = key == CustomizeKey.Face;
            ImGui.SetCursorScreenPos(origin);
            bool opened = Crystarium.ImageTile(
                $"appearance-tile-{key}",
                option is { Icon: not 0 } ? ResolveIcon(option.Icon) : 0,
                tile,
                null,
                help: has ? (redraws ? name + " · redraws" : name) : (feature is null ? "Not for this clan" : why),
                disabled: !has,
                disruptive: redraws);
            if (opened && feature is not null && menu is not null)
                TilePicker(menu, feature).OpenAt((uint)current, ImGui.GetItemRectMin(), ImGui.GetItemRectMax());

            float x = origin.X + side + gap;
            float width = MathF.Max(1f, row.ControlWidth - side - gap);
            var nameStyle = new TextStyle
            {
                Size = theme.Typography.LabelSize,
                Color = theme.Text,
                Disabled = !has,
            };
            Crystarium.TextInBand(
                new Vector2(x, origin.Y),
                new Vector2(width, half),
                Crystarium.TruncateText(feature is null ? "—" : name, nameStyle, width),
                nameStyle);
            float square = theme.Controls.WorkspaceHeight;
            StepperAt(
                new Vector2(x, origin.Y + half + (half - square * s) * 0.5f), width, s,
                $"appearance-step-{key}", feature, current, has, why,
                next => Set(actor, key, next, $"Set {name.ToLowerInvariant()}"),
                disruptive: redraws);
        }, help: key switch
        {
            CustomizeKey.Face => "The face",
            CustomizeKey.Hairstyle => "The hair",
            CustomizeKey.TailShape => "The tail or the ears",
            _ => "The face paint",
        });
    }

    /// <summary>A named option — brows, eyes, nose — as a plus and minus
    /// well that steps only through the values the clan has.</summary>
    private void OptionCell(
        in Crystarium.FormPairCell cell, ActorId actor, CustomizeMenu? menu,
        CustomizeState? state, CustomizeKey key, bool live, string? why)
    {
        var theme = Crystarium.ActiveTheme;
        var feature = menu?.Feature(key);
        int current = state?.Value(key) ?? 0;
        var seat = cell.Center(theme.Controls.WorkspaceHeight);
        string name = feature?.Name is { Length: > 0 } n ? n : key.ToString();
        StepperAt(seat, cell.Width, cell.Scale, $"appearance-step-{key}", feature, current,
            live && feature is not null, feature is null ? "Not for this clan" : why,
            next => Set(actor, key, next, $"Set {name.ToLowerInvariant()}"));
    }

    /// <summary>[−] [value] [+] across a width: the well drags and snaps to
    /// the nearest valid value, the steppers walk the list.</summary>
    private void StepperAt(
        Vector2 at, float width, float s, string id, CustomizeFeature? feature,
        int current, bool enabled, string? why, Action<int> apply, bool disruptive = false)
    {
        var theme = Crystarium.ActiveTheme;
        float square = theme.Controls.WorkspaceHeight;
        float narrow = square;
        var stepStyle = ControlStyle.Square(square);
        float tight = theme.Spacing.One * s;
        float wellW = MathF.Max(1f, width - (narrow * s + tight) * 2f);
        var options = feature?.Options ?? Array.Empty<CustomizeOption>();
        int index = IndexOf(options, current);
        bool canDown = enabled && options.Count > 0 && index != 0;
        bool canUp = enabled && options.Count > 0 && index < options.Count - 1;
        string? help = enabled ? null : why;

        ImGui.SetCursorScreenPos(at);
        Crystarium.IconButton(TablerIcon.Minus,
            () => apply(options[index < 0 ? 0 : index - 1].Value),
            stepStyle, !canDown, help ?? "Previous", id: id + "-down", disruptive: disruptive);
        ImGui.SetCursorScreenPos(new Vector2(at.X + narrow * s + tight, at.Y));
        Crystarium.AxisWell(
            id + "-well",
            string.Empty,
            current,
            next =>
            {
                int snapped = Nearest(options, (int)MathF.Round(next));
                if (snapped != current)
                    apply(snapped);
            },
            null,
            theme.FormValue,
            0.05f,
            "0",
            ControlStyle.Workspace with { Width = UiWidth.Fixed(wellW / s) },
            disabled: !enabled || options.Count == 0);
        ImGui.SetCursorScreenPos(new Vector2(at.X + narrow * s + tight + wellW + tight, at.Y));
        Crystarium.IconButton(TablerIcon.Plus,
            () => apply(options[index < 0 ? 0 : index + 1].Value),
            stepStyle, !canUp, help ?? "Next", id: id + "-up", disruptive: disruptive);
    }

    /// <summary>The seven facial features and the legacy tattoo as one row
    /// of small icon tiles, each a toggle.</summary>
    private void FeatureRow(
        Crystarium.FormScope form, ActorId actor, CustomizeMenu? menu,
        CustomizeState? state, bool live, string? why)
    {
        var theme = Crystarium.ActiveTheme;
        float big = theme.Controls.FormRowHeight * 2f;
        float line = theme.Controls.FormRowHeight;
        form.Custom("Features", line + big, row =>
        {
            float s = row.Scale;
            float gap = theme.Spacing.Three * s;
            float side = MathF.Min(big * s, (row.Width - gap * 7f) / 8f);
            // The eight tiles stand UNDER the label's line, centred across
            // the whole row.
            float left = row.Origin.X + (row.Width - (side * 8f + gap * 7f)) * 0.5f;
            float top = row.Origin.Y + line * s;
            byte face = (byte)(state?.Value(CustomizeKey.Face) ?? 0);
            uint[]? icons = null;
            if (menu is not null && !menu.FaceFeatureIcons.TryGetValue(face, out icons))
                foreach (var any in menu.FaceFeatureIcons.Values)
                {
                    icons = any;
                    break;
                }
            icons ??= Array.Empty<uint>();
            for (int i = 0; i < 8; i++)
            {
                var key = i < 7 ? CustomizeKey.FacialFeature1 + i : CustomizeKey.LegacyTattoo;
                bool on = (state?.Value(key) ?? 0) != 0;
                nint texture = i < 7
                    ? (i < icons.Length ? ResolveIcon(icons[i]) : 0)
                    : LegacyTattooHandle();
                ImGui.SetCursorScreenPos(new Vector2(left + i * (side + gap), top));
                int index = i;
                Crystarium.ImageTile(
                    $"appearance-feature-{i}",
                    texture,
                    side / s,
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
    private void ColorCell(
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

    private static CustomizeOption? OptionOf(CustomizeFeature feature, int value)
    {
        foreach (var option in feature.Options)
            if (option.Value == value)
                return option;
        return null;
    }

    private static int IndexOf(IReadOnlyList<CustomizeOption> options, int value)
    {
        for (int i = 0; i < options.Count; i++)
            if (options[i].Value == value)
                return i;
        return -1;
    }

    private static int Nearest(IReadOnlyList<CustomizeOption> options, int value)
    {
        int best = value;
        int distance = int.MaxValue;
        foreach (var option in options)
        {
            int d = Math.Abs(option.Value - value);
            if (d < distance)
            {
                distance = d;
                best = option.Value;
            }
        }
        return best;
    }

    private static string TileName(CustomizeKey key) => key switch
    {
        CustomizeKey.Face => "Face",
        CustomizeKey.Hairstyle => "Hair",
        CustomizeKey.TailShape => "Tail",
        CustomizeKey.FacePaint => "Face paint",
        _ => key.ToString(),
    };

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

    /// <summary>The grid for a tile: the texture picker fed the feature's
    /// icons. A value without an icon still lists, as a bare tile.</summary>
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
        picker = new Crystarium.TexturePicker(
            $"appearance-{menu.Clan}-{menu.Gender}-{feature.Key}",
            (uint id, out nint handle, out Vector2 pixels) =>
            {
                pixels = Vector2.Zero;
                handle = 0;
                if (!icons.TryGetValue(id, out uint icon))
                    return TextureProbe.Missing;
                if (icon == 0)
                    return TextureProbe.Ready;
                handle = ResolveIcon(icon, out var probe);
                return probe;
            },
            top + 1,
            columns: 5,
            tileSize: 72f,
            rows: 6,
            knownIds: new List<uint>(icons.Keys));
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

    /// <summary>Runs the palette and the tile pickers and lands their picks
    /// on the actor captured when they opened.</summary>
    private void DrainCustomizePickers()
    {
        if (_palette.Draw() is { } index && _paletteActor is { } actor)
        {
            _customizeSession.Seal();
            Set(actor, _paletteKey, index, $"Set {ColorName(_paletteKey)}");
            _customizeSession.Seal();
        }
        foreach (var (key, picker) in _tilePickers)
        {
            if (picker.Draw() is not { } id || _customizeActor is not { } owner)
                continue;
            Set(owner, key.Key, (int)id, $"Set {TileName(key.Key).ToLowerInvariant()}");
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
