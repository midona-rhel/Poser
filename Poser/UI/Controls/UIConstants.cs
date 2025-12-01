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
    public static readonly Vector4 PoseableColor = new(0.4f, 0.9f, 0.4f, 1.0f);   // Green - actor is poseable
    public static readonly Vector4 NotPoseableColor = new(0.9f, 0.4f, 0.4f, 1.0f); // Red - actor not poseable
    public static readonly Vector4 GPoseActiveColor = new(0.4f, 1.0f, 0.4f, 1.0f); // GPose indicator
    public static readonly Vector4 DisabledTextColor = new(0.5f, 0.5f, 0.5f, 1.0f);
}
