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
        float height = ControlSizing.Height(
            style.Height, ActiveTheme.Controls.WorkspaceHeight);
        float width = ControlSizing.Width(
            style.Width, ActiveTheme.Form.ValueColumnWidth,
            ImGui.GetContentRegionAvail().X / scale);
        var pos = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, height) * scale;

        if (_axisEditId == id && !disabled)
            return EditAxisWell(
                id, axis, value, onChange, onCommit, accent, format,
                pos, size, scale);

        var hit = Interactive.Reserve(id, size, disabled);
        bool changed = false;
        if (hit.Hovered
            && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            _axisEditId = id;
            _axisEditValue = value;
            _axisEditNeedsFocus = true;
        }
        else if (hit.Active)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);
            float delta = ImGui.GetIO().MouseDelta.X;
            if (delta != 0f)
            {
                float next = value + delta * perPixel
                    * DragModifierMultiplier(ImGui.GetIO());
                onChange(next);
                value = next;
                changed = true;
            }
        }

        if (ImGui.IsItemDeactivated())
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
            focused: true, disabled: false, scale);

        float axisSlot = axis.Length == 0
            ? ActiveTheme.Spacing.Two
            : ActiveTheme.Spacing.Eight;
        var mono = FontRegistry.Resolve(
            FontFamily.Mono, ActiveTheme.Typography.LabelSize);
        bool fontPushed = mono is { Available: true };
        if (fontPushed)
            mono!.Push();

        ImGui.SetCursorScreenPos(pos + new Vector2(
            axisSlot * scale, ActiveTheme.Spacing.One * scale));
        ImGui.SetNextItemWidth(MathF.Max(
            1f, size.X - (axisSlot + ActiveTheme.Spacing.One) * scale));
        if (_axisEditNeedsFocus)
            ImGui.SetKeyboardFocusHere();

        ImGui.PushStyleVar(
            ImGuiStyleVar.FramePadding,
            new Vector2(ActiveTheme.Spacing.Two, ActiveTheme.Spacing.One) * scale);
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
        float scale)
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
        float pad = ActiveTheme.Spacing.Two * scale;
        float axisSlot = axis.Length == 0
            ? 0f
            : ActiveTheme.Spacing.Eight * scale;
        if (axis.Length > 0)
        {
            draw.PushClipRect(
                pos + new Vector2(inset),
                new Vector2(pos.X + axisSlot, max.Y - inset),
                true);
            draw.AddText(
                new Vector2(
                    pos.X + pad,
                    pos.Y + (size.Y - ImGui.CalcTextSize(axis).Y) * 0.5f),
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(accent)),
                axis);
            draw.PopClipRect();
        }
        string text = value.ToString(format, CultureInfo.InvariantCulture);
        var textSize = ImGui.CalcTextSize(text);
        draw.PushClipRect(
            new Vector2(pos.X + axisSlot, pos.Y + inset),
            max - new Vector2(inset),
            true);
        draw.AddText(
            new Vector2(
                max.X - pad - textSize.X,
                pos.Y + (size.Y - textSize.Y) * 0.5f
                    + ActiveTheme.Optical.DropdownText * scale),
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(
                disabled ? ActiveTheme.TextDim : ActiveTheme.Text)),
            text);
        draw.PopClipRect();
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
