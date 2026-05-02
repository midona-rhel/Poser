using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Poser.UI.Controls;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>Plain inline text. Cascade-inherited color/font/opacity unless overridden.</summary>
    public static void Text(string text)
        => TextCore(text, default, null);
    public static void Text(string text, StyleClass cls)
        => TextCore(text, cls, null);
    public static void Text(string text, StyleClassSet classes)
        => TextCore(text, classes, null);
    public static void Text(string text, in TextProps props)
        => TextCore(text, props.Classes, props.Style);

    private static void TextCore(string text, StyleClassSet classes, TextStyle? inline)
    {
        Stylesheet.EnsureInitialized();

        var resolved = Stylesheet.ResolveText(classes, PseudoState.None);
        if (inline.HasValue) resolved = resolved.MergedWith(inline.Value);

        // Margin top
        float scale = PoserUI.Scale;
        if (resolved.Margin.HasValue && resolved.Margin.Value.Top > 0)
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + resolved.Margin.Value.Top * scale);

        // Vertical centering when inside a sized cell
        float ambientH = AvailableHeight;
        if (ambientH > 0f)
        {
            float offsetY = (ambientH - ImGui.GetTextLineHeight()) / 2f;
            if (offsetY > 0f) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + offsetY);
        }

        // Color cascade — push once if specified
        bool pushed = false;
        if (resolved.Color.HasValue)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, resolved.Color.Value);
            pushed = true;
        }
        ImGui.Text(text);
        if (pushed) ImGui.PopStyleColor();

        if (resolved.Margin.HasValue && resolved.Margin.Value.Bottom > 0)
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + resolved.Margin.Value.Bottom * scale);
    }

    /// <summary>Thin separator line at 50% border opacity.</summary>
    public static void Separator() => Controls.PoserUI.Separator();

    /// <summary>Image element. Renders a Dalamud texture wrap at the given size.</summary>
    public static void Image(IDalamudTextureWrap texture, Vector2 size, Vector4? tint = null)
    {
        if (texture == null) return;
        if (tint.HasValue)
            ImGui.Image(texture.Handle, size, Vector2.Zero, Vector2.One, tint.Value);
        else
            ImGui.Image(texture.Handle, size);
    }
}
