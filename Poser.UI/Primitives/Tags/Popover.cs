using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public record struct PopoverProps
{
    /// <summary>Unscaled width. The popover never sizes to content —
    /// a search result list would resize under the pointer as the user
    /// types, which moves the rows they are aiming at.</summary>
    public float Width;
    /// <summary>Unscaled height.</summary>
    public float Height;
    /// <summary>Screen rect the popover anchors under, in pixels. It
    /// flips above when there is no room below.</summary>
    public Vector2 AnchorMin;
    public Vector2 AnchorMax;
    /// <summary>Unscaled inner padding. Default 8.</summary>
    public float Padding;
}

public static partial class Crystarium
{
    /// <summary>
    /// Anchored glass popover with a caller-supplied body — the shared
    /// shell behind pickers and any other "click a control, get a panel"
    /// surface.
    ///
    /// It is the same glass recipe as ContextMenu, Modal and ColorWell
    /// (backdrop blur, the border trio, radius 8), which those three each
    /// duplicated inline; this is the one place it now lives. Unlike
    /// ContextMenu it is a fixed size and scrolls, and unlike Modal it
    /// does not block input behind it.
    ///
    /// Open it with <c>ImGui.OpenPopup(id)</c>. Returns true while it is
    /// open, after invoking <paramref name="body"/>.
    /// </summary>
    public static bool Popover(string id, in PopoverProps props, Action body)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float width = props.Width * scale;
        float height = props.Height * scale;
        float padding = (props.Padding > 0f ? props.Padding : 8f) * scale;
        float radius = 8f * scale;

        // Anchor below, flipping above when the bottom would overflow, and
        // clamping horizontally so a popover opened near the right edge is
        // still fully reachable.
        var display = ImGui.GetIO().DisplaySize;
        float x = Math.Clamp(props.AnchorMin.X, 0f, MathF.Max(0f, display.X - width));
        float y = props.AnchorMax.Y + 2f * scale;
        if (y + height > display.Y)
        {
            float above = props.AnchorMin.Y - height - 2f * scale;
            y = above >= 0f ? above : MathF.Max(0f, display.Y - height);
        }

        ImGui.SetNextWindowPos(new Vector2(x, y));
        ImGui.SetNextWindowSize(new Vector2(width, height));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(padding, padding));
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, radius);
        // The border trio is drawn manually; ImGui's own border cannot do
        // per-side colours.
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 0f);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, GlassChrome.BackgroundColor);

        bool open = ImGui.BeginPopup(id,
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings);
        if (open)
        {
            var winMin = ImGui.GetWindowPos();
            var winMax = winMin + ImGui.GetWindowSize();
            var dl = ImGui.GetWindowDrawList();
            GlassChrome.PrependBlur(dl, winMin, winMax, radius);
            Norvrandt.Box(winMin, winMax, new BoxStyle
            {
                BorderWidth = 1f,
                BorderRadius = 8f,
                BorderTopColor = Theme.Glass.BorderTop,
                BorderLeftColor = Theme.Glass.BorderSide,
                BorderRightColor = Theme.Glass.BorderSide,
                BorderBottomColor = Theme.Glass.BorderBottom,
            });

            body();
            ImGui.EndPopup();
        }

        ImGui.PopStyleColor();
        ImGui.PopStyleVar(3);
        return open;
    }

    /// <summary>
    /// The shell's scrollbar treatment, pushed locally. A popup is not
    /// inside the shell's style scope, so it inherits ImGui's default
    /// scrollbar unless it pushes its own; without this a picker's
    /// scrollbar does not match the sidebar's.
    /// Pair with <see cref="PopScrollbarStyle"/>.
    /// </summary>
    public static void PushScrollbarStyle(float widthUnscaled = 12f, float radiusUnscaled = 4f)
    {
        float scale = ImGuiHelpers.GlobalScale;
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, widthUnscaled * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, radiusUnscaled * scale);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, new Vector4(1f, 1f, 1f, 0.12f));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, new Vector4(1f, 1f, 1f, 0.25f));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive, new Vector4(1f, 1f, 1f, 0.25f));
    }

    public static void PopScrollbarStyle()
    {
        ImGui.PopStyleColor(4);
        ImGui.PopStyleVar(2);
    }
}
