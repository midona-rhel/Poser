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
        bool adaptiveDisplay = false)
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
        // The wheel has to be CLAIMED, not merely read: every well sits inside
        // the shell's scrolling child, and an unclaimed notch would step the
        // value AND scroll the page out from under the pointer.
        // SetItemUsingMouseWheel is ImGui's own claim and it only takes hold
        // while the item is the hovered one, so a notch anywhere else still
        // scrolls normally.
        ImGuiP.SetItemUsingMouseWheel();
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

        // Wheel stepping (Brio ImBrio.Drag.cs:105-109, Ktisis
        // TransformTable.cs:210-228) with THIS control's own modifiers, so a
        // notch and a drag pixel scale by the same rule. A notch is a discrete
        // edit with no release to wait for, so it commits itself — one notch
        // is one undo step, which is what a stepper means.
        float wheel = ImGui.GetIO().MouseWheel;
        if (wheel != 0f && hit.Hovered && _axisEditId == null)
        {
            float next = value + wheel * perPixel * WheelStepPixels
                * DragModifierMultiplier(ImGui.GetIO());
            onChange(next);
            value = next;
            changed = true;
            onCommit?.Invoke();
        }

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

        // The edit shares the well's band with a TextInBand-seated axis
        // label, so the native value takes the same metric ink seat.
        // FramePadding cannot reseat text inside a fixed box (the frame's
        // height derives from it), so the padding keeps the line-box
        // value that makes the frame exactly the well's height, the FILL
        // is painted at the intended rect here, and the widget itself is
        // submitted risen with a transparent frame — value, caret, and
        // selection lift together while the visible box stays put.
        float rise = FontRegistry.InkRise(
            FontFamily.Mono, FontWeight.Regular,
            ActiveTheme.Typography.LabelSize) * scale;
        ImGui.GetWindowDrawList().AddRectFilled(
            pos + new Vector2(inputLeft, 0f),
            pos + new Vector2(size.X, size.Y),
            ImGui.ColorConvertFloat4ToU32(
                ColorEx.ApplyAlpha(ActiveTheme.Chrome.InputWell)),
            ActiveTheme.Radii.Small * scale);
        ImGui.SetCursorScreenPos(
            pos + new Vector2(inputLeft, rise));
        ImGui.SetNextItemWidth(MathF.Max(
            1f, size.X - inputLeft));
        if (_axisEditNeedsFocus)
            ImGui.SetKeyboardFocusHere();

        float verticalPadding = MathF.Max(
            0f,
            (size.Y - ImGui.GetTextLineHeight()) * 0.5f);
        // Same caret trim as TextInput: the native caret spans the line
        // box, and the dead band above the cap is scissored off so it
        // reads as the value's own height.
        float caretTrim = (FontRegistry.AscentOverCap(
                FontFamily.Mono, FontWeight.Regular,
                ActiveTheme.Typography.LabelSize)
            - CaretHeadroom) * scale;
        bool caretClipped = caretTrim > 0f;
        if (caretClipped)
            ImGui.GetWindowDrawList().PushClipRect(
                new Vector2(
                    pos.X + inputLeft,
                    pos.Y + rise + verticalPadding + caretTrim),
                pos + new Vector2(size.X, size.Y),
                true);
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
        if (caretClipped)
            ImGui.GetWindowDrawList().PopClipRect();
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
            ? accent with { W = 0.60f }
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
