using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

/// <summary>
/// GlassInput — <c>src/shared/ui/GlassInput/GlassInput.module.css</c> and
/// its <c>.tsx</c>. Two variants share one core:
///
/// <list type="bullet">
/// <item><c>.input</c> — 32px box, <c>padding: 0 10px</c>, 1px
/// <c>--color-border-primary</c>, <c>--radius-sm</c>,
/// <c>--color-black-20</c> fill, <c>--font-size-md</c>
/// <c>--color-text-primary</c> value, <c>--color-text-tertiary</c>
/// placeholder, <c>:focus</c> = <c>border-color: --color-primary-50</c>,
/// <c>:disabled</c> = <c>opacity: .5</c>.</item>
/// <item><c>.searchWrap</c> — 36px flex row, <c>padding-left: 10px</c>,
/// <c>gap: 6px</c>, a 14px leading icon at <c>--color-text-tertiary</c>
/// <c>opacity: .6</c>, and a <c>.searchInput</c> with NO background, NO
/// border and NO focus treatment (<c>background: none; border: none;
/// outline: none</c>).</item>
/// </list>
///
/// <para>The value itself is a native ImGui <c>InputText</c>, so its
/// frame, text, selection and nav chrome are normalized through ImGui
/// style pushes rather than painted here; only what CSS puts OUTSIDE the
/// native widget (border color, placeholder, search icon, clear
/// affordance) is drawn by this file.</para>
///
/// <para>DEVIATIONS from the CSS, all deliberate:</para>
/// <list type="number">
/// <item>The clear affordance has NO Picto counterpart — GlassInput never
/// clears. It is kept as Poser's own, painted in the <c>.searchIcon</c>
/// grammar (<c>--color-text-tertiary</c>, lifting to
/// <c>--color-text-primary</c> under the pointer).</item>
/// <item>The search glyph is the shipped <c>zoom-in</c>; the shipped icon
/// set has no plain <c>search</c>, and adding one reflows the icon-grid
/// conformance states. Same magnifier, plus an inner cross.</item>
/// <item>ImGui's FramePadding is symmetric, so the search variant's
/// 30px leading inset (10 + 14 + 6) is mirrored on the right where CSS
/// has 0. It also keeps the value clear of the clear affordance.</item>
/// <item>Selection uses <c>--color-primary</c> at .32 (the AxisWell inline
/// edit's existing idiom); the CSS declares no <c>::selection</c>.</item>
/// </list>
/// </summary>
public static partial class Crystarium
{
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

    public static bool ClearableTextInput(
        string id,
        string value,
        Action<string> onChange,
        ControlStyle style = default,
        string? placeholder = null,
        bool disabled = false,
        string? help = null) =>
        TextInputCore(
            id, value, onChange, style, placeholder,
            clearable: true, search: false, disabled, help);

    // The clear affordance is a reserved hit area, so pressing it takes
    // ImGui's active id away from the field the way any other control
    // would. Clearing is an edit of the field the user is still in, so
    // the field takes focus back on the IMMEDIATELY following frame.
    //
    // The frame is part of the request because the identity alone is not
    // enough: an id is only unique within a frame's id stack, so a request
    // that outlived its frame could hand focus to a completely different
    // control that happens to reuse the identity later. One frame of grace
    // is exactly the lifetime the handover needs.
    private static uint _clearRefocusTarget;
    private static int _clearRefocusFrame;

    private static bool TextInputCore(
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
        // .input: padding-left 10. .searchWrap: 10 + a 14px icon + gap 6,
        // so the field's own text starts 30 in.
        float textInset = search
            ? padX + iconSide + theme.Controls.SearchIconGap * scale
            : padX;

        // The native widget renders the VALUE, so the field's font is the
        // one it is submitted under: --font-size-md, --font-family,
        // regular. Resolving it first also makes FramePadding.y the true
        // vertical centering for that font.
        var font = FontRegistry.Resolve(
            FontFamily.Default,
            FontWeight.Regular,
            theme.Typography.BodySize);
        bool fontPushed = font is { Available: true };
        if (fontPushed)
            font!.Push();

        float framePadY = MathF.Max(
            0f, (height - ImGui.GetTextLineHeight()) * 0.5f);

        if (disabled)
        {
            ImGui.PushStyleVar(
                ImGuiStyleVar.DisabledAlpha,
                theme.Controls.InputDisabledOpacity);
            ImGui.BeginDisabled();
        }

        // .searchInput has `background: none`; .input has --color-black-20.
        // Neither declares a :hover or :active background, so all three
        // frame slots take the one fill.
        var fill = search ? Vector4.Zero : theme.Chrome.InputWell;
        ImGui.PushStyleColor(ImGuiCol.FrameBg, fill);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, fill);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, fill);
        ImGui.PushStyleColor(ImGuiCol.Text, theme.Text);
        ImGui.PushStyleColor(
            ImGuiCol.TextSelectedBg, theme.Chrome.Primary with { W = 0.32f });
        // `outline: none` on both variants: ImGui's own nav ring is not a
        // declared treatment, and the CSS focus treatment is the border.
        ImGui.PushStyleColor(ImGuiCol.NavHighlight, Vector4.Zero);
        ImGui.PushStyleVar(
            ImGuiStyleVar.FramePadding, new Vector2(textInset, framePadY));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, radius);
        // The border is painted below instead: its COLOR is the whole
        // :focus treatment, and focus is only known after submission.
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f);
        ImGui.SetNextItemWidth(width);

        uint identity = ImGui.GetID(id);
        if (_clearRefocusTarget != 0)
        {
            // Anything but the very next frame — including a restarted
            // frame counter — discards the request outright, whether or
            // not this field is the one it named.
            if (ImGui.GetFrameCount() != _clearRefocusFrame + 1)
                _clearRefocusTarget = 0;
            else if (_clearRefocusTarget == identity)
            {
                _clearRefocusTarget = 0;
                ImGui.SetKeyboardFocusHere();
            }
        }

        string next = value;
        bool changed = ImGui.InputText(id, ref next);

        var inputMin = ImGui.GetItemRectMin();
        var inputMax = ImGui.GetItemRectMax();
        var cursorAfterInput = ImGui.GetCursorScreenPos();
        // :focus is the DOM sense — the caret is in the field and typing
        // goes there — which for a native InputText is the ACTIVE id, not
        // merely the nav id. Nav landing on the first item of a window
        // would otherwise light every field up permanently.
        bool focused = ImGui.IsItemActive();
        // InputText stays a native ImGui widget, so its help trigger takes
        // the occlusion gate that Interactive.Reserve applies for us
        // everywhere else.
        bool hovered = ImGui.IsItemHovered() && !Interactive.PointerOccluded();

        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(6);

        var draw = ImGui.GetWindowDrawList();
        // .input's 1px border, whose color IS :focus. .searchInput has
        // `border: none`, so the search variant paints nothing here.
        if (!search)
            draw.AddRect(
                inputMin,
                inputMax,
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(
                    focused
                        ? theme.Chrome.PrimaryFocus
                        : theme.Chrome.ControlBorder)),
                radius,
                ImDrawFlags.None,
                border);

        if (search)
        {
            var iconMin = new Vector2(
                inputMin.X + padX,
                inputMin.Y + (height - iconSide) * 0.5f);
            IconIn(
                iconMin,
                iconMin + new Vector2(iconSide),
                "zoom-in",
                theme.TextMuted,
                opacity: 0.6f);
        }

        // ::placeholder is not a focus-gated pseudo-element: an empty
        // field shows it while the caret is in it, exactly as Blink does.
        if (next.Length == 0 && !string.IsNullOrEmpty(placeholder))
            TextAt(
                new Vector2(inputMin.X + textInset, inputMin.Y + framePadY),
                placeholder!,
                new TextStyle { Color = theme.TextMuted });

        if (disabled)
        {
            ImGui.EndDisabled();
            ImGui.PopStyleVar();
        }

        if (clearable && !disabled && next.Length > 0)
        {
            var center = new Vector2(
                inputMax.X - 13f * scale,
                (inputMin.Y + inputMax.Y) * 0.5f);
            var hitPadding = new Vector2(9f) * scale;
            // The clear affordance is a real reserved hit area on the one
            // interaction path, so it is occlusion-gated like every other
            // control. It overlaps the native InputText submitted above,
            // which must therefore yield hover/active arbitration to it.
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
                _clearRefocusTarget = identity;
                _clearRefocusFrame = ImGui.GetFrameCount();
            }
        }

        if (fontPushed)
            font!.Pop();

        if (changed) onChange(next);
        if (!string.IsNullOrEmpty(help) && hovered)
            HoverHelp.Explain(id, inputMin, inputMax, help!);
        return changed;
    }
}
