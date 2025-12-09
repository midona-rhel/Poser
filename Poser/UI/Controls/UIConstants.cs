using System.Numerics;
using Dalamud.Interface.Utility;

namespace Poser.UI.Controls;

/// <summary>
/// Centralized UI constants with GlobalScale support for responsive sizing.
/// </summary>
public static class UIConstants
{
    // Base sizes (before GlobalScale)
    public const float StandardRowHeight = 24f;
    public const float IconColumnWidth = 24f;
    public const float CheckboxColumnWidth = 24f;
    public const float ButtonSize = 25f;
    public const float HeaderHeight = 24f;

    // Scaled sizes - use these in UI code
    public static float ScaledRowHeight => StandardRowHeight * ImGuiHelpers.GlobalScale;
    public static float ScaledIconWidth => IconColumnWidth * ImGuiHelpers.GlobalScale;
    public static float ScaledCheckboxWidth => CheckboxColumnWidth * ImGuiHelpers.GlobalScale;
    public static float ScaledButtonSize => ButtonSize * ImGuiHelpers.GlobalScale;
    public static float ScaledHeaderHeight => HeaderHeight * ImGuiHelpers.GlobalScale;

    // Colors
    public static readonly Vector4 DefaultIconColor = new(1.0f, 1.0f, 1.0f, 1.0f);   // White for all entities
    public static readonly Vector4 HiddenIconColor = new(0.5f, 0.5f, 0.5f, 0.5f);    // Dimmed - hidden
    public static readonly Vector4 DisabledTextColor = new(0.5f, 0.5f, 0.5f, 1.0f);
    public static readonly Vector4 SkeletonColor = new(0.6f, 0.8f, 1.0f, 1.0f);      // Light blue - for skeleton

    // Toggle/State colors (used for Pose toggle, Gaze controls, etc.)
    public static readonly Vector4 ActiveColor = new(1.0f, 0.7f, 0.3f, 1.0f);        // Orange - active/enabled
    public static readonly Vector4 InactiveColor = new(0.5f, 0.5f, 0.5f, 1.0f);      // Gray - inactive/disabled
    public static readonly Vector4 LockedColor = new(0.8f, 0.3f, 0.3f, 1.0f);        // Red - locked state
}
