using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI.Views;

public sealed class BoneMatrixPill
{
    /// <summary>This pill's ImGui id, minted with the row and never rebuilt.
    /// It is the PILL's identity, not its grid position: a viewport resize
    /// reflows the grid, and a positional id would hand every pill a new
    /// identity — and discard its interaction state — on every resize.
    /// </summary>
    public string Id = "";
    public string Label = "";
    public bool Selected;
    public object? Tag;
}

public sealed class BoneMatrixRow
{
    public string Label = "";
    public List<BoneMatrixPill> Pills = new();

    /// <summary>5+ pill clusters span two grid tracks (`.mxRow.-wide`).</summary>
    public bool Wide => Pills.Count >= 5;
}

public sealed class BoneMatrixSection
{
    /// <summary>The heading's ImGui id; see <see cref="BoneMatrixPill.Id"/>.
    /// </summary>
    public string Id = "";
    public string Title = "";
    public List<BoneMatrixRow> Rows = new();
}

public sealed class BoneMatrixViewModel
{
    public List<BoneMatrixSection> Sections = new();

    /// <summary>Pill clicked; second arg = additive (ctrl held).</summary>
    public Action<BoneMatrixPill, bool, bool>? OnPill;

    /// <summary>Section heading clicked; second arg = additive (ctrl held).</summary>
    public Action<BoneMatrixSection, bool>? OnSection;
}

/// <summary>
/// Anamnesis-style grouped bone matrix — pixel transcription of the m2
/// properties mockup's `.mxWrap/.mxHead/.mxGrid/.mxRow/.mxPill` grammar
/// (itself transcribed from Anamnesis PoseMatrixView.xaml into picto
/// tokens); the frozen mockup remains the only source for that grammar.
/// Dynamic auto-fit columns: `repeat(auto-fit, minmax(235px, 1fr))`
/// with 22px column / 2px row gaps; rows are 30px (label right-aligned,
/// 24px circular pills); wide clusters span two tracks. Selection state and
/// click routing come from the VM — the view stays service-free.
/// </summary>
public static class BoneMatrixView
{
    private static Vector4 TextPrimary =>
        Crystarium.ActiveTheme.Chrome.Text;
    private static Vector4 TextSecondary =>
        Crystarium.ActiveTheme.TextDim;
    private static Vector4 BorderPrimary =>
        Crystarium.ActiveTheme.Chrome.ControlBorder;
    private static Vector4 BorderSecond =>
        Crystarium.ActiveTheme.FormSeparator;
    private static Vector4 Primary =>
        Crystarium.ActiveTheme.Chrome.Primary;
    private static Vector4 Primary50 =>
        Crystarium.ActiveTheme.Chrome.PrimaryFocus;
    private static Vector4 SurfaceHover =>
        Crystarium.ActiveTheme.Chrome.ControlFill;

    /// <summary>Draws the matrix flowing downward from origin; returns total height.</summary>
    public static float Draw(
        BoneMatrixViewModel vm,
        Vector2 origin,
        float width,
        string idPrefix = "mx")
    {
        var metrics = Crystarium.ActiveTheme.Matrix;
        float s = ImGuiHelpers.GlobalScale;
        float logicalWidth = width / s;
        var dl = ImGui.GetWindowDrawList();

        // Only a viewport resize recomputes the responsive column fit.
        int columns = Math.Max(1, (int)MathF.Floor(
            (logicalWidth + metrics.ColumnGap)
            / (metrics.MinimumTrackWidth + metrics.ColumnGap)));
        float trackW = (logicalWidth
            - metrics.ColumnGap * (columns - 1)) / columns;

        // ONE id scope for the whole matrix instead of a per-element string
        // concatenation. The elements carry their own stable ids; the prefix
        // only has to keep two matrices drawn in one window apart, which is
        // exactly what an id scope is for. Building the row's
        // `$"{prefix}-{section}-{slot}"` and each pill's `$"##{row}-p{i}"`
        // minted a few hundred strings per frame — and hashed every one of
        // them fresh — for a table whose contents change only when the scene
        // does.
        ImGui.PushID(idPrefix);
        float y = origin.Y;
        try
        {
            foreach (var section in vm.Sections)
            {
                // .mxHead box model: 14px pad-top, 11px caps (~13px line), 5px
                // pad-bottom → hairline at +32, rows begin at +41 (1px line +
                // 8px margin).
                var sectionStyle = new TextStyle
                {
                    Size = Crystarium.ActiveTheme.Typography.CaptionSize,
                    Weight = FontWeight.SemiBold,
                    Color = TextSecondary,
                };
                Crystarium.TextAt(
                    new Vector2(origin.X, y + 15f * s), section.Title,
                    sectionStyle);
                ImGui.SetCursorScreenPos(new Vector2(origin.X, y + 7f * s));
                ImGui.InvisibleButton(
                    section.Id,
                    new Vector2(
                        MathF.Min(
                            width,
                            Crystarium.MeasureText(section.Title, sectionStyle).X
                                + 18f * s),
                        24f * s));
                if (ImGui.IsItemHovered())
                    Crystarium.HoverHelp.Explain(
                        section.Id,
                        ImGui.GetItemRectMin(),
                        ImGui.GetItemRectMax(),
                        "Select every bone in this group · Ctrl adds to the selection");
                if (ImGui.IsItemClicked())
                    vm.OnSection?.Invoke(section, ImGui.GetIO().KeyCtrl);
                float lineY = y + 32f * s;
                dl.AddRectFilled(
                    new Vector2(origin.X, lineY),
                    new Vector2(origin.X + width, lineY + 1f * s),
                    ImGui.ColorConvertFloat4ToU32(
                        ColorEx.ApplyAlpha(BorderSecond)));
                y += 41f * s;

                // Row-major flow into `columns` tracks; wide rows take two
                // slots.
                int slot = 0;
                float gridTop = y;
                int gridRows = 0;
                foreach (var row in section.Rows)
                {
                    int span = row.Wide && columns > 1 ? 2 : 1;
                    // wrap: a wide row does not fit this line
                    if (slot % columns + span > columns)
                        slot += columns - slot % columns;

                    int gridRow = slot / columns;
                    int gridCol = slot % columns;
                    float cellX = origin.X
                        + gridCol * (trackW + metrics.ColumnGap) * s;
                    float cellY = gridTop
                        + gridRow * (metrics.RowHeight + metrics.RowGap) * s;
                    float cellW = (trackW * span
                        + metrics.ColumnGap * (span - 1)) * s;

                    DrawRow(vm, row, dl, new Vector2(cellX, cellY), cellW, s);

                    slot += span;
                    gridRows = Math.Max(gridRows, gridRow + 1);
                }
                y = gridTop
                    + (gridRows * (metrics.RowHeight + metrics.RowGap)
                        - metrics.RowGap) * s;
            }
        }
        finally
        {
            ImGui.PopID();
        }

        return y - origin.Y;
    }

    private static void DrawRow(BoneMatrixViewModel vm, BoneMatrixRow row, ImDrawListPtr dl,
        Vector2 pos, float width, float s)
    {
        var metrics = Crystarium.ActiveTheme.Matrix;
        // pills right-aligned; label fills the rest, right-aligned with ellipsis
        float pillsW = (row.Pills.Count * metrics.PillSize
            + (row.Pills.Count - 1) * metrics.PillGap) * s;
        float labelRight = pos.X + width - pillsW - 10f * s;

        var labelStyle = new TextStyle
        {
            Size = Crystarium.ActiveTheme.Typography.LabelSize,
            Color = TextSecondary,
        };
        float labelAvail = labelRight - pos.X;
        if (labelAvail > 0f)
            Crystarium.TextInBand(
                new Vector2(pos.X, pos.Y),
                new Vector2(labelAvail, metrics.RowHeight * s),
                row.Label, labelStyle,
                TextConstraint.Truncate(labelAvail, TextAlign.End));

        float x = pos.X + width - pillsW;
        int i = 0;
        foreach (var pill in row.Pills)
        {
            var center = new Vector2(
                x + metrics.PillSize / 2f * s,
                pos.Y + metrics.RowHeight / 2f * s);
            float radius = metrics.PillSize / 2f * s;

            ImGui.SetCursorScreenPos(new Vector2(
                x,
                pos.Y + (metrics.RowHeight - metrics.PillSize) / 2f * s));
            ImGui.InvisibleButton(
                pill.Id,
                new Vector2(metrics.PillSize, metrics.PillSize) * s);
            // Capture ALL item state for THIS pill immediately after its
            // InvisibleButton: the label below submits another ImGui item, so
            // a later IsItemClicked() would belong to it — the round-1
            // "clicking a pill selects the previous pill" defect.
            bool hovered = ImGui.IsItemHovered();
            bool clicked = ImGui.IsItemClicked();
            bool ctrl = ImGui.GetIO().KeyCtrl;
            bool shift = ImGui.GetIO().KeyShift;

            if (pill.Selected)
            {
                dl.AddCircleFilled(center, radius, ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(Primary)));
                dl.AddCircle(center, radius - 0.5f * s, ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(Primary)), 0, 1f * s);
            }
            else
            {
                if (hovered)
                    dl.AddCircleFilled(center, radius, ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(SurfaceHover)));
                dl.AddCircle(center, radius - 0.5f * s,
                    ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(hovered ? Primary50 : BorderPrimary)), 0, 1f * s);
            }

            if (pill.Label.Length > 0)
            {
                var pillLabelStyle = new TextStyle
                {
                    Size = Crystarium.ActiveTheme.Typography.ShortcutSize,
                    Weight = FontWeight.SemiBold,
                    Family = FontFamily.Mono,
                    Color = pill.Selected ? TextPrimary : hovered ? TextPrimary : TextSecondary,
                };
                Crystarium.TextInBand(
                    new Vector2(center.X - radius, center.Y - radius),
                    new Vector2(metrics.PillSize, metrics.PillSize) * s,
                    pill.Label, pillLabelStyle,
                    TextAlign.Center);
            }

            if (clicked)
                vm.OnPill?.Invoke(pill, ctrl, shift);

            x += (metrics.PillSize + metrics.PillGap) * s;
            i++;
        }
    }
}
