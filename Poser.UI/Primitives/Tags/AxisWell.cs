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
        bool disabled = false)
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
                id, axis, value, onChange, onCommit, accent, format,
                pos, size, scale);

        var hit = Interactive.Reserve(id, size, disabled);
        bool changed = false;
        if (hit.DoubleClicked)
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

        DrawAxisWell(pos, size, axis, value, accent, format, hit.Active,
            disabled, scale);
        if (hit.Hovered && _axisEditId == null)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);
            HoverHelp.Explain(id, pos, pos + size,
                "Drag to adjust · Ctrl fine ×0.1 · Shift coarse ×10 · Double-click to type");
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
        ImGui.PushStyleColor(ImGuiCol.FrameBg, ActiveTheme.Chrome.InputWell);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, ActiveTheme.Chrome.InputWell);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, ActiveTheme.Chrome.InputWell);
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
        string format,
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
            ? accent with { W = 0.60f }
            : ActiveTheme.Chrome.ControlBorder;
        if (disabled)
        {
            fill.W *= ActiveTheme.Chrome.DisabledOpacity;
            border.W *= ActiveTheme.Chrome.DisabledOpacity;
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
        float axisY = pos.Y
            + (size.Y - axisSize.Y) * 0.5f
            + ActiveTheme.Optical.AxisText * scale;
        if (axis.Length > 0)
        {
            draw.PushClipRect(
                pos + new Vector2(inset),
                new Vector2(pos.X + axisSlot, max.Y - inset),
                true);
            draw.AddText(
                new Vector2(
                    pos.X + pad,
                    axisY),
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(accent)),
                axis);
            draw.PopClipRect();
        }
        if (drawValue)
        {
            string text =
                value.ToString(format, CultureInfo.InvariantCulture);
            var textSize = ImGui.CalcTextSize(text);
            float textY = pos.Y
                + (size.Y - textSize.Y) * 0.5f
                + ActiveTheme.Optical.AxisText * scale;
            draw.PushClipRect(
                new Vector2(pos.X + axisSlot, pos.Y + inset),
                max - new Vector2(inset),
                true);
            draw.AddText(
                new Vector2(
                    max.X - pad - textSize.X,
                    textY),
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(
                    disabled ? ActiveTheme.TextDim : ActiveTheme.Text)),
                text);
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
