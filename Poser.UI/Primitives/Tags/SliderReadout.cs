using System;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public static partial class Crystarium
{
    private static string? _readoutEditId;
    private static float _readoutEditValue;
    private static bool _readoutEditNeedsFocus;

    /// <summary>Three significant digits, stepped by magnitude: integers
    /// from one hundred up, one decimal through the tens, two below ten.
    /// The shared rule every slider readout states its value with — the
    /// full precision belongs to the edit, not the label.</summary>
    public static string AdaptiveValueText(float value)
    {
        float magnitude = MathF.Abs(value);
        return magnitude >= 99.995f
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : magnitude >= 9.9995f
                ? value.ToString("0.0", CultureInfo.InvariantCulture)
                : value.ToString("0.00", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A slider's value band: right-aligned caption text that a click turns
    /// into a numeric edit showing the FULL value. Display follows the
    /// adaptive three-digit rule (or the caller's readout); the edit
    /// commits clamped to the slider's own range, Escape cancels.
    /// </summary>
    public static void SliderReadout(
        string id,
        Vector2 origin,
        float width,
        float height,
        float value,
        float minimum,
        float maximum,
        Action<float> onCommit,
        Func<float, string>? readout = null,
        bool disabled = false)
    {
        var size = new Vector2(width, height);
        if (_readoutEditId == id && !disabled)
        {
            EditSliderReadout(id, origin, size, minimum, maximum, onCommit);
            return;
        }

        ImGui.SetCursorScreenPos(origin);
        var hit = Interactive.Reserve(id, size, disabled);
        if (hit.Clicked && !disabled)
        {
            _readoutEditId = id;
            _readoutEditValue = value;
            _readoutEditNeedsFocus = true;
        }

        DrawTextRight(
            origin, width, height,
            ActiveTheme.Typography.CaptionSize,
            FontFamily.Mono,
            FormLabelColor,
            readout is { } custom ? custom(value) : AdaptiveValueText(value));

        if (hit.Hovered && _readoutEditId == null && !disabled)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.TextInput);
            HoverHelp.Explain(
                id, origin, origin + size, "Click to type an exact value");
        }
    }

    private static void EditSliderReadout(
        string id,
        Vector2 origin,
        Vector2 size,
        float minimum,
        float maximum,
        Action<float> onCommit)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var mono = FontRegistry.Resolve(
            FontFamily.Mono, ActiveTheme.Typography.CaptionSize);
        bool fontPushed = mono is { Available: true };
        if (fontPushed)
            mono!.Push();

        // The edit is right-anchored on the band and may grow LEFT to hold
        // the full value: precision the label never shows must not be cut
        // off by the label's own width the moment it becomes editable.
        float pad = ActiveTheme.Spacing.Two * scale;
        string editText = _readoutEditValue.ToString(
            "0.######", CultureInfo.InvariantCulture);
        float editWidth = MathF.Max(
            size.X,
            ImGui.CalcTextSize(editText).X + pad * 4f);
        var editMin = new Vector2(origin.X + size.X - editWidth, origin.Y);
        var editMax = origin + size;

        ImGui.GetWindowDrawList().AddRectFilled(
            editMin, editMax,
            ImGui.ColorConvertFloat4ToU32(
                ColorEx.ApplyAlpha(ActiveTheme.Chrome.InputWell)),
            ActiveTheme.Radii.Small * scale);

        ImGui.SetCursorScreenPos(editMin);
        ImGui.SetNextItemWidth(editWidth);
        if (_readoutEditNeedsFocus)
            ImGui.SetKeyboardFocusHere();

        float verticalPadding = MathF.Max(
            0f, (size.Y - ImGui.GetTextLineHeight()) * 0.5f);
        ImGui.PushStyleVar(
            ImGuiStyleVar.FramePadding, new Vector2(pad, verticalPadding));
        ImGui.PushStyleVar(
            ImGuiStyleVar.FrameRounding, ActiveTheme.Radii.Small * scale);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
        ImGui.PushStyleColor(
            ImGuiCol.TextSelectedBg,
            ActiveTheme.Chrome.Primary with { W = 0.32f });
        ImGui.PushStyleColor(ImGuiCol.Text, ActiveTheme.Text);
        bool enter = ImGui.InputFloat(
            $"##readout-edit-{id}",
            ref _readoutEditValue,
            0f,
            0f,
            "%.6g",
            ImGuiInputTextFlags.AutoSelectAll
                | ImGuiInputTextFlags.EnterReturnsTrue);
        bool editedOnDeactivate = ImGui.IsItemDeactivatedAfterEdit();
        bool deactivated = ImGui.IsItemDeactivated();
        bool cancelled = ImGui.IsKeyPressed(ImGuiKey.Escape);
        ImGui.PopStyleColor(5);
        ImGui.PopStyleVar(2);
        if (fontPushed)
            mono!.Pop();
        _readoutEditNeedsFocus = false;

        if (cancelled)
        {
            _readoutEditId = null;
            return;
        }

        if (enter || editedOnDeactivate)
        {
            onCommit(Math.Clamp(_readoutEditValue, minimum, maximum));
            _readoutEditId = null;
            return;
        }

        if (deactivated)
            _readoutEditId = null;
    }
}
