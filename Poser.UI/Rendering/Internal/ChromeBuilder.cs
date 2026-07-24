using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI;

/// <summary>
/// Materializes a resolved style + interaction state into a <see cref="BoxStyle"/>
/// paint call. Encapsulates the duplicated "background/border/shadow with ImGui
/// live fallback and disabled opacity" logic that every chrome tag did.
/// </summary>
public static class ChromeBuilder
{
    /// <summary>
    /// Paint chrome at the given rect. <paramref name="liveFallbackBg"/> is used
    /// when the resolved style has no <c>BackgroundColor</c> — typically an
    /// <c>ImGui.GetStyle().Colors[ImGuiCol.X]</c> read so chrome inherits the
    /// active Dalamud theme tint.
    /// </summary>
    public static void Paint(Vector2 min, Vector2 max, in ElementStyle resolved, Vector4 liveFallbackBg)
    {
        bool disabled = false; // caller bakes disabled opacity via the resolved.Opacity field
        Vector4 bg = resolved.BackgroundColor.HasValue
            ? ColorEx.ApplyAlpha(resolved.BackgroundColor.Value)
            : ColorEx.ApplyAlpha(liveFallbackBg with { W = 1f });

        // Element opacity fades the whole chrome, border included — a disabled
        // control must not keep a full-strength outline around a faded fill.
        var border = resolved.BorderColor ?? Norvrandt.Sheet.CurrentTheme.Border;
        if (resolved.Opacity.HasValue)
        {
            bg = bg with { W = bg.W * resolved.Opacity.Value };
            border = border with { W = border.W * resolved.Opacity.Value };
        }

        Norvrandt.Box(min, max, new BoxStyle
        {
            BackgroundColor = bg,
            BackgroundGradient = resolved.BackgroundGradient,
            BorderColor = border,
            BorderWidth = resolved.BorderWidth ?? 1f,
            BorderRadius = resolved.BorderRadius ?? 4f,
            BoxShadow = resolved.BoxShadow,
            BoxShadows = resolved.BoxShadows,
            Outline = resolved.Outline,
            RaisedGradient = resolved.RaisedGradient ?? !disabled,
        });
    }

    /// <summary>Live fallback for button-like chrome (uses ImGui's Button/ButtonHovered/ButtonActive).</summary>
    public static Vector4 LiveButtonBg(PseudoState state)
    {
        var style = ImGui.GetStyle();
        if ((state & PseudoState.Active) != 0)  return style.Colors[(int)ImGuiCol.ButtonActive];
        if ((state & PseudoState.Hover)  != 0)  return style.Colors[(int)ImGuiCol.ButtonHovered];
        return style.Colors[(int)ImGuiCol.Button];
    }

    /// <summary>Live fallback for input-like chrome (uses ImGui's FrameBg/FrameBgHovered/FrameBgActive).</summary>
    public static Vector4 LiveFrameBg(PseudoState state)
    {
        var style = ImGui.GetStyle();
        if ((state & PseudoState.Active) != 0)  return style.Colors[(int)ImGuiCol.FrameBgActive];
        if ((state & PseudoState.Hover)  != 0)  return style.Colors[(int)ImGuiCol.FrameBgHovered];
        return style.Colors[(int)ImGuiCol.FrameBg];
    }
}
