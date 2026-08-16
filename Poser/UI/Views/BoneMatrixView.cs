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

/// <summary>Screen-space rectangle used by the retained matrix draw pass.
/// Layout is still calculated for every item, but only geometry intersecting
/// the active child viewport is handed to the draw sink.</summary>
internal readonly struct BoneMatrixGeometry
{
    public BoneMatrixGeometry(Vector2 min, Vector2 max)
    {
        Min = min;
        Max = max;
    }

    public Vector2 Min { get; }
    public Vector2 Max { get; }

    public bool Intersects(BoneMatrixClipRect clip) =>
        Max.X > clip.Min.X && Min.X < clip.Max.X
            && Max.Y > clip.Min.Y && Min.Y < clip.Max.Y;
}

/// <summary>The current visible bounds of the nested matrix child. This is
/// deliberately independent of the scrolled content origin: that origin moves
/// as the child scrolls, while the child's clip rectangle does not.</summary>
internal readonly struct BoneMatrixClipRect
{
    public BoneMatrixClipRect(Vector2 min, Vector2 max)
    {
        Min = min;
        Max = max;
    }

    public Vector2 Min { get; }
    public Vector2 Max { get; }
}

/// <summary>One shared production/test seam for Matrix emission. The
/// production ImGui sink and the counter-bearing contract-test sink both
/// consume the exact same retained layout traversal.</summary>
internal interface IBoneMatrixDrawSink
{
    void DrawSection(BoneMatrixSection section, BoneMatrixGeometry geometry);
    void DrawDivider(BoneMatrixGeometry geometry);
    void DrawRow(BoneMatrixRow row, BoneMatrixGeometry geometry);
    void DrawPill(BoneMatrixPill pill, BoneMatrixGeometry geometry);
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
        var sink = new ImGuiBoneMatrixDrawSink(vm, ImGui.GetWindowDrawList(), s);
        var windowMin = ImGui.GetWindowPos();
        var windowMax = windowMin + ImGui.GetWindowSize();
        // BeginChild owns the active nested-scroll clip. Do not use origin.Y
        // for the top: origin is content space and moves above the child as it
        // scrolls, while the child's clip rectangle does not. The content
        // width is already inset from the scrollbar.
        var clip = new BoneMatrixClipRect(
            new Vector2(MathF.Max(origin.X, windowMin.X), windowMin.Y),
            new Vector2(MathF.Min(origin.X + width, windowMax.X), windowMax.Y));

        ImGui.PushID(idPrefix);
        try
        {
            return DrawCore(vm, origin, width, s, columns, trackW, clip, ref sink);
        }
        finally
        {
            ImGui.PopID();
        }
    }

    /// <summary>Runs the same production layout/emission orchestration with a
    /// counter-bearing sink. Contract tests use this only to drive the exact
    /// path called by <see cref="Draw"/>; they do not test a disconnected
    /// helper or substitute the layout algorithm.</summary>
    internal static float DrawForTesting<TSink>(
        BoneMatrixViewModel vm,
        Vector2 origin,
        float width,
        float scale,
        BoneMatrixClipRect clip,
        ref TSink sink)
        where TSink : struct, IBoneMatrixDrawSink
    {
        var metrics = Crystarium.ActiveTheme.Matrix;
        int columns = Math.Max(1, (int)MathF.Floor(
            (width / scale + metrics.ColumnGap)
            / (metrics.MinimumTrackWidth + metrics.ColumnGap)));
        float trackW = (width / scale
            - metrics.ColumnGap * (columns - 1)) / columns;
        return DrawCore(vm, origin, width, scale, columns, trackW, clip, ref sink);
    }

    private static float DrawCore<TSink>(
        BoneMatrixViewModel vm,
        Vector2 origin,
        float width,
        float s,
        int columns,
        float trackW,
        BoneMatrixClipRect clip,
        ref TSink sink)
        where TSink : struct, IBoneMatrixDrawSink
    {
        var metrics = Crystarium.ActiveTheme.Matrix;
        float y = origin.Y;
        foreach (var section in vm.Sections)
        {
            var sectionGeometry = new BoneMatrixGeometry(
                new Vector2(origin.X, y),
                new Vector2(origin.X + width, y + 31f * s));
            if (sectionGeometry.Intersects(clip))
                sink.DrawSection(section, sectionGeometry);

            float lineY = y + 32f * s;
            var dividerGeometry = new BoneMatrixGeometry(
                new Vector2(origin.X, lineY),
                new Vector2(origin.X + width, lineY + 1f * s));
            if (dividerGeometry.Intersects(clip))
                sink.DrawDivider(dividerGeometry);
            y += 41f * s;

            // Row-major flow into `columns` tracks; wide rows take two slots.
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
                var rowGeometry = new BoneMatrixGeometry(
                    new Vector2(cellX, cellY),
                    new Vector2(cellX + cellW, cellY + metrics.RowHeight * s));

                if (rowGeometry.Intersects(clip))
                {
                    sink.DrawRow(row, rowGeometry);
                    float pillsW = (row.Pills.Count * metrics.PillSize
                        + (row.Pills.Count - 1) * metrics.PillGap) * s;
                    float x = cellX + cellW - pillsW;
                    foreach (var pill in row.Pills)
                    {
                        var pillGeometry = new BoneMatrixGeometry(
                            new Vector2(
                                x,
                                cellY + (metrics.RowHeight - metrics.PillSize) / 2f * s),
                            new Vector2(
                                x + metrics.PillSize * s,
                                cellY + (metrics.RowHeight + metrics.PillSize) / 2f * s));
                        if (pillGeometry.Intersects(clip))
                            sink.DrawPill(pill, pillGeometry);
                        x += (metrics.PillSize + metrics.PillGap) * s;
                    }
                }

                slot += span;
                gridRows = Math.Max(gridRows, gridRow + 1);
            }
            y = gridTop
                + (gridRows * (metrics.RowHeight + metrics.RowGap)
                    - metrics.RowGap) * s;
        }

        return y - origin.Y;
    }

    private readonly struct ImGuiBoneMatrixDrawSink : IBoneMatrixDrawSink
    {
        private readonly BoneMatrixViewModel _vm;
        private readonly ImDrawListPtr _drawList;
        private readonly float _scale;

        public ImGuiBoneMatrixDrawSink(
            BoneMatrixViewModel vm,
            ImDrawListPtr drawList,
            float scale)
        {
            _vm = vm;
            _drawList = drawList;
            _scale = scale;
        }

        public void DrawSection(BoneMatrixSection section, BoneMatrixGeometry geometry)
        {
            var style = new TextStyle
            {
                Size = Crystarium.ActiveTheme.Typography.CaptionSize,
                Weight = FontWeight.SemiBold,
                Color = TextSecondary,
            };
            Crystarium.TextAt(
                new Vector2(geometry.Min.X, geometry.Min.Y + 15f * _scale),
                section.Title,
                style);
            ImGui.SetCursorScreenPos(new Vector2(
                geometry.Min.X,
                geometry.Min.Y + 7f * _scale));
            ImGui.InvisibleButton(
                section.Id,
                new Vector2(
                    MathF.Min(
                        geometry.Max.X - geometry.Min.X,
                        Crystarium.MeasureText(section.Title, style).X
                            + 18f * _scale),
                    24f * _scale));
            if (ImGui.IsItemHovered())
                Crystarium.HoverHelp.Explain(
                    section.Id,
                    ImGui.GetItemRectMin(),
                    ImGui.GetItemRectMax(),
                    "Select every bone in this group · Ctrl adds to the selection");
            if (ImGui.IsItemClicked())
                _vm.OnSection?.Invoke(section, ImGui.GetIO().KeyCtrl);
        }

        public void DrawDivider(BoneMatrixGeometry geometry) => _drawList.AddRectFilled(
            geometry.Min,
            geometry.Max,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(BorderSecond)));

        public void DrawRow(BoneMatrixRow row, BoneMatrixGeometry geometry)
        {
            var metrics = Crystarium.ActiveTheme.Matrix;
            float pillsW = (row.Pills.Count * metrics.PillSize
                + (row.Pills.Count - 1) * metrics.PillGap) * _scale;
            float labelRight = geometry.Max.X - pillsW - 10f * _scale;
            var style = new TextStyle
            {
                Size = Crystarium.ActiveTheme.Typography.LabelSize,
                Color = TextSecondary,
            };
            float labelAvail = labelRight - geometry.Min.X;
            if (labelAvail > 0f)
                Crystarium.TextInBand(
                    geometry.Min,
                    new Vector2(labelAvail, metrics.RowHeight * _scale),
                    row.Label,
                    style,
                    TextConstraint.Truncate(labelAvail, TextAlign.End));
        }

        public void DrawPill(BoneMatrixPill pill, BoneMatrixGeometry geometry)
        {
            var metrics = Crystarium.ActiveTheme.Matrix;
            var center = (geometry.Min + geometry.Max) * 0.5f;
            float radius = metrics.PillSize / 2f * _scale;
            ImGui.SetCursorScreenPos(geometry.Min);
            ImGui.InvisibleButton(pill.Id, geometry.Max - geometry.Min);
            // Capture ALL item state immediately after this pill's button;
            // the label below submits another ImGui item.
            bool hovered = ImGui.IsItemHovered();
            bool clicked = ImGui.IsItemClicked();
            bool ctrl = ImGui.GetIO().KeyCtrl;
            bool shift = ImGui.GetIO().KeyShift;

            if (pill.Selected)
            {
                _drawList.AddCircleFilled(center, radius,
                    ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(Primary)));
                _drawList.AddCircle(center, radius - 0.5f * _scale,
                    ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(Primary)), 0, 1f * _scale);
            }
            else
            {
                if (hovered)
                    _drawList.AddCircleFilled(center, radius,
                        ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(SurfaceHover)));
                _drawList.AddCircle(center, radius - 0.5f * _scale,
                    ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(
                        hovered ? Primary50 : BorderPrimary)), 0, 1f * _scale);
            }

            if (pill.Label.Length > 0)
            {
                var style = new TextStyle
                {
                    Size = Crystarium.ActiveTheme.Typography.ShortcutSize,
                    Weight = FontWeight.SemiBold,
                    Family = FontFamily.Mono,
                    Color = pill.Selected ? TextPrimary : hovered ? TextPrimary : TextSecondary,
                };
                Crystarium.TextInBand(
                    geometry.Min,
                    geometry.Max - geometry.Min,
                    pill.Label,
                    style,
                    TextAlign.Center);
            }

            if (clicked)
                _vm.OnPill?.Invoke(pill, ctrl, shift);
        }
    }
}
