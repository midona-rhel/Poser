using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI.Views;

public sealed class BoneMatrixPill
{
    public string Label = "";
    public bool Selected;
    public object? Tag;
}

public sealed class BoneMatrixRow
{
    public string Label = "";
    public List<BoneMatrixPill> Pills = new();

    /// <summary>5+ pill clusters span two grid tracks (M2 `.mxRow.-wide`).</summary>
    public bool Wide => Pills.Count >= 5;
}

public sealed class BoneMatrixSection
{
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
/// Anamnesis-style grouped bone matrix — pixel transcription of the approved
/// docs/mockups/m2-properties.html `.mxWrap/.mxHead/.mxGrid/.mxRow/.mxPill`
/// grammar (itself transcribed from Anamnesis PoseMatrixView.xaml into picto
/// tokens). Dynamic auto-fit columns: `repeat(auto-fit, minmax(235px, 1fr))`
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
        string idPrefix = "mx",
        float zoom = 1f)
    {
        var metrics = Crystarium.ActiveTheme.Matrix;
        float layoutScale = ImGuiHelpers.GlobalScale;
        float s = layoutScale * zoom;
        float logicalWidth = width / layoutScale;
        float transformedWidth = logicalWidth * s;
        var dl = ImGui.GetWindowDrawList();

        // Responsive fit belongs to the unzoomed viewport. Zoom transforms
        // that stable arrangement; only a viewport resize may reflow it.
        int columns = Math.Max(1, (int)MathF.Floor(
            (logicalWidth + metrics.ColumnGap)
            / (metrics.MinimumTrackWidth + metrics.ColumnGap)));
        float trackW = (logicalWidth
            - metrics.ColumnGap * (columns - 1)) / columns;

        float y = origin.Y;
        int sectionIndex = 0;
        foreach (var section in vm.Sections)
        {
            // .mxHead box model: 14px pad-top, 11px caps (~13px line), 5px
            // pad-bottom → hairline at +32, rows begin at +41 (1px line + 8px margin).
            ViewText.Label(
                new Vector2(origin.X, y + 15f * s),
                section.Title,
                11f * zoom,
                FontWeight.SemiBold,
                TextSecondary);
            ImGui.SetCursorScreenPos(new Vector2(origin.X, y + 7f * s));
            ImGui.InvisibleButton($"##{idPrefix}-section-{sectionIndex}",
                new Vector2(
                    MathF.Min(
                        transformedWidth,
                        ViewText.Measure(
                            section.Title, 11f * zoom) + 18f * s),
                    24f * s));
            if (ImGui.IsItemHovered())
                Crystarium.HoverHelp.Explain($"bmv-section-{sectionIndex}",
                    ImGui.GetItemRectMin(), ImGui.GetItemRectMax(),
                    "Select every bone in this group · Ctrl adds to the selection");
            if (ImGui.IsItemClicked())
                vm.OnSection?.Invoke(section, ImGui.GetIO().KeyCtrl);
            float lineY = y + 32f * s;
            dl.AddRectFilled(new Vector2(origin.X, lineY),
                new Vector2(origin.X + transformedWidth, lineY + 1f * s),
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(BorderSecond)));
            y += 41f * s;

            // Row-major flow into `columns` tracks; wide rows take two slots.
            int slot = 0;
            float gridTop = y;
            int gridRows = 0;
            foreach (var row in section.Rows)
            {
                int span = row.Wide && columns > 1 ? 2 : 1;
                if (slot % columns + span > columns)
                    slot += columns - slot % columns; // wrap: wide row doesn't fit this line

                int gridRow = slot / columns;
                int gridCol = slot % columns;
                float cellX = origin.X
                    + gridCol * (trackW + metrics.ColumnGap) * s;
                float cellY = gridTop
                    + gridRow * (metrics.RowHeight + metrics.RowGap) * s;
                float cellW = (trackW * span
                    + metrics.ColumnGap * (span - 1)) * s;

                DrawRow(
                    vm, row, dl, new Vector2(cellX, cellY), cellW,
                    s, zoom, $"{idPrefix}-{sectionIndex}-{slot}");

                slot += span;
                gridRows = Math.Max(gridRows, gridRow + 1);
            }
            y = gridTop
                + (gridRows * (metrics.RowHeight + metrics.RowGap)
                    - metrics.RowGap) * s;
            sectionIndex++;
        }

        return y - origin.Y;
    }

    private static void DrawRow(BoneMatrixViewModel vm, BoneMatrixRow row, ImDrawListPtr dl,
        Vector2 pos, float width, float s, float zoom, string id)
    {
        var metrics = Crystarium.ActiveTheme.Matrix;
        // pills right-aligned; label fills the rest, right-aligned with ellipsis
        float pillsW = (row.Pills.Count * metrics.PillSize
            + (row.Pills.Count - 1) * metrics.PillGap) * s;
        float labelRight = pos.X + width - pillsW - 10f * s;

        string label = Ellipsize(
            row.Label,
            MathF.Max(0f, labelRight - pos.X),
            12f * zoom);
        float labelW = ViewText.Measure(label, 12f * zoom);
        ViewText.Label(
            new Vector2(
                MathF.Max(pos.X, labelRight - labelW),
                pos.Y + (metrics.RowHeight - 12f) / 2f * s - 2f * s),
            label, 12f * zoom, FontWeight.Regular, TextSecondary);

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
                $"##{id}-p{i}",
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
                float tw = ViewText.Measure(
                    pill.Label, 10f * zoom, FontWeight.SemiBold, mono: true);
                ViewText.Label(
                    new Vector2(center.X - tw / 2f, center.Y - 5f * s),
                    pill.Label,
                    10f * zoom,
                    FontWeight.SemiBold, pill.Selected ? TextPrimary : hovered ? TextPrimary : TextSecondary, mono: true);
            }

            if (clicked)
                vm.OnPill?.Invoke(pill, ctrl, shift);

            x += (metrics.PillSize + metrics.PillGap) * s;
            i++;
        }
    }

    private static string Ellipsize(string text, float available, float size)
    {
        if (ViewText.Measure(text, size) <= available)
            return text;
        const string ellipsis = "…";
        if (ViewText.Measure(ellipsis, size) > available)
            return "";
        int length = text.Length;
        while (length > 0
            && ViewText.Measure(text[..length] + ellipsis, size) > available)
            length--;
        return text[..length] + ellipsis;
    }
}
