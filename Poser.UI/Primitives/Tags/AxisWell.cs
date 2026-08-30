using System;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public static partial class Crystarium
{
    private static string? _axisEditId;
    private static float _axisEditValue;
    private static bool _axisEditNeedsFocus;

    /// <summary>One wheel notch is worth this many drag pixels. Four puts the
    /// step within a hair of Ktisis' own (its 0.2°/px rotation speed × 10 =
    /// 2.0° a notch; Poser's 0.5°/px × 4 = 2.0°) without giving any caller a
    /// second speed to keep in sync with its drag rate.</summary>
    private const float WheelStepPixels = 4f;

    public static bool AxisWell(
        string id,
        string axis,
        float value,
        Action<float> onChange,
        Action? onCommit,
        Vector4 accent,
        float perPixel,
        string format,
        ControlStyle style = default,
        bool disabled = false,
        bool adaptiveDisplay = false,
        float? altReset = null)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var metrics = ControlSizing.Resolve(
            style,
            ActiveTheme.Form.ValueColumnWidth,
            ActiveTheme.Controls.WorkspaceHeight);
        var pos = ImGui.GetCursorScreenPos();
        var size = metrics.Size;

        if (_axisEditId == id && !disabled)
            return EditAxisWell(
                id, axis, value, onChange, onCommit, accent,
                adaptiveDisplay ? "0.######" : format,
                pos, size, scale);

        var hit = Interactive.Reserve(id, size, disabled);
        bool changed = false;
        // Alt-click resets to the stated default — the slider's own
        // gesture, spoken by every value control that HAS a default.
        if (hit.Clicked && ImGui.GetIO().KeyAlt
            && altReset is { } fallback && !disabled)
        {
            if (value != fallback)
            {
                onChange(fallback);
                value = fallback;
                changed = true;
            }
            onCommit?.Invoke();
        }
        else if (hit.DoubleClicked)
        {
            _axisEditId = id;
            _axisEditValue = value;
            _axisEditNeedsFocus = true;
        }
        else if (hit.Active)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);
            float delta = hit.DragDelta.X;
            if (delta != 0f)
            {
                float next = value + delta * perPixel
                    * DragModifierMultiplier(ImGui.GetIO());
                onChange(next);
                value = next;
                changed = true;
            }
        }

        if (hit.DragEnded)
            onCommit?.Invoke();

        // NO wheel stepping: the wheel belongs to the page scroll, and a
        // well that stepped on a notch hijacked it (the Brio behaviour was
        // removed 2026-08-30 — only the pose preview and the viewports
        // read the wheel).

        // The label follows the adaptive three-digit rule when asked; the
        // EDIT above always carries the full value — precision belongs to
        // typing, not to the resting label.
        DrawAxisWell(
            pos, size, axis, value, accent,
            adaptiveDisplay ? null : format,
            hit.Active, disabled, scale);
        if (hit.Hovered && _axisEditId == null)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);
            HoverHelp.Explain(id, pos, pos + size,
                "Drag · double-click to edit");
        }
        return changed;
    }

    public static void CancelAxisEdit()
    {
        _axisEditId = null;
        _axisEditNeedsFocus = false;
    }

    private static bool EditAxisWell(
        string id,
        string axis,
        float value,
        Action<float> onChange,
        Action? onCommit,
        Vector4 accent,
        string format,
        Vector2 pos,
        Vector2 size,
        float scale)
    {
        DrawAxisWell(pos, size, axis, _axisEditValue, accent, format,
            focused: true, disabled: false, scale, drawValue: false);

        var mono = FontRegistry.Resolve(
            FontFamily.Mono, ActiveTheme.Typography.LabelSize);
        bool fontPushed = mono is { Available: true };
        if (fontPushed)
            mono!.Push();
        float horizontalPadding =
            ActiveTheme.Form.AxisWellHorizontalPadding;
        float axisWidth = axis.Length == 0
            ? 0f
            : ImGui.CalcTextSize(axis).X / scale;
        float axisSlot = axis.Length == 0
            ? horizontalPadding
            : horizontalPadding
                + axisWidth
                + ActiveTheme.Form.AxisLabelGap;
        float horizontalPaddingPx = horizontalPadding * scale;
        float axisSlotPx = axisSlot * scale;
        string editText = _axisEditValue.ToString(
            format,
            CultureInfo.InvariantCulture);
        float inputLeft = MathF.Max(
            axisSlotPx,
            size.X
                - ImGui.CalcTextSize(editText).X
                - horizontalPaddingPx * 2f);

        // The EDIT centres by ImGui's own line height, with no metric
        // rise and no caret scissor: the selection highlight and caret
        // then exactly hug the text, which is what a focused input looks
        // like. The file-metric seating bought sub-pixel alignment with
        // the resting label and cost a visibly misplaced highlight after
        // the Roboto switch (its cap dead band is 4.4px, Cascadia's was
        // 3.0).
        ImGui.GetWindowDrawList().AddRectFilled(
            pos + new Vector2(inputLeft, 0f),
            pos + new Vector2(size.X, size.Y),
            ImGui.ColorConvertFloat4ToU32(
                ColorEx.ApplyAlpha(ActiveTheme.Chrome.InputWell)),
            ActiveTheme.Radii.Small * scale);
        ImGui.SetCursorScreenPos(
            pos + new Vector2(inputLeft, 0f));
        ImGui.SetNextItemWidth(MathF.Max(
            1f, size.X - inputLeft));
        if (_axisEditNeedsFocus)
            ImGui.SetKeyboardFocusHere();

        float verticalPadding = MathF.Max(
            0f,
            (size.Y - ImGui.GetTextLineHeight()) * 0.5f);
        ImGui.PushStyleVar(
            ImGuiStyleVar.FramePadding,
            new Vector2(
                horizontalPaddingPx,
                verticalPadding));
        ImGui.PushStyleVar(
            ImGuiStyleVar.FrameRounding,
            ActiveTheme.Radii.Small * scale);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
        ImGui.PushStyleColor(
            ImGuiCol.TextSelectedBg,
            ActiveTheme.Chrome.Primary with { W = 0.32f });
        ImGui.PushStyleColor(ImGuiCol.Text, ActiveTheme.Text);
        bool enter = ImGui.InputFloat(
            $"##axis-edit-{id}",
            ref _axisEditValue,
            0f,
            0f,
            InputFloatFormat(format),
            ImGuiInputTextFlags.AutoSelectAll
                | ImGuiInputTextFlags.EnterReturnsTrue);
        bool editedOnDeactivate = ImGui.IsItemDeactivatedAfterEdit();
        bool deactivated = ImGui.IsItemDeactivated();
        bool cancelled = ImGui.IsKeyPressed(ImGuiKey.Escape);
        ImGui.PopStyleColor(5);
        ImGui.PopStyleVar(2);
        if (fontPushed)
            mono!.Pop();
        _axisEditNeedsFocus = false;

        if (cancelled)
        {
            CancelAxisEdit();
            return false;
        }

        if (enter || editedOnDeactivate)
        {
            onChange(_axisEditValue);
            onCommit?.Invoke();
            CancelAxisEdit();
            return _axisEditValue != value;
        }

        if (deactivated)
            CancelAxisEdit();
        return false;
    }

    private static void DrawAxisWell(
        Vector2 pos,
        Vector2 size,
        string axis,
        float value,
        Vector4 accent,
        string? format,
        bool focused,
        bool disabled,
        float scale,
        bool drawValue = true)
    {
        var draw = ImGui.GetWindowDrawList();
        var max = pos + size;
        float radius = ActiveTheme.Radii.Small * scale;
        var fill = ActiveTheme.Chrome.InputWell;
        var border = focused
            ? accent with { W = 0.85f }
            : ActiveTheme.Chrome.ControlBorder;
        if (disabled)
        {
            fill = fill.Fade(ActiveTheme.Chrome.DisabledOpacity);
            border = border.Fade(ActiveTheme.Chrome.DisabledOpacity);
        }
        draw.AddRectFilled(
            pos, max,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(fill)),
            radius);
        float inset = 0.5f * scale;
        draw.AddRect(
            pos + new Vector2(inset),
            max - new Vector2(inset),
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(border)),
            MathF.Max(0f, radius - inset),
            ImDrawFlags.None,
            scale);

        var mono = FontRegistry.Resolve(
            FontFamily.Mono, ActiveTheme.Typography.LabelSize);
        bool pushed = mono is { Available: true };
        if (pushed)
            mono!.Push();
        float pad =
            ActiveTheme.Form.AxisWellHorizontalPadding * scale;
        var axisSize = ImGui.CalcTextSize(axis);
        float axisSlot = axis.Length == 0
            ? pad
            : pad
                + axisSize.X
                + ActiveTheme.Form.AxisLabelGap * scale;
        // The well's own mono face stays pushed for the slot geometry
        // above; TextInBand resolves the same handle and ink-centers both
        // runs on the well.
        var wellStyle = new TextStyle
        {
            Size = ActiveTheme.Typography.LabelSize,
            Family = FontFamily.Mono,
        };
        if (axis.Length > 0)
        {
            draw.PushClipRect(
                pos + new Vector2(inset),
                new Vector2(pos.X + axisSlot, max.Y - inset),
                true);
            TextInBand(
                new Vector2(pos.X + pad, pos.Y),
                new Vector2(axisSize.X, size.Y),
                axis,
                wellStyle with { Color = accent });
            draw.PopClipRect();
        }
        if (drawValue)
        {
            string text = format is { } fixedFormat
                ? value.ToString(fixedFormat, CultureInfo.InvariantCulture)
                : AdaptiveValueText(value);
            draw.PushClipRect(
                new Vector2(pos.X + axisSlot, pos.Y + inset),
                max - new Vector2(inset),
                true);
            TextInBand(
                new Vector2(pos.X + axisSlot, pos.Y),
                new Vector2(max.X - pad - (pos.X + axisSlot), size.Y),
                text,
                wellStyle with
                {
                    Color = disabled ? ActiveTheme.TextDim : ActiveTheme.Text,
                },
                TextAlign.End);
            draw.PopClipRect();
        }
        if (pushed)
            mono!.Pop();
    }

    private static float DragModifierMultiplier(ImGuiIOPtr io) =>
        io.KeyCtrl && io.KeyShift ? 1f
        : io.KeyCtrl ? 0.1f
        : io.KeyShift ? 10f
        : 1f;

    private static string InputFloatFormat(string displayFormat)
    {
        int dot = displayFormat.IndexOf('.');
        int decimals = dot < 0 ? 0 : displayFormat.Length - dot - 1;
        return $"%.{decimals}f";
    }
}
