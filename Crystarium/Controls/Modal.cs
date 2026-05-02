using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Poser.UI;

namespace Poser.UI.Controls;

/// <summary>
/// Delegate for drawing modal content with access to the modal's draw list.
/// </summary>
/// <param name="overlayDrawList">The modal's draw list for overlay rendering (shadow/border).</param>
public delegate void ModalContentDrawer(ImDrawListPtr overlayDrawList);

/// <summary>
/// Reusable modal window controller (non-blocking, resizable).
/// Uses UIColors for consistent theming.
/// </summary>
public class Modal
{
    private readonly string _title;
    private readonly string _windowId;
    private readonly Vector2 _minSize;

    private bool _isOpen;

    /// <summary>
    /// Creates a new modal with the specified title and minimum size.
    /// </summary>
    /// <param name="title">The modal title displayed in the title bar.</param>
    /// <param name="minSize">The minimum modal size (before GlobalScale). Can resize larger but not smaller.</param>
    public Modal(string title, Vector2 minSize, ImGuiWindowFlags flags = ImGuiWindowFlags.None)
    {
        _title = title;
        _windowId = $"{title}##modal_{Guid.NewGuid():N}";
        _minSize = minSize;
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
    }

    /// <summary>
    /// Closes the modal.
    /// </summary>
    public void Close()
    {
        _isOpen = false;
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

        SetupWindowConstraints();
        DrawWithModalColors(_windowId, drawContent);
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

        SetupWindowConstraints();
        var customWindowId = $"{title}##{_windowId}";
        DrawWithModalColors(customWindowId, drawContent);
    }

    /// <summary>
    /// Sets up window position, size, and constraints for the modal.
    /// </summary>
    private void SetupWindowConstraints()
    {
        var displaySize = ImGui.GetIO().DisplaySize;
        var minSize = _minSize * PoserUI.Scale;

        ImGui.SetNextWindowPos(
            new Vector2((displaySize.X - minSize.X) / 2, (displaySize.Y - minSize.Y) / 2),
            ImGuiCond.Appearing);
        ImGui.SetNextWindowSize(minSize, ImGuiCond.Appearing);
        ImGui.SetNextWindowSizeConstraints(minSize, new Vector2(float.MaxValue, float.MaxValue));
    }

    /// <summary>
    /// Helper to wrap modal content with all UIColors pushed.
    /// </summary>
    private void DrawWithModalColors(string windowId, ModalContentDrawer drawContent)
    {
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
        using (ImRaii.PushColor(ImGuiCol.ResizeGrip, UIColors.Border))
        using (ImRaii.PushColor(ImGuiCol.ResizeGripHovered, UIColors.SelectionHovered))
        using (ImRaii.PushColor(ImGuiCol.ResizeGripActive, UIColors.SelectionActive))
        {
            if (ImGui.Begin(windowId, ref _isOpen, ImGuiWindowFlags.NoCollapse))
            {
                var modalDrawList = ImGui.GetWindowDrawList();

                float padding = 12f * PoserUI.Scale;
                var available = ImGui.GetContentRegionAvail();
                ImGui.SetCursorPos(ImGui.GetCursorPos() + new Vector2(padding, padding));
                var childSize = available - new Vector2(padding * 2, padding * 2);
                using (ImRaii.Child("##modal_content", childSize, false))
                {
                    drawContent(modalDrawList);
                }
            }
            ImGui.End();
        }
    }
}
