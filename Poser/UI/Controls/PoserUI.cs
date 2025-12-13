using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI.Controls;

/// <summary>
/// Common UI layout helpers for Poser controls.
/// </summary>
public static class PoserUI
{
    private const float DefaultLabelWidth = 140f;
    private const float RowSpacing = 15f;
    private const float Margin = 16f;

    /// <summary>
    /// Gets the UI scale.
    /// </summary>
    public static float Scale => ImGuiHelpers.GlobalScale;

    /// <summary>
    /// Creates a new row builder for flexible row layouts.
    /// </summary>
    /// <param name="height">Height of the row.</param>
    /// <returns>A RowBuilder that must be disposed.</returns>
    public static RowBuilder Row(float height) => new(height);

    /// <summary>
    /// Adds top margin to content. Call at start of content area.
    /// </summary>
    public static void TopMargin()
    {
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Margin * Scale);
    }

    /// <summary>
    /// Adds bottom margin to content. Call at end of content area.
    /// </summary>
    public static void BottomMargin()
    {
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Margin * Scale);
    }

    /// <summary>
    /// Gets the scrubber control height.
    /// </summary>
    public static float ScrubberHeight => 24f * Scale;

    /// <summary>
    /// Gets the standard ImGui frame height (for sliders, checkboxes, color pickers, etc).
    /// </summary>
    public static float FrameHeight => ImGui.GetFrameHeight();

    /// <summary>
    /// Gets the button height.
    /// </summary>
    public static float ButtonHeight => 24f * Scale;

    /// <summary>
    /// Gets the dropdown height.
    /// </summary>
    public static float DropdownHeight => PoserDropdown.Height;

    /// <summary>
    /// Gets the margin constant.
    /// </summary>
    public static float MarginScaled => Margin * Scale;

    /// <summary>
    /// Gets the row spacing constant.
    /// </summary>
    internal static float RowSpacingScaled => RowSpacing * Scale;

    /// <summary>
    /// Adds an empty row for vertical spacing between sections.
    /// </summary>
    public static void EmptyRow()
    {
        using var row = Row(FrameHeight);
        // Empty row for spacing
    }

    /// <summary>
    /// Draws a separator line at 50% border color opacity.
    /// </summary>
    public static void Separator()
    {
        float spacingBefore = 6f * Scale;
        float spacingAfter = 10f * Scale;

        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + spacingBefore);

        var drawList = ImGui.GetWindowDrawList();
        var cursorPos = ImGui.GetCursorScreenPos();
        var availWidth = ImGui.GetContentRegionAvail().X;

        var borderColor = UIColors.Border with { W = UIColors.Border.W * 0.5f };
        var colorU32 = ImGui.ColorConvertFloat4ToU32(borderColor);

        drawList.AddLine(
            cursorPos,
            new Vector2(cursorPos.X + availWidth, cursorPos.Y),
            colorU32,
            1f);

        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + spacingAfter + 1f);
    }
}

/// <summary>
/// Builder for flexible row layouts with multiple cells.
/// </summary>
public sealed class RowBuilder : IDisposable
{
    private readonly float _height;
    private readonly Vector2 _startPos;
    private readonly float _marginScaled;
    private readonly float _availableWidth;
    private float _currentX;
    private float _rightX; // For right-aligned elements after Stretch

    internal RowBuilder(float height)
    {
        _height = height;
        _startPos = ImGui.GetCursorPos();
        _marginScaled = PoserUI.MarginScaled;
        _availableWidth = ImGui.GetContentRegionAvail().X - _marginScaled * 2;
        _currentX = _startPos.X + _marginScaled;
        _rightX = _startPos.X + _marginScaled + _availableWidth; // Right edge
    }

    private const float LabelSpacing = 8f;

    /// <summary>
    /// Adds a text label, vertically centered and right-aligned within its width.
    /// Includes spacing after the label.
    /// </summary>
    /// <param name="text">Label text.</param>
    /// <param name="width">Fixed width. If 0, uses text width.</param>
    public RowBuilder Label(string text, float width = 0)
    {
        float w = width > 0 ? width * PoserUI.Scale : ImGui.CalcTextSize(text).X;
        float textWidth = ImGui.CalcTextSize(text).X;
        float textY = _startPos.Y + (_height - ImGui.GetTextLineHeight()) / 2f;
        // Right-align text within the label width
        float textX = _currentX + w - textWidth;
        ImGui.SetCursorPos(new Vector2(textX, textY));
        ImGui.Text(text);
        _currentX += w + LabelSpacing * PoserUI.Scale;
        return this;
    }

    /// <summary>
    /// Adds a disabled text label (header style), vertically centered.
    /// </summary>
    /// <param name="text">Header text.</param>
    public RowBuilder Header(string text)
    {
        float textY = _startPos.Y + (_height - ImGui.GetTextLineHeight()) / 2f;
        ImGui.SetCursorPos(new Vector2(_currentX, textY));
        ImGui.TextDisabled(text);
        _currentX += ImGui.CalcTextSize(text).X;
        return this;
    }

    /// <summary>
    /// Adds inline text, vertically centered.
    /// </summary>
    /// <param name="text">Text to display.</param>
    public RowBuilder Text(string text)
    {
        float textY = _startPos.Y + (_height - ImGui.GetTextLineHeight()) / 2f;
        ImGui.SetCursorPos(new Vector2(_currentX, textY));
        ImGui.Text(text);
        _currentX += ImGui.CalcTextSize(text).X;
        return this;
    }

    /// <summary>
    /// Adds a styled checkbox control.
    /// </summary>
    /// <param name="id">Unique ImGui ID.</param>
    /// <param name="value">Checkbox value (ref).</param>
    /// <returns>True if value changed.</returns>
    public bool Checkbox(string id, ref bool value)
    {
        float w = PoserCheckbox.Size;
        // Center checkbox vertically
        float offsetY = (_height - w) / 2f;
        ImGui.SetCursorPos(new Vector2(_currentX, _startPos.Y + offsetY));
        bool changed = PoserCheckbox.Draw(id, ref value);
        _currentX += w;
        return changed;
    }

    /// <summary>
    /// Adds a styled toggle button control.
    /// </summary>
    /// <param name="id">Unique ImGui ID.</param>
    /// <param name="value">Toggle value (ref).</param>
    /// <param name="iconOff">Icon when value is false.</param>
    /// <param name="iconOn">Icon when value is true.</param>
    /// <param name="tooltip">Optional tooltip.</param>
    /// <returns>True if value changed.</returns>
    public bool ToggleButton(string id, ref bool value, Dalamud.Interface.FontAwesomeIcon iconOff, Dalamud.Interface.FontAwesomeIcon iconOn, string? tooltip = null)
    {
        float w = PoserToggleButton.Size;
        // Center button vertically
        float offsetY = (_height - w) / 2f;
        ImGui.SetCursorPos(new Vector2(_currentX, _startPos.Y + offsetY));
        bool changed = PoserToggleButton.Draw(id, ref value, iconOff, iconOn, tooltip);
        _currentX += w;
        return changed;
    }

    /// <summary>
    /// Adds a slider control, taking remaining width.
    /// </summary>
    /// <param name="id">Unique ImGui ID.</param>
    /// <param name="value">Slider value (ref).</param>
    /// <param name="min">Minimum value.</param>
    /// <param name="max">Maximum value.</param>
    /// <returns>True if value changed.</returns>
    public bool Slider(string id, ref float value, float min, float max)
    {
        float w = RemainingWidth();
        ImGui.SetCursorPos(new Vector2(_currentX, _startPos.Y));
        ImGui.SetNextItemWidth(w);
        bool changed = ImGui.SliderFloat(id, ref value, min, max);
        _currentX += w;
        return changed;
    }

    /// <summary>
    /// Adds a color picker control.
    /// </summary>
    /// <param name="id">Unique ImGui ID.</param>
    /// <param name="color">Color value as uint ABGR (ref).</param>
    /// <param name="flags">Color edit flags.</param>
    /// <returns>True if value changed.</returns>
    public bool ColorEdit(string id, ref uint color, ImGuiColorEditFlags flags = ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoAlpha)
    {
        float w = ImGui.GetFrameHeight();
        ImGui.SetCursorPos(new Vector2(_currentX, _startPos.Y));
        var colorVec = ImGui.ColorConvertU32ToFloat4(color);

        // Add padding to the color picker popup
        float popupPadding = 8f * PoserUI.Scale;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(popupPadding, popupPadding));
        bool changed = ImGui.ColorEdit4(id, ref colorVec, flags);
        ImGui.PopStyleVar();

        if (changed)
            color = ImGui.ColorConvertFloat4ToU32(colorVec);
        _currentX += w;
        return changed;
    }

    /// <summary>
    /// Adds a color picker control (Vector4 version).
    /// </summary>
    /// <param name="id">Unique ImGui ID.</param>
    /// <param name="color">Color value as Vector4 (ref).</param>
    /// <param name="flags">Color edit flags.</param>
    /// <returns>True if value changed.</returns>
    public bool ColorEdit(string id, ref Vector4 color, ImGuiColorEditFlags flags = ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoAlpha)
    {
        float w = ImGui.GetFrameHeight();
        ImGui.SetCursorPos(new Vector2(_currentX, _startPos.Y));

        // Add padding to the color picker popup
        float popupPadding = 8f * PoserUI.Scale;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(popupPadding, popupPadding));
        bool changed = ImGui.ColorEdit4(id, ref color, flags);
        ImGui.PopStyleVar();

        _currentX += w;
        return changed;
    }

    /// <summary>
    /// Adds a scrubber control, taking remaining width.
    /// </summary>
    /// <param name="id">Unique ImGui ID.</param>
    /// <param name="value">Scrubber value (ref).</param>
    /// <param name="min">Minimum value.</param>
    /// <param name="max">Maximum value.</param>
    /// <param name="step">Step increment for snapping. If 0, no snapping.</param>
    /// <returns>True if value changed.</returns>
    public bool Scrubber(string id, ref float value, float min, float max, float step = 0f)
    {
        float w = RemainingWidth();
        ImGui.SetCursorPos(new Vector2(_currentX, _startPos.Y));
        bool changed = Controls.Scrubber.Draw(id, ref value, min, max, step, w);
        _currentX += w;
        return changed;
    }

    /// <summary>
    /// Adds a dropdown control with fixed width.
    /// </summary>
    /// <param name="id">Unique ImGui ID.</param>
    /// <param name="currentIndex">Current selected index (ref).</param>
    /// <param name="items">Array of item labels.</param>
    /// <param name="width">Width of the dropdown.</param>
    /// <returns>True if selection changed.</returns>
    public bool Dropdown(string id, ref int currentIndex, string[] items, float width = 150f)
    {
        float w = width * PoserUI.Scale;
        // Center dropdown vertically
        float offsetY = (_height - PoserDropdown.Height) / 2f;
        ImGui.SetCursorPos(new Vector2(_currentX, _startPos.Y + offsetY));
        bool changed = PoserDropdown.Draw(id, ref currentIndex, items, w);
        _currentX += w;
        return changed;
    }

    /// <summary>
    /// Adds a dropdown control that fills remaining width.
    /// </summary>
    /// <param name="id">Unique ImGui ID.</param>
    /// <param name="currentIndex">Current selected index (ref).</param>
    /// <param name="items">Array of item labels.</param>
    /// <returns>True if selection changed.</returns>
    public bool DropdownFill(string id, ref int currentIndex, string[] items)
    {
        float w = RemainingWidth();
        // Center dropdown vertically
        float offsetY = (_height - PoserDropdown.Height) / 2f;
        ImGui.SetCursorPos(new Vector2(_currentX, _startPos.Y + offsetY));
        bool changed = PoserDropdown.Draw(id, ref currentIndex, items, w);
        _currentX += w;
        return changed;
    }

    /// <summary>
    /// Adds a fixed-width spacer.
    /// </summary>
    /// <param name="width">Spacer width in unscaled pixels.</param>
    public RowBuilder Spacer(float width)
    {
        _currentX += width * PoserUI.Scale;
        return this;
    }

    /// <summary>
    /// Semantic marker indicating remaining space should stretch.
    /// Call Right* methods after this to position elements from the right edge.
    /// </summary>
    public RowBuilder Stretch()
    {
        return this;
    }

    /// <summary>
    /// Adds a color picker aligned to the right edge.
    /// </summary>
    /// <param name="id">Unique ImGui ID.</param>
    /// <param name="color">Color value as uint ABGR (ref).</param>
    /// <param name="flags">Color edit flags.</param>
    /// <returns>True if value changed.</returns>
    public bool RightColorEdit(string id, ref uint color, ImGuiColorEditFlags flags = ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoAlpha)
    {
        float w = ImGui.GetFrameHeight();
        float padding = 4f * PoserUI.Scale;
        _rightX -= padding + w; // padding on right, then width
        ImGui.SetCursorPos(new Vector2(_rightX, _startPos.Y));
        var colorVec = ImGui.ColorConvertU32ToFloat4(color);

        // Add padding to the color picker popup
        float popupPadding = 8f * PoserUI.Scale;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(popupPadding, popupPadding));
        bool changed = ImGui.ColorEdit4(id, ref colorVec, flags);
        ImGui.PopStyleVar();

        if (changed)
            color = ImGui.ColorConvertFloat4ToU32(colorVec);
        return changed;
    }

    /// <summary>
    /// Adds a styled checkbox aligned to the right edge.
    /// </summary>
    /// <param name="id">Unique ImGui ID.</param>
    /// <param name="value">Checkbox value (ref).</param>
    /// <returns>True if value changed.</returns>
    public bool RightCheckbox(string id, ref bool value)
    {
        float w = PoserCheckbox.Size;
        _rightX -= w;
        // Center checkbox vertically
        float offsetY = (_height - w) / 2f;
        ImGui.SetCursorPos(new Vector2(_rightX, _startPos.Y + offsetY));
        return PoserCheckbox.Draw(id, ref value);
    }

    /// <summary>
    /// Adds a styled button aligned to the right edge.
    /// </summary>
    /// <param name="id">Unique ImGui ID.</param>
    /// <param name="label">Button label.</param>
    /// <returns>True if clicked.</returns>
    public bool RightButton(string id, string label)
    {
        float paddingX = 12f * PoserUI.Scale;
        var textSize = ImGui.CalcTextSize(label);
        float w = textSize.X + paddingX * 2;
        _rightX -= w;
        ImGui.SetCursorPos(new Vector2(_rightX, _startPos.Y));
        return PoserButton.Draw(id, label);
    }

    /// <summary>
    /// Adds a fixed-width spacer for right-aligned elements.
    /// </summary>
    /// <param name="width">Spacer width in unscaled pixels.</param>
    public RowBuilder RightSpacer(float width)
    {
        _rightX -= width * PoserUI.Scale;
        return this;
    }

    /// <summary>
    /// Adds a styled button.
    /// </summary>
    /// <param name="id">Unique ImGui ID.</param>
    /// <param name="label">Button label.</param>
    /// <returns>True if clicked.</returns>
    public bool Button(string id, string label)
    {
        float paddingX = 12f * PoserUI.Scale;
        var textSize = ImGui.CalcTextSize(label);
        float w = textSize.X + paddingX * 2;
        ImGui.SetCursorPos(new Vector2(_currentX, _startPos.Y));
        bool clicked = PoserButton.Draw(id, label);
        _currentX += w;
        return clicked;
    }

    /// <summary>
    /// Draws custom content at the current position.
    /// </summary>
    /// <param name="width">Width to reserve.</param>
    /// <param name="draw">Drawing action.</param>
    public RowBuilder Custom(float width, Action draw)
    {
        float w = width * PoserUI.Scale;
        ImGui.SetCursorPos(new Vector2(_currentX, _startPos.Y));
        draw();
        _currentX += w;
        return this;
    }

    /// <summary>
    /// Adds an icon button.
    /// </summary>
    /// <param name="id">Unique ImGui ID.</param>
    /// <param name="icon">FontAwesome icon.</param>
    /// <param name="tooltip">Optional tooltip.</param>
    /// <returns>True if clicked.</returns>
    public bool IconButton(string id, Dalamud.Interface.FontAwesomeIcon icon, string? tooltip = null)
    {
        float w = PoserButton.IconButtonSize * PoserUI.Scale;
        float offsetY = (_height - w) / 2f;
        ImGui.SetCursorPos(new Vector2(_currentX, _startPos.Y + offsetY));
        bool clicked = PoserButton.DrawIcon(id, icon, tooltip);
        _currentX += w;
        return clicked;
    }

    /// <summary>
    /// Draws custom content that fills remaining width.
    /// </summary>
    /// <param name="draw">Drawing action that receives available width.</param>
    public RowBuilder CustomFill(Action<float> draw)
    {
        float w = RemainingWidth();
        ImGui.SetCursorPos(new Vector2(_currentX, _startPos.Y));
        draw(w);
        _currentX += w;
        return this;
    }

    public float RemainingWidth()
    {
        float endX = _startPos.X + _marginScaled + _availableWidth;
        return endX - _currentX;
    }

    /// <summary>
    /// Finalizes the row and advances cursor for next row.
    /// </summary>
    public void Dispose()
    {
        ImGui.SetCursorPos(new Vector2(_startPos.X, _startPos.Y + _height + PoserUI.RowSpacingScaled));
    }
}
