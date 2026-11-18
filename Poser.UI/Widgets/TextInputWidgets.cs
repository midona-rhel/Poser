using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using static Poser.UI.Widgets.TablerIconWidgets;
using static Poser.UI.Widgets.TextWidgets;
using static Poser.UI.Widgets.Themes;
using static Poser.UI.Widgets.ValueEdits;

namespace Poser.UI.Widgets;

/// <summary>
/// Draws plain and search text fields on top of ImGui's own text field.
/// ImGui handles typing and selecting; this draws the border, the placeholder
/// text, the search icon and the optional clear button.
/// </summary>
public static class TextInputWidgets
{
    /// <summary>The field a clear button hands focus back to.</summary>
    internal sealed class RefocusState
    {
        // The clear button takes the mouse like any other control, so
        // pressing it takes focus away from the text field. Clearing is still
        // an edit of the field being typed in, so the field takes focus back
        // on the very next frame.
        //
        // The frame number is part of the request because the id alone is not
        // enough: an id is only unique within one frame, so an old request
        // could later give focus to a different control that reuses the id.
        // One frame is exactly as long as the handover needs.
        internal uint Target;
        internal int Frame;
    }

    private static RefocusState State => UiContext.Current.Refocus;

    public static bool TextInput(
        string id,
        string value,
        Action<string> onChange,
        ControlStyle style = default,
        string? placeholder = null,
        bool disabled = false,
        string? help = null) =>
        TextInputCore(
            id, value, onChange, style, placeholder,
            clearable: false, search: false, disabled, help);

    /// <summary>Space, in unscaled pixels, kept above the top of capital
    /// letters when the text cursor is cut shorter. Accents on tall letters
    /// (É, Å) reach into this space and stay visible. A bigger value makes the
    /// cursor look taller.</summary>
    internal const float CaretHeadroom = 2f;

    /// <summary>The most bytes of text a field accepts. Below 512 bytes,
    /// Dalamud's ImGui binding keeps the text in a small built-in buffer. At
    /// 512 or more it borrows a 1024-byte buffer and copies the text into it
    /// every frame, for every field. 510 is the largest value that stays in
    /// the built-in buffer, and no field here needs more text than that.</summary>
    private const int NativeInputMaxBytes = 510;

    internal static bool TextInputCore(
        string id,
        string value,
        Action<string> onChange,
        ControlStyle style,
        string? placeholder,
        bool clearable,
        bool search,
        bool disabled,
        string? help)
    {
        disabled |= Interactive.GreyedOut;
        float scale = ImGuiHelpers.GlobalScale;
        var theme = ActiveTheme;
        var metrics = ControlSizing.Resolve(
            style,
            ImGui.GetContentRegionAvail().X / scale,
            search
                ? theme.Controls.SearchHeight
                : theme.Controls.ComfortableHeight);
        float height = metrics.Height;
        float width = metrics.Width;
        float radius = theme.Radii.Medium * scale;
        float border = 1f * scale;
        float padX = theme.Controls.InputPaddingX * scale;
        float iconSide = theme.Controls.SmallIconSize * scale;
        // A plain field's text starts after the left padding. A search
        // field's text starts after the padding, the search icon and a gap.
        float textInset = search
            ? padX + iconSide + theme.Controls.SearchIconGap * scale
            : padX;

        // ImGui draws the typed text itself, in whatever font is active when
        // the field is drawn, so switch to the regular body font first. Doing
        // that first also makes the vertical padding centre this font correctly.
        var font = FontRegistry.Resolve(
            FontFamily.Default,
            FontWeight.Regular,
            theme.Typography.BodySize);
        bool fontPushed = font is { Available: true };
        if (fontPushed)
            font!.Push();

        // Placing the text up and down. Padding cannot move text inside a box
        // of fixed height, because ImGui works out the box height from the
        // padding (font size plus twice the padding). So the padding stays at
        // the value that makes the box exactly `height` tall, and the whole
        // ImGui field is drawn moved up a little instead. The text, cursor and
        // selection move up together, while the fill, border and icons are
        // drawn at the real box below.
        float framePadY = MathF.Max(
            0f, (height - ImGui.GetTextLineHeight()) * 0.5f);
        float rise = FontRegistry.InkRise(
            FontFamily.Default, FontWeight.Regular,
            theme.Typography.BodySize) * scale;

        var boxMin = ImGui.GetCursorScreenPos();
        var boxMax = boxMin + new Vector2(width, height);
        var draw = ImGui.GetWindowDrawList();

        if (disabled)
        {
            ImGui.PushStyleVar(
                ImGuiStyleVar.DisabledAlpha,
                theme.Controls.InputDisabledOpacity);
            ImGui.BeginDisabled();
        }

        // A plain field's dark fill, drawn at the real box. It is drawn after
        // disabling starts, so it dims with the rest. ImGui's own box is made
        // transparent below, so the moved-up field draws no box of its own.
        // A search field has no fill.
        if (!search)
            draw.AddRectFilled(
                boxMin,
                boxMax,
                ImGui.ColorConvertFloat4ToU32(
                    ColorEx.ApplyAlpha(theme.Chrome.InputWell)),
                radius);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Text, theme.Text);
        ImGui.PushStyleColor(
            ImGuiCol.TextSelectedBg, theme.Chrome.Primary with { W = 0.32f });
        // Hide the native navigation ring; the caret indicates text focus.
        ImGui.PushStyleColor(ImGuiCol.NavHighlight, Vector4.Zero);
        ImGui.PushStyleVar(
            ImGuiStyleVar.FramePadding, new Vector2(textInset, framePadY));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, radius);
        // The border is drawn after the field, at the real box, not the moved-up one.
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f);
        ImGui.SetCursorScreenPos(boxMin + new Vector2(0f, rise));
        ImGui.SetNextItemWidth(width);

        // ImGui's text cursor and selection are as tall as the whole line,
        // which reaches well above the tops of the letters, so the cursor
        // looks far taller than the text. The field is cut off just above
        // the letters, leaving CaretHeadroom so accents on tall letters stay
        // visible. Nothing below the top of capital letters is cut.
        float caretTrim = (FontRegistry.AscentOverCap(
                FontFamily.Default, FontWeight.Regular,
                theme.Typography.BodySize)
            - CaretHeadroom) * scale;
        bool caretClipped = caretTrim > 0f;
        if (caretClipped)
            draw.PushClipRect(
                new Vector2(
                    boxMin.X, boxMin.Y + rise + framePadY + caretTrim),
                boxMax,
                true);

        uint identity = ImGui.GetID(id);
        if (State.Target != 0)
        {
            // Anything but the very next frame — including a restarted
            // frame counter — discards the request outright, whether or
            // not this field is the one it named.
            if (ImGui.GetFrameCount() != State.Frame + 1)
                State.Target = 0;
            else if (State.Target == identity)
            {
                State.Target = 0;
                ImGui.SetKeyboardFocusHere();
            }
        }

        string next = value;
        // A search field selects all its text when clicked, so typing
        // replaces the old search instead of adding to it. That makes it quick
        // to search for the next thing to spawn.
        bool changed = ImGui.InputText(
            id, ref next, NativeInputMaxBytes,
            search
                ? ImGuiInputTextFlags.AutoSelectAll
                : ImGuiInputTextFlags.None);
        // ImGui draws the typed text without Poser's text drawing, so the
        // field asks for Chinese, Japanese and Korean characters itself,
        // whether typed directly or through an input method.
        FontRegistry.RequireGlyphs(next);
        bool committed = ImGui.IsItemDeactivatedAfterEdit()
            || (ImGui.IsItemActive() && ImGui.IsKeyPressed(ImGuiKey.Enter));
        if (caretClipped)
            draw.PopClipRect();

        // ImGui's field was moved up to place the text. The border, icons and
        // placeholder use the real box instead.
        var inputMin = ImGui.GetItemRectMin();
        // ImGui's layout position moved on from the moved-up field; put it
        // back where the real box would have left it.
        var cursorAfterInput = ImGui.GetCursorScreenPos()
            - new Vector2(0f, rise);
        ImGui.SetCursorScreenPos(cursorAfterInput);
        // The text field is ImGui's own control, so it must check by hand
        // that nothing covers it before showing help. Interactive.Reserve does
        // this for every other control.
        bool hovered = ImGui.IsItemHovered() && !Interactive.PointerOccluded();

        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(6);

        // Plain fields keep the same border when focused; search fields have no border.
        if (!search)
            draw.AddRect(
                boxMin,
                boxMax,
                ImGui.ColorConvertFloat4ToU32(
                    ColorEx.ApplyAlpha(theme.Chrome.ControlBorder)),
                radius,
                ImDrawFlags.None,
                border);

        if (search)
        {
            var iconMin = new Vector2(
                boxMin.X + padX,
                boxMin.Y + (height - iconSide) * 0.5f);
            IconIn(
                iconMin,
                iconMin + new Vector2(iconSide),
                "search",
                theme.TextMuted,
                opacity: 0.6f);
        }

        // An empty field shows its placeholder even while it is being typed
        // in, as web browsers do. The placeholder is centred in the real box,
        // like the search icon, because placing it like the typed text put it
        // a few pixels too low.
        if (next.Length == 0 && !string.IsNullOrEmpty(placeholder))
            TextInBand(
                new Vector2(boxMin.X + textInset, boxMin.Y),
                new Vector2(MathF.Max(0f, width - textInset), height),
                placeholder!,
                new TextStyle { Color = theme.TextMuted },
                TextAlign.Start,
                besideIcon: true);

        if (disabled)
        {
            ImGui.EndDisabled();
            ImGui.PopStyleVar();
        }

        if (clearable && !disabled && next.Length > 0)
        {
            var center = new Vector2(
                boxMax.X - 13f * scale,
                (boxMin.Y + boxMax.Y) * 0.5f);
            var hitPadding = new Vector2(9f) * scale;
            // The clear button takes the mouse through Interactive.Reserve like
            // every other control, so it ignores the mouse when something
            // covers it. It sits on top of the text field, so the field must
            // let it have the mouse.
            ImGui.SetItemAllowOverlap();
            ImGui.SetCursorScreenPos(center - hitPadding);
            var clearHit = Interactive.Reserve(
                $"{id}##clear", hitPadding * 2f, disabled: false);
            ImGui.SetCursorScreenPos(cursorAfterInput);
            bool clearHovered = clearHit.Hovered;
            IconIn(
                center - new Vector2(iconSide * 0.5f),
                center + new Vector2(iconSide * 0.5f),
                TablerIcon.X,
                clearHovered ? theme.Text : theme.TextMuted);

            if (clearHovered)
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (clearHit.Clicked)
            {
                next = string.Empty;
                changed = true;
                State.Target = identity;
                State.Frame = ImGui.GetFrameCount();
            }
        }

        if (fontPushed)
            font!.Pop();

        if (changed) ChangeValue(id, () => onChange(next));
        if (committed) Commit(id, null);
        if (!string.IsNullOrEmpty(help) && hovered)
            HoverHelp.Explain(id, boxMin, boxMax, help!);
        return changed;
    }
}
