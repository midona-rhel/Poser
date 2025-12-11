using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace Poser.UI.Controls;

/// <summary>
/// Delegate for drawing modal content with access to the modal's draw list.
/// </summary>
/// <param name="overlayDrawList">The modal's draw list for overlay rendering (shadow/border).</param>
public delegate void ModalContentDrawer(ImDrawListPtr overlayDrawList);

/// <summary>
/// Reusable modal popup controller.
/// Uses UIColors for consistent theming.
/// </summary>
public class Modal
{
    private readonly string _title;
    private readonly string _popupId;
    private readonly Vector2 _size;
    private readonly ImGuiWindowFlags _flags;

    private bool _isOpen;

    /// <summary>
    /// Creates a new modal with the specified title and size.
    /// </summary>
    /// <param name="title">The modal title displayed in the title bar.</param>
    /// <param name="size">The modal size (before GlobalScale).</param>
    /// <param name="flags">Additional window flags (NoResize, NoMove, NoCollapse are always applied).</param>
    public Modal(string title, Vector2 size, ImGuiWindowFlags flags = ImGuiWindowFlags.None)
    {
        _title = title;
        _popupId = $"{title}##modal_{Guid.NewGuid():N}";
        _size = size;
        _flags = flags | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse;
    }

    /// <summary>
    /// Whether the modal is currently open.
    /// </summary>
    public bool IsOpen => _isOpen;

    /// <summary>
    /// Opens the modal.
    /// </summary>
    public void Open()
    {
        _isOpen = true;
        ImGui.OpenPopup(_popupId);
    }

    /// <summary>
    /// Closes the modal.
    /// </summary>
    public void Close()
    {
        _isOpen = false;
        ImGui.CloseCurrentPopup();
    }

    /// <summary>
    /// Draws the modal with the specified content action.
    /// </summary>
    /// <param name="drawContent">Action to draw the modal content.</param>
    public void Draw(Action drawContent) => Draw(dl => drawContent());

    /// <summary>
    /// Draws the modal with access to the modal's draw list for overlay rendering.
    /// </summary>
    /// <param name="drawContent">Delegate to draw the modal content, receives the modal's draw list.</param>
    public void Draw(ModalContentDrawer drawContent)
    {
        if (!_isOpen)
            return;

        var displaySize = ImGui.GetIO().DisplaySize;
        var modalSize = _size * ImGuiHelpers.GlobalScale;

        ImGui.SetNextWindowPos(
            new Vector2((displaySize.X - modalSize.X) / 2, (displaySize.Y - modalSize.Y) / 2),
            ImGuiCond.Appearing);
        ImGui.SetNextWindowSize(modalSize, ImGuiCond.Always);

        // Apply UIColors - Background for all surfaces, ControlBackground for inputs
        using (ImRaii.PushColor(ImGuiCol.WindowBg, UIColors.Background))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, UIColors.Background))
        using (ImRaii.PushColor(ImGuiCol.PopupBg, UIColors.Background))
        using (ImRaii.PushColor(ImGuiCol.FrameBg, UIColors.ControlBackground))
        using (ImRaii.PushColor(ImGuiCol.ScrollbarBg, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.ScrollbarGrab, UIColors.ControlBackground))
        using (ImRaii.PushColor(ImGuiCol.ScrollbarGrabHovered, UIColors.ControlBackground))
        using (ImRaii.PushColor(ImGuiCol.ScrollbarGrabActive, UIColors.ControlBackground))
        using (ImRaii.PushColor(ImGuiCol.Text, UIColors.Text))
        using (ImRaii.PushColor(ImGuiCol.TextDisabled, UIColors.TextDisabled))
        using (ImRaii.PushColor(ImGuiCol.Border, UIColors.Border))
        using (ImRaii.PushColor(ImGuiCol.TitleBg, UIColors.TitleBar))
        using (ImRaii.PushColor(ImGuiCol.TitleBgActive, UIColors.TitleBarActive))
        using (ImRaii.PushColor(ImGuiCol.Button, UIColors.Button))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, UIColors.ButtonHovered))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive, UIColors.ButtonActive))
        using (ImRaii.PushColor(ImGuiCol.Header, UIColors.SelectionActive))
        using (ImRaii.PushColor(ImGuiCol.HeaderHovered, UIColors.SelectionHovered))
        using (ImRaii.PushColor(ImGuiCol.HeaderActive, UIColors.SelectionActiveHovered))
        {
            if (ImGui.BeginPopupModal(_popupId, ref _isOpen, _flags))
            {
                // Capture modal's draw list before creating child window
                var modalDrawList = ImGui.GetWindowDrawList();

                // Add internal padding so content doesn't reach modal edges
                float padding = 12f * ImGuiHelpers.GlobalScale;
                var available = ImGui.GetContentRegionAvail();
                ImGui.SetCursorPos(ImGui.GetCursorPos() + new Vector2(padding, padding));
                var childSize = available - new Vector2(padding * 2, padding * 2);
                using (ImRaii.Child("##modal_content", childSize, false))
                {
                    drawContent(modalDrawList);
                }
                ImGui.EndPopup();
            }
        }
    }

    /// <summary>
    /// Draws the modal with the specified content action, with a custom title.
    /// </summary>
    /// <param name="title">Custom title to display (overrides constructor title).</param>
    /// <param name="drawContent">Action to draw the modal content.</param>
    public void Draw(string title, Action drawContent) => Draw(title, dl => drawContent());

    /// <summary>
    /// Draws the modal with a custom title and access to the modal's draw list.
    /// </summary>
    /// <param name="title">Custom title to display (overrides constructor title).</param>
    /// <param name="drawContent">Delegate to draw the modal content, receives the modal's draw list.</param>
    public void Draw(string title, ModalContentDrawer drawContent)
    {
        if (!_isOpen)
            return;

        var displaySize = ImGui.GetIO().DisplaySize;
        var modalSize = _size * ImGuiHelpers.GlobalScale;

        ImGui.SetNextWindowPos(
            new Vector2((displaySize.X - modalSize.X) / 2, (displaySize.Y - modalSize.Y) / 2),
            ImGuiCond.Appearing);
        ImGui.SetNextWindowSize(modalSize, ImGuiCond.Always);

        var customPopupId = $"{title}##{_popupId}";

        // Apply UIColors - Background for all surfaces, ControlBackground for inputs
        using (ImRaii.PushColor(ImGuiCol.WindowBg, UIColors.Background))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, UIColors.Background))
        using (ImRaii.PushColor(ImGuiCol.PopupBg, UIColors.Background))
        using (ImRaii.PushColor(ImGuiCol.FrameBg, UIColors.ControlBackground))
        using (ImRaii.PushColor(ImGuiCol.ScrollbarBg, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.ScrollbarGrab, UIColors.ControlBackground))
        using (ImRaii.PushColor(ImGuiCol.ScrollbarGrabHovered, UIColors.ControlBackground))
        using (ImRaii.PushColor(ImGuiCol.ScrollbarGrabActive, UIColors.ControlBackground))
        using (ImRaii.PushColor(ImGuiCol.Text, UIColors.Text))
        using (ImRaii.PushColor(ImGuiCol.TextDisabled, UIColors.TextDisabled))
        using (ImRaii.PushColor(ImGuiCol.Border, UIColors.Border))
        using (ImRaii.PushColor(ImGuiCol.TitleBg, UIColors.TitleBar))
        using (ImRaii.PushColor(ImGuiCol.TitleBgActive, UIColors.TitleBarActive))
        using (ImRaii.PushColor(ImGuiCol.Button, UIColors.Button))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, UIColors.ButtonHovered))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive, UIColors.ButtonActive))
        using (ImRaii.PushColor(ImGuiCol.Header, UIColors.SelectionActive))
        using (ImRaii.PushColor(ImGuiCol.HeaderHovered, UIColors.SelectionHovered))
        using (ImRaii.PushColor(ImGuiCol.HeaderActive, UIColors.SelectionActiveHovered))
        {
            if (ImGui.BeginPopupModal(customPopupId, ref _isOpen, _flags))
            {
                // Capture modal's draw list before creating child window
                var modalDrawList = ImGui.GetWindowDrawList();

                // Add internal padding so content doesn't reach modal edges
                float padding = 12f * ImGuiHelpers.GlobalScale;
                var available = ImGui.GetContentRegionAvail();
                ImGui.SetCursorPos(ImGui.GetCursorPos() + new Vector2(padding, padding));
                var childSize = available - new Vector2(padding * 2, padding * 2);
                using (ImRaii.Child("##modal_content", childSize, false))
                {
                    drawContent(modalDrawList);
                }
                ImGui.EndPopup();
            }
        }
    }
}
