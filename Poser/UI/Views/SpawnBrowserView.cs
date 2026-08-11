using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI.Views;

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
    // both of these close over nothing but this model.
    internal Action<Crystarium.ActionBarScope>? Footer;
    internal Action<Crystarium.ScrollRegionScope>? List;
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
    public const float DesignWidth = 440f;
    public const float DesignHeight = 580f;

    private const string SearchId = "##spawn-browser-search";
    private const string ListId = "##spawn-browser-list";

    /// <summary>The band under the title bar, which is also FilterPill's own
    /// natural search height.</summary>
    private const float SearchBandHeight = 36f;

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

    /// <summary>The clear cross breathes off the gutter instead of sitting
    /// flush against it.</summary>
    private const float SearchClearPad = 6f;

    /// <summary>FilterPill's own left pad; the band's margin tops it up to the
    /// row content base.</summary>
    private const float SearchInnerPad = 10f;

    private static readonly Action<string> IgnoreQuery = static _ => { };

    public static void Draw(SpawnBrowserViewModel vm, Vector2 origin)
    {
        ArgumentNullException.ThrowIfNull(vm);
        var theme = Crystarium.ActiveTheme;
        float scale = ImGuiHelpers.GlobalScale;
        var size = new Vector2(DesignWidth, DesignHeight) * scale;

        // Enter is sampled BEFORE the body opens its scroll child, so the gate
        // is the host window's focus rather than whichever child owns the
        // cursor. FilterPill exposes no submit callback. Repeat is off: a held
        // key must not spawn once per repeat tick.
        bool submit = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)
            && (ImGui.IsKeyPressed(ImGuiKey.Enter, repeat: false)
                || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, repeat: false));

        vm.Footer ??= scope => scope.Label(vm.Status);
        vm.List ??= region => DrawRows(vm, region);

        var rects = Crystarium.WindowFrame(
            "spawn-browser",
            origin,
            size,
            new WindowFrameProps
            {
                Title = "Add to scene",
                OnClose = vm.OnClose,
                CloseHelp = "Close",
                BandHeight = SearchBandHeight,
                FooterLeft = vm.Footer,
            });

        DrawSearch(vm, rects.Band, scale, theme);
        DrawBody(vm, rects.Body, scale, theme);

        if (submit)
            ActivateFirstEnabled(vm);
    }

    /// <summary>The band's content: the frame owns the band and its rule, this
    /// owns the field seated in it.</summary>
    private static void DrawSearch(
        SpawnBrowserViewModel vm, WindowFrameRect band, float scale, Theme theme)
    {
        float inset = theme.Scrollbar.GutterWidth;
        float pillInset = inset * RowBarShare;
        // The margin makes up FilterPill's own pad, so the search glyph sits
        // over the row marks and the search text over the labels.
        float margin = MathF.Max(0f, pillInset + RowPadding - SearchInnerPad);
        ImGui.SetCursorScreenPos(band.Min + new Vector2(margin * scale, 0f));
        Crystarium.FilterPill(
            SearchId,
            vm.Query,
            vm.OnQuery ?? IgnoreQuery,
            "Search minions, mounts, accessories",
            new ControlStyle
            {
                Width = UiWidth.Fixed(
                    DesignWidth - margin - inset - SearchClearPad),
            });
    }

    private static void DrawBody(
        SpawnBrowserViewModel vm, WindowFrameRect body, float scale, Theme theme)
    {
        ImGui.SetCursorScreenPos(body.Min);
        // Half-width bar: bar + its padding = the pill's left edge.
        Crystarium.ScrollRegion(
            ListId,
            body.Size.X / scale,
            body.Size.Y / scale,
            vm.List!,
            theme.Scrollbar.GutterWidth * RowBarShare);
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
                EmptyLine(pillWidth, scale, theme);
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

    /// <summary>The list's empty state: one caption on a row band, padded to
    /// where the labels above it would have started.</summary>
    private static void EmptyLine(float pillWidth, float scale, Theme theme)
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
            "No matches.",
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
