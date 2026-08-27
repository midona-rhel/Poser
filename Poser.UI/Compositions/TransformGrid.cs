using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public static partial class Crystarium
{
    private const float TransformLegendRise = 12f;
    private const float TransformBoxPad = 6f;
    private const float TransformRowGap = 6f;
    private const float TransformBottomMargin = 8f;

    /// <summary>The grid's logical row height, stated so the hosting form
    /// row can reserve it exactly — the bottom margin included, so the
    /// next row breathes.</summary>
    public static float TransformGridHeight => TransformGridHeightFor(3);

    /// <summary>The logical height for a grid of the given row count —
    /// the transform presentation is UNIVERSAL across inspectors, and a
    /// camera's grid carries different rows than an actor's.</summary>
    public static float TransformGridHeightFor(int rowCount) =>
        TransformLegendRise + TransformBoxPad * 2f
        + ActiveTheme.Controls.WorkspaceHeight * rowCount
        + TransformRowGap * MathF.Max(0, rowCount - 1)
        + TransformBottomMargin;

    /// <summary>
    /// The inspector's transform: rows are translate / rotate / scale
    /// wearing the toolbar's own icons instead of word labels; columns are
    /// the axes, each wrapped in its color-coded rounded box that rises
    /// above the grid and carries its letter in a CUTOUT of the top border
    /// — a fieldset legend. The wells inside carry no letters; the column
    /// says it once.
    /// </summary>
    public static void TransformGrid(
        string id,
        Vector2 origin,
        float width,
        ReadOnlySpan<(TablerIcon Icon, string Name)> rows,
        Func<int, int, float> value,
        Action<int, int, float> onChange,
        Action<int> onCommit,
        Func<int, float> perPixel,
        Func<int, string> format,
        Func<int, bool> disabled)
    {
        var theme = ActiveTheme;
        float s = ImGuiHelpers.GlobalScale;
        float rowH = theme.Controls.WorkspaceHeight * s;
        float rowGap = TransformRowGap * s;
        float pad = TransformBoxPad * s;
        float rise = TransformLegendRise * s;
        float iconSide = 18f * s;
        float margin = theme.Spacing.Three * s;
        float boxGap = theme.Spacing.Three * s;
        float boxW = MathF.Max(
            1f, (width - iconSide - margin - boxGap * 2f) / 3f);
        float boxH = rise + pad * 2f + rowH * rows.Length
            + rowGap * MathF.Max(0, rows.Length - 1);
        bool allDisabled = true;
        for (int r = 0; r < rows.Length; r++)
            if (!disabled(r))
                allDisabled = false;
        float gridTop = origin.Y + rise;

        for (int r = 0; r < rows.Length; r++)
        {
            float y = gridTop + pad + r * (rowH + rowGap);
            IconIn(
                new Vector2(origin.X, y),
                new Vector2(origin.X + iconSide, y + rowH),
                rows[r].Icon,
                theme.FormLabel,
                contentScale: 0.9f,
                disabled: disabled(r));
            if (ImGui.IsMouseHoveringRect(
                    new Vector2(origin.X, y),
                    new Vector2(origin.X + iconSide, y + rowH)))
                HoverHelp.Explain(
                    Ids.Join(id, "-row-", rows[r].Name),
                    new Vector2(origin.X, y),
                    new Vector2(origin.X + iconSide, y + rowH),
                    rows[r].Name);
        }

        Span<Vector4> accents =
        [
            theme.Palette.AxisX,
            theme.Palette.AxisY,
            theme.Palette.AxisZ,
        ];
        ReadOnlySpan<string> letters = ["X", "Y", "Z"];
        for (int a = 0; a < 3; a++)
        {
            float x = origin.X + iconSide + margin + a * (boxW + boxGap);
            DrawAxisFieldset(
                new Vector2(x, origin.Y),
                new Vector2(boxW, boxH),
                letters[a],
                accents[a],
                s,
                allDisabled);
            for (int r = 0; r < rows.Length; r++)
            {
                float y = gridTop + pad + r * (rowH + rowGap);
                ImGui.SetCursorScreenPos(new Vector2(x + pad, y));
                int row = r;
                int axis = a;
                AxisWell(
                    Ids.Join(Ids.Join(id, "-r", row), "-a", axis),
                    string.Empty,
                    value(row, axis),
                    next => onChange(row, axis, next),
                    () => onCommit(row),
                    accents[axis],
                    perPixel(row),
                    format(row),
                    ControlStyle.Workspace with
                    {
                        Width = UiWidth.Fixed((boxW - pad * 2f) / s),
                    },
                    disabled(row));
            }
        }
    }

    /// <summary>One axis column's box: a single open border path running
    /// clockwise from one side of the legend cutout to the other, with the
    /// larger radius on the raised top corners, and the axis letter seated
    /// in the gap.</summary>
    private static void DrawAxisFieldset(
        Vector2 min,
        Vector2 size,
        string letter,
        Vector4 accent,
        float s,
        bool disabled)
    {
        var theme = ActiveTheme;
        var dl = ImGui.GetWindowDrawList();
        var max = min + size;
        float radiusTop = 9f * s;
        float radiusBottom = 5f * s;
        var line = disabled
            ? accent.Fade(theme.Chrome.DisabledOpacity)
            : accent with { W = 0.55f };
        uint packed = ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(line));

        var letterStyle = new TextStyle
        {
            Size = theme.Typography.CaptionSize,
            Family = FontFamily.Mono,
            Weight = FontWeight.SemiBold,
            Color = disabled
                ? accent.Fade(theme.Chrome.DisabledOpacity)
                : accent,
        };
        float letterWidth = MeasureText(letter, letterStyle).X;
        float cx = min.X + size.X * 0.5f;
        float gap = letterWidth * 0.5f + 4f * s;

        dl.PathClear();
        dl.PathLineTo(new Vector2(cx + gap, min.Y));
        dl.PathArcTo(
            new Vector2(max.X - radiusTop, min.Y + radiusTop),
            radiusTop, -MathF.PI * 0.5f, 0f);
        dl.PathArcTo(
            new Vector2(max.X - radiusBottom, max.Y - radiusBottom),
            radiusBottom, 0f, MathF.PI * 0.5f);
        dl.PathArcTo(
            new Vector2(min.X + radiusBottom, max.Y - radiusBottom),
            radiusBottom, MathF.PI * 0.5f, MathF.PI);
        dl.PathArcTo(
            new Vector2(min.X + radiusTop, min.Y + radiusTop),
            radiusTop, MathF.PI, MathF.PI * 1.5f);
        dl.PathLineTo(new Vector2(cx - gap, min.Y));
        dl.PathStroke(packed, ImDrawFlags.None, 1f * s);

        float band = theme.Controls.WorkspaceHeight * s;
        TextInBand(
            new Vector2(cx - letterWidth * 0.5f, min.Y - band * 0.5f),
            new Vector2(letterWidth, band),
            letter,
            letterStyle);
    }
}
