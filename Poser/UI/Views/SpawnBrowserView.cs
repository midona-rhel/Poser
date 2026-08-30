using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI.Views;

/// <summary>The browser's category strips. All is the whole flat list; the
/// rest are the plus buttons' own groups, so a section plus can open its
/// group directly. The minion/mount/accessory catalog lives under Actors —
/// catalog spawns ARE actors — with the badge stating the kind; Props holds
/// the prop entry (a prop catalog arrives later, user 2026-08-11).</summary>
public enum SpawnBrowserTab
{
    All,
    Actors,
    Lights,
    Cameras,
    Props,

    /// <summary>Everything this browser lays OVER the game rather than into
    /// the scene: the three game-UI overlay nodes — the dialogue panel, the
    /// chat bubble and the status line — and the reference picture, which is
    /// an overlay by the same test (user 2026-08-14: "shouldn't these spawn
    /// images be under overlays? It's technically an overlay"). Brio files the
    /// picture under its spawn menu's "Other" group instead
    /// (<c>Brio/UI/Controls/Editors/SpawnMenu.cs:268-280</c>); a group named
    /// for one entry is not a group.</summary>
    Overlays,

    // The world had a tab here — a refreshable list of nearby actors to clone.
    // It is gone (user 2026-08-15: "the world thing on the plus should just be
    // removed"). The world is answered IN the world now: the footer's class
    // glyphs mark what is addable and the marks themselves are the rows.
}

/// <summary>
/// One row of the flat spawn list. Every string it needs is minted when the
/// list is built — the ImGui id, the label, and the label lowercased for the
/// filter scan — so no frame builds one. A catalog row states an icon id and a
/// badge; an action row states a glyph and neither.
/// </summary>
public readonly record struct SpawnBrowserRow(
    string Id,
    string Label,
    string LabelLower,
    TablerIcon Glyph,
    uint IconId,
    string? Badge,
    bool Disabled,
    string? Help = null);

public sealed class SpawnBrowserViewModel
{
    /// <summary>Every spawnable, the action rows first. Built once.</summary>
    public readonly List<SpawnBrowserRow> Rows = new();

    /// <summary>Indices into <see cref="Rows"/> the query kept. Refilled in
    /// place on a query change and read unchanged on every other frame.
    /// </summary>
    public readonly List<int> Visible = new();

    public string Query = string.Empty;

    /// <summary>The active <see cref="SpawnBrowserTab"/>, as its index.
    /// </summary>
    public int Tab;

    public Action<int>? OnTab;

    /// <summary>Pinned stays open when focus leaves; unpinned closes.
    /// </summary>
    public bool Pinned;

    public Action? OnPinToggle;

    /// <summary>Whether what this browser adds arrives frozen. It sits in the
    /// browser's own chrome rather than in Settings because it qualifies the
    /// act every row here performs, and the toggle IS the persisted setting.
    /// </summary>
    public bool Frozen;

    public Action? OnFrozenToggle;

    /// <summary>The footer caption: the honest count, or the note explaining
    /// why the last activation did nothing.</summary>
    public string Status = string.Empty;

    public Action<string>? OnQuery;

    /// <summary>Told an index into <see cref="Rows"/>. A disabled row never
    /// reaches it.</summary>
    public Action<int>? OnActivate;

    public Action? OnClose;

    /// <summary>Resolves a row's game icon. Called per visible row per frame:
    /// shared texture wraps must be re-resolved, so this can never answer with
    /// a stored handle.</summary>
    public Func<uint, nint>? ResolveIcon;

    // Hoisted once per model: the frame's chrome must not mint a closure, and
    // all of these close over nothing but this model.
    internal Action<Crystarium.ScrollRegionScope>? List;
    internal Action<Crystarium.ActionBarScope>? Footer;
    internal Action<Crystarium.ActionBarScope>? Header;
    internal Action<WindowFrameRect>? TitleContent;
}

/// <summary>
/// The spawn browser: the shared <see cref="Crystarium.WindowFrame"/> is the
/// whole chassis — chrome, title bar, the search band under it and the footer —
/// and this view fills the band and the body. No rail: the list is FLAT by
/// design, because one search over everything spawnable is the affordance.
///
/// <para>The body is the picker's row shape (Compositions/SearchPicker) at the
/// same pitch, clipped, minus the check slot no row here carries.</para>
/// </summary>
public static class SpawnBrowserView
{
    public const float DesignHeight = 520f;

    /// <summary>The window's width floor: room for the search row's field
    /// plus its two icons even if the tab strip ever narrows.</summary>
    private const float MinWidth = 320f;

    /// <summary>The window is BUILT AROUND the tab strip: its logical width
    /// is the strip plus the content inset each side (user 2026-08-11:
    /// "build this around the width of the props spawner"). Callable
    /// wherever an ImGui frame is current (PreDraw included).</summary>
    public static float MeasureWidth()
    {
        var theme = Crystarium.ActiveTheme;
        float scale = ImGuiHelpers.GlobalScale;
        float inset = theme.Scrollbar.GutterWidth * RowBarShare + RowPadding;
        float tabs = Crystarium.MeasureSegmentedControl(
            TabIcons, TabText).X / scale;
        return MathF.Max(MinWidth, tabs + inset * 2f);
    }

    private const string SearchId = "##spawn-browser-search";
    private const string ListId = "##spawn-browser-list";

    /// <summary>The band under the title bar, which is also FilterPill's own
    /// natural search height.</summary>
    private const float SearchBandHeight = 36f;

    /// <summary>The tab strip's row above the search, inside the same band:
    /// the pill's own measured height plus one spacing step above and below,
    /// so the strip breathes off the title bar over it and the list under it
    /// instead of sitting hard against both (user 2026-08-15: "add a little
    /// top and bottom padding for the tabs"). Measured rather than stated, so
    /// the padding stays the padding whatever the pill's height becomes.
    /// </summary>
    private static float TabBandHeight =>
        Crystarium.MeasureSegmentedControl(TabIcons, TabText).Y
            / ImGuiHelpers.GlobalScale
        + Crystarium.ActiveTheme.Spacing.Three * 2f;

    /// <summary>The tab strip is the SAME segmented pill every other tab
    /// strip uses — the MIXED variant: six text tabs made this window
    /// super wide, so the kinds wear their icons and only "All" keeps its
    /// word (short, and no glyph says it better). Order matches
    /// <see cref="SpawnBrowserTab"/>; the labels survive as the icon
    /// tabs' hovers.</summary>
    private static readonly string[] TabLabels =
    [
        "All",
        "Actors",
        "Lights",
        "Cameras",
        "Objects",
        "Overlays",
    ];

    /// <summary>Positional against <see cref="TabLabels"/>; index 0 is
    /// covered by the text stand-in and never drawn.</summary>
    private static readonly TablerIcon[] TabIcons =
    [
        TablerIcon.Circle,
        TablerIcon.User,
        TablerIcon.Bulb,
        TablerIcon.Camera,
        TablerIcon.Diamond,
        TablerIcon.Message,
    ];

    private static readonly Func<int, string?> TabText =
        static index => index == 0 ? "All" : null;

    private static readonly Func<int, string?> TabHelp =
        static index => index == 0 ? null : TabLabels[index];

    /// <summary>The freeze/pin/close side in the search row.</summary>
    private const float HeaderButtonSide = 26f;

    /// <summary>How many icons sit right of the search field: the freeze
    /// toggle, the pin, and the frame's own close. The search field is sized
    /// around this, so a fourth icon has to be counted here too.</summary>
    private const int HeaderButtonCount = 3;

    /// <summary>The PILL's own height, and the 2px it breathes off each
    /// neighbour — together the pitch the clipper steps at.</summary>
    private const float RowHeight = 28f;

    private const float RowPillVGap = 2f;

    private const float RowPitch = RowHeight + RowPillVGap * 2f;

    /// <summary>The picker's square inset inside the pill: half the difference
    /// between the pill and its 14px slot.</summary>
    private const float RowPadding = (RowHeight - 14f) * 0.5f;

    /// <summary>The bar is HALF the shell gutter, and the pill breathes that
    /// same half against the body's left edge.</summary>
    private const float RowBarShare = 0.5f;

    /// <summary>The list breathes against the band above and the footer below.
    /// </summary>
    private const float ListVPad = 4f;

    /// <summary>FilterPill's own left pad; the band's margin tops it up to the
    /// row content base.</summary>
    private const float SearchInnerPad = 10f;

    private static readonly Action<string> IgnoreQuery = static _ => { };

    public static void Draw(SpawnBrowserViewModel vm, Vector2 origin)
    {
        ArgumentNullException.ThrowIfNull(vm);
        var theme = Crystarium.ActiveTheme;
        float scale = ImGuiHelpers.GlobalScale;
        float width = MeasureWidth();
        var size = new Vector2(width, DesignHeight) * scale;

        // Enter is sampled BEFORE the body opens its scroll child, so the gate
        // is the host window's focus rather than whichever child owns the
        // cursor. FilterPill exposes no submit callback. Repeat is off: a held
        // key must not spawn once per repeat tick.
        bool submit = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)
            && (ImGui.IsKeyPressed(ImGuiKey.Enter, repeat: false)
                || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, repeat: false));

        vm.List ??= region => DrawRows(vm, region);
        vm.Footer ??= scope => scope.Label(vm.Status);
        vm.Header ??= right =>
        {
            right.Icon(
                TablerIcon.PlayerPause,
                () => vm.OnFrozenToggle?.Invoke(),
                vm.Frozen
                    ? "New actors arrive frozen on their first frame. Click "
                        + "to let them play."
                    : "Freeze what you add — every actor this browser "
                        + "spawns, clones or captures stops on its first "
                        + "frame instead of playing.",
                style: new ControlStyle { Selected = vm.Frozen });
            right.Icon(
                TablerIcon.Pin,
                () => vm.OnPinToggle?.Invoke(),
                vm.Pinned
                    ? "Pinned — the window stays open. Click to unpin."
                    : "Pin the window open — unpinned, it closes when it "
                        + "loses focus.",
                style: new ControlStyle { Selected = vm.Pinned });
        };
        vm.TitleContent ??= rect => DrawSearchInTitle(vm, rect);

        // THE window frame, exactly as before the title went: the search
        // field IS the title-bar content now, the tab strip is the band,
        // and the frame owns every rule, fill and hover treatment (user
        // 2026-08-11: the hand-drawn chassis "doesn't look quite right").
        var rects = Crystarium.WindowFrame(
            "spawn-browser",
            origin,
            size,
            new WindowFrameProps
            {
                Title = string.Empty,
                TitleContent = vm.TitleContent,
                HeaderRight = vm.Header,
                OnClose = vm.OnClose,
                CloseHelp = "Close",
                BandHeight = TabBandHeight,
                FooterLeft = vm.Footer,
            });

        DrawTabs(vm, rects.Band, scale, theme);
        DrawBody(vm, rects.Body, scale, theme);

        if (submit)
            ActivateFirstEnabled(vm);
    }

    /// <summary>The tab strip row, under the search. The strip SPANS the
    /// row on the rows' own insets — a natural-width icon strip left a
    /// small island in a wide band, which read as broken — and the fixed
    /// width hands each tab an equal share of the slack.</summary>
    private static void DrawTabs(
        SpawnBrowserViewModel vm, WindowFrameRect band, float scale,
        Theme theme)
    {
        float inset =
            (theme.Scrollbar.GutterWidth * RowBarShare + RowPadding) * scale;
        float width = MathF.Max(1f, band.Size.X - inset * 2f);
        var style = ControlStyle.Workspace with
        { Width = UiWidth.Fixed(width / scale) };
        var size = Crystarium.MeasureSegmentedControl(
            TabIcons, TabText, style);
        ImGui.SetCursorScreenPos(new Vector2(
            band.Min.X + inset,
            band.Min.Y + (TabBandHeight * scale - size.Y) * 0.5f));
        Crystarium.SegmentedControl(
            "##spawn-browser-tabs",
            TabIcons,
            TabText,
            vm.Tab,
            chosen => vm.OnTab?.Invoke(chosen),
            style: style,
            alignFirstTabToCursor: true,
            itemHelp: TabHelp);
    }

    /// <summary>The title bar's content: the search field, sized to leave
    /// the frame's right icon cluster (pin + close) its room.</summary>
    private static void DrawSearchInTitle(
        SpawnBrowserViewModel vm, WindowFrameRect bar)
    {
        var theme = Crystarium.ActiveTheme;
        float scale = ImGuiHelpers.GlobalScale;
        float width = bar.Size.X / scale;
        float pillInset = theme.Scrollbar.GutterWidth * RowBarShare;
        // The margin makes up FilterPill's own pad, so the search glyph sits
        // over the row marks and the search text over the labels.
        float margin = MathF.Max(0f, pillInset + RowPadding - SearchInnerPad);
        float cluster = theme.Floating.HeaderInset
            + HeaderButtonSide * HeaderButtonCount
            + theme.Spacing.Three * HeaderButtonCount;
        ImGui.SetCursorScreenPos(bar.Min + new Vector2(
            margin * scale,
            (bar.Size.Y - SearchBandHeight * scale) * 0.5f));
        Crystarium.FilterPill(
            SearchId,
            vm.Query,
            vm.OnQuery ?? IgnoreQuery,
            "Search",
            new ControlStyle
            {
                Width = UiWidth.Region(width - margin - cluster),
            });
    }

    private static void DrawBody(
        SpawnBrowserViewModel vm, WindowFrameRect body, float scale, Theme theme)
    {
        ImGui.SetCursorScreenPos(body.Min);
        // The DEFAULT full gutter: the half-share bar this passed before
        // rendered a scrollbar too thin to see (user 2026-08-11).
        Crystarium.ScrollRegion(
            ListId,
            body.Size.X / scale,
            body.Size.Y / scale,
            vm.List!);
    }

    private static void DrawRows(
        SpawnBrowserViewModel vm, Crystarium.ScrollRegionScope region)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var theme = Crystarium.ActiveTheme;
        float pillInset = theme.Scrollbar.GutterWidth * RowBarShare;
        float pillWidth = MathF.Max(0f, region.ContentWidth - pillInset);

        // The rows place themselves; ImGui's ambient vertical spacing would
        // inflate the scrolled extent past the last one.
        var spacing = ImGui.GetStyle().ItemSpacing;
        ImGui.PushStyleVar(
            ImGuiStyleVar.ItemSpacing, new Vector2(spacing.X, 0f));
        try
        {
            float pad = ListVPad * scale;
            ImGui.Dummy(new Vector2(0f, pad));
            if (vm.Visible.Count == 0)
            {
                CaptionLine("No matches.", pillWidth, scale, theme);
            }
            else
            {
                // Clipped at the pitch, so a catalog of thousands submits only
                // the band the viewport shows.
                var clipper = new ImGuiListClipper();
                clipper.Begin(vm.Visible.Count, RowPitch * scale);
                while (clipper.Step())
                {
                    for (int i = clipper.DisplayStart;
                         i < clipper.DisplayEnd;
                         i++)
                        Row(vm, vm.Visible[i], pillWidth, scale, theme);
                }
                clipper.End();
            }
            // Trailing breathing is INVISIBLE to ImGui's scroll extent — no
            // item covers it — so max-scroll would pin the last pill to the
            // viewport edge without this.
            ImGui.Dummy(new Vector2(0f, pad));
        }
        finally
        {
            ImGui.PopStyleVar();
        }
    }

    private static void Row(
        SpawnBrowserViewModel vm,
        int index,
        float pillWidth,
        float scale,
        Theme theme)
    {
        var row = vm.Rows[index];
        var draw = ImGui.GetWindowDrawList();
        var bandMin = ImGui.GetCursorScreenPos();
        float pillInset = theme.Scrollbar.GutterWidth * RowBarShare;
        var pillMin = new Vector2(
            bandMin.X + pillInset * scale,
            bandMin.Y + RowPillVGap * scale);
        var pillSize = new Vector2(pillWidth, RowHeight) * scale;

        ImGui.SetCursorScreenPos(pillMin);
        // Disabled rows are INERT: Reserve reports neither hover nor click for
        // them, so the fill and the activation below are unreachable.
        var hit = Interactive.Reserve(row.Id, pillSize, row.Disabled);
        ImGui.SetCursorScreenPos(
            new Vector2(bandMin.X, bandMin.Y + RowPitch * scale));

        if (hit.Hovered || hit.Active)
            draw.AddRectFilled(
                pillMin,
                pillMin + pillSize,
                ImGui.ColorConvertFloat4ToU32(
                    ColorEx.ApplyAlpha(theme.Chrome.WeakOverlay)),
                theme.Radii.Control * scale);

        float gap = theme.Spacing.Three * scale;
        float x = pillMin.X + RowPadding * scale;
        float centerY = pillMin.Y + pillSize.Y * 0.5f;

        // The catalog row's game icon, or the action row's glyph. Same slot
        // either way, which is what keeps every label on one line.
        float side = theme.Controls.IconSize * scale;
        var markMin = theme.Optical.Snap(
            new Vector2(x, centerY - side * 0.5f));
        nint texture = row.IconId != 0 && vm.ResolveIcon is { } resolve
            ? resolve(row.IconId)
            : 0;
        if (texture != 0)
            draw.AddImage(
                new ImTextureID(texture),
                markMin,
                markMin + new Vector2(side),
                Vector2.Zero,
                Vector2.One,
                ImGui.ColorConvertFloat4ToU32(
                    ColorEx.ApplyAlpha(Vector4.One)));
        else
            Crystarium.IconIn(
                markMin,
                markMin + new Vector2(side),
                row.Glyph,
                theme.Text,
                disabled: row.Disabled);
        x += side + gap;

        float contentRight = pillMin.X + pillSize.X - RowPadding * scale;
        float labelRight = contentRight;
        if (row.Badge is { } badge)
        {
            var badgeStyle = new TextStyle
            {
                Size = theme.Typography.CaptionSize,
                Family = FontFamily.Mono,
                Color = theme.FormLabel,
            };
            float width = Crystarium.MeasureText(badge, badgeStyle).X;
            labelRight = contentRight - width - gap;
            Crystarium.TextInBand(
                new Vector2(contentRight - width, pillMin.Y),
                new Vector2(width, pillSize.Y),
                badge,
                badgeStyle,
                TextAlign.Start,
                besideIcon: true);
        }

        if (labelRight > x)
            LabelInBand(
                new Vector2(x, pillMin.Y),
                new Vector2(labelRight - x, pillSize.Y),
                row.Label,
                new TextStyle
                {
                    Size = theme.Typography.BodySize,
                    Color = row.Disabled ? theme.TextDim : theme.Text,
                });

        // A disabled row has no live item to hover, so its help falls back to
        // the geometric test — which is the only state that usually needs one.
        if (row.Help is { Length: > 0 } help &&
            (hit.Hovered ||
                (row.Disabled &&
                    Crystarium.HoverHelp.HelpHovered(
                        pillMin, pillMin + pillSize))))
            Crystarium.HoverHelp.Explain(
                row.Id, pillMin, pillMin + pillSize, help);

        if (hit.Clicked)
            vm.OnActivate?.Invoke(index);
    }

    /// <summary>One caption on a row band — the empty state and the
    /// activation note both use it — padded to where the labels above it
    /// would have started.</summary>
    private static void CaptionLine(
        string text, float pillWidth, float scale, Theme theme)
    {
        var bandMin = ImGui.GetCursorScreenPos();
        float pillInset = theme.Scrollbar.GutterWidth * RowBarShare;
        float left = pillInset + RowPadding + theme.Controls.IconSize
            + theme.Spacing.Three;
        LabelInBand(
            new Vector2(
                bandMin.X + left * scale, bandMin.Y + RowPillVGap * scale),
            new Vector2(
                MathF.Max(0f, pillInset + pillWidth - left) * scale,
                RowHeight * scale),
            text,
            new TextStyle
            {
                Size = theme.Typography.CaptionSize,
                Color = theme.FormHint,
            });
        ImGui.Dummy(new Vector2(0f, RowPitch * scale));
    }

    /// <summary>Band-centred label, constrained ONLY on overflow: the truncate
    /// clip's snapped edge shaves a fitting run's descender otherwise.
    /// </summary>
    private static void LabelInBand(
        Vector2 min, Vector2 band, string text, in TextStyle style)
    {
        if (!(band.X > 0f))
            return;
        if (Crystarium.MeasureText(text, style).X <= band.X)
            Crystarium.TextInBand(
                min, band, text, style, TextAlign.Start, besideIcon: true);
        else
            Crystarium.TextInBand(
                min, band, text, style, TextConstraint.Truncate(band.X),
                TextAlign.Start, besideIcon: true);
    }

    private static void ActivateFirstEnabled(SpawnBrowserViewModel vm)
    {
        for (int i = 0; i < vm.Visible.Count; i++)
        {
            int index = vm.Visible[i];
            if (vm.Rows[index].Disabled)
                continue;
            vm.OnActivate?.Invoke(index);
            return;
        }
    }
}
