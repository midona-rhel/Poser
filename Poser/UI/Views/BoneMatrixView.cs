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
    private static readonly Vector4 TextPrimary   = new(1f, 1f, 1f, 1f);
    private static readonly Vector4 TextSecondary = new(1f, 1f, 1f, 0.72f);
    private static readonly Vector4 BorderPrimary = new(1f, 1f, 1f, 0.14f);
    private static readonly Vector4 BorderSecond  = new(1f, 1f, 1f, 0.08f);
    private static readonly Vector4 Primary       = new(50 / 255f, 151 / 255f, 1f, 1f);
    private static readonly Vector4 Primary50     = new(50 / 255f, 151 / 255f, 1f, 0.5f);
    private static readonly Vector4 SurfaceHover  = new(1f, 1f, 1f, 0.05f);

    private const float MinTrack = 235f;
    private const float ColGap = 22f;
    private const float RowH = 30f;
    private const float RowGap = 2f;
    private const float PillSize = 24f;
    private const float PillGap = 6f;

    /// <summary>Draws the matrix flowing downward from origin; returns total height.</summary>
    public static float Draw(BoneMatrixViewModel vm, Vector2 origin, float width, string idPrefix = "mx")
    {
        float s = ImGuiHelpers.GlobalScale;
        var dl = ImGui.GetWindowDrawList();

        int columns = Math.Max(1, (int)MathF.Floor((width / s + ColGap) / (MinTrack + ColGap)));
        float trackW = (width / s - ColGap * (columns - 1)) / columns;

        float y = origin.Y;
        int sectionIndex = 0;
        foreach (var section in vm.Sections)
        {
            // .mxHead box model: 14px pad-top, 11px caps (~13px line), 5px
            // pad-bottom → hairline at +32, rows begin at +41 (1px line + 8px margin).
            ViewText.Label(new Vector2(origin.X, y + 15f * s), section.Title, 11f, FontWeight.SemiBold, TextSecondary);
            ImGui.SetCursorScreenPos(new Vector2(origin.X, y + 7f * s));
            ImGui.InvisibleButton($"##{idPrefix}-section-{sectionIndex}",
                new Vector2(MathF.Min(width, ViewText.Measure(section.Title, 11f) + 18f * s), 24f * s));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Select every bone in this group");
            if (ImGui.IsItemClicked())
                vm.OnSection?.Invoke(section, ImGui.GetIO().KeyCtrl);
            float lineY = y + 32f * s;
            dl.AddRectFilled(new Vector2(origin.X, lineY), new Vector2(origin.X + width, lineY + 1f * s),
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
                float cellX = origin.X + gridCol * (trackW + ColGap) * s;
                float cellY = gridTop + gridRow * (RowH + RowGap) * s;
                float cellW = (trackW * span + ColGap * (span - 1)) * s;

                DrawRow(vm, row, dl, new Vector2(cellX, cellY), cellW, s, $"{idPrefix}-{sectionIndex}-{slot}");

                slot += span;
                gridRows = Math.Max(gridRows, gridRow + 1);
            }
            y = gridTop + (gridRows * (RowH + RowGap) - RowGap) * s;
            sectionIndex++;
        }

        return y - origin.Y;
    }

    private static void DrawRow(BoneMatrixViewModel vm, BoneMatrixRow row, ImDrawListPtr dl,
        Vector2 pos, float width, float s, string id)
    {
        // pills right-aligned; label fills the rest, right-aligned with ellipsis
        float pillsW = (row.Pills.Count * PillSize + (row.Pills.Count - 1) * PillGap) * s;
        float labelRight = pos.X + width - pillsW - 10f * s;

        float labelW = ViewText.Measure(row.Label, 12f);
        ViewText.Label(new Vector2(MathF.Max(pos.X, labelRight - labelW), pos.Y + (RowH - 12f) / 2f * s - 2f * s),
            row.Label, 12f, FontWeight.Regular, TextSecondary);

        float x = pos.X + width - pillsW;
        int i = 0;
        foreach (var pill in row.Pills)
        {
            var center = new Vector2(x + PillSize / 2f * s, pos.Y + RowH / 2f * s);
            float radius = PillSize / 2f * s;

            ImGui.SetCursorScreenPos(new Vector2(x, pos.Y + (RowH - PillSize) / 2f * s));
            ImGui.InvisibleButton($"##{id}-p{i}", new Vector2(PillSize, PillSize) * s);
            bool hovered = ImGui.IsItemHovered();

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
                float tw = ViewText.Measure(pill.Label, 10f, FontWeight.SemiBold, mono: true);
                ViewText.Label(new Vector2(center.X - tw / 2f, center.Y - 5f * s), pill.Label, 10f,
                    FontWeight.SemiBold, pill.Selected ? TextPrimary : hovered ? TextPrimary : TextSecondary, mono: true);
            }

            if (ImGui.IsItemClicked())
                vm.OnPill?.Invoke(pill, ImGui.GetIO().KeyCtrl, ImGui.GetIO().KeyShift);

            x += (PillSize + PillGap) * s;
            i++;
        }
    }
}
