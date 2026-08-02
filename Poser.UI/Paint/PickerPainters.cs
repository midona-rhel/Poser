using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI.Reactive;

/// <summary>
/// OverlayShell's <c>box-shadow: 0 -1px 0 var(--color-border-secondary) inset</c>
/// — the hairline the header and the search area each carry along their BOTTOM
/// edge. An inset shadow is painted inside the box, so it costs the band no
/// height and the content above it never shifts.
/// </summary>
internal sealed class PickerRulePainter : IInteractivePainter
{
    internal static readonly PickerRulePainter Instance = new();

    private PickerRulePainter()
    {
    }

    public PaintOutput Paint(in PaintInput input)
    {
        float scale = ImGuiHelpers.GlobalScale;
        Vector2 max = input.Hit.ScreenMax;
        input.DrawList.AddRectFilled(
            new Vector2(input.Hit.ScreenMin.X, max.Y - scale),
            max,
            ImGui.ColorConvertFloat4ToU32(
                Poser.UI.ColorEx.ApplyAlpha(Poser.UI.Crystarium.ActiveTheme.Border)));
        return new PaintOutput(null, null);
    }
}

/// <summary>
/// OverlayShell's <c>.checkRow</c>: a 28px pointer row at <c>--radius-md</c>
/// whose only chrome is its state fill — <c>--color-subtle-overlay</c> under
/// the pointer, <c>--color-primary-10</c> when it is the active one. Both are
/// flat: the CSS declares no transition on this row, so neither does this.
///
/// <para><c>Arg</c> is the active flag. The row's content — the check slot and
/// the label — is composed, so this paints the fill and nothing else and
/// returns no foreground: <c>.checkRow</c> states <c>--color-text-primary</c>,
/// which is the theme default the label already resolves.</para>
/// </summary>
internal sealed class CheckRowPainter : IInteractivePainter
{
    internal static readonly CheckRowPainter Instance = new();

    private CheckRowPainter()
    {
    }

    public PaintOutput Paint(in PaintInput input)
    {
        Theme theme = Poser.UI.Crystarium.ActiveTheme;
        // USER DECISION 2026-08-02, supersedes .checkRowActive's
        // --color-primary-10: the active row shares the hover's WHITEISH
        // overlay - the check glyph is what marks selection, exactly the
        // accepted dropdown-row pattern.
        Vector4 fill = input.Arg != 0 || (input.Hit.Hovered && !input.Disabled)
            ? theme.Chrome.ControlHover
            : default;
        if (fill.W <= 0f)
            return new PaintOutput(null, null);

        Poser.UI.BoxRenderer.Draw(
            input.DrawList,
            input.Hit.ScreenMin,
            input.Hit.ScreenMin + input.BoxSize,
            new Poser.UI.BoxStyle
            {
                BackgroundColor = fill,
                BorderRadius = theme.Radii.Control,
            });
        return new PaintOutput(null, null);
    }
}

/// <summary>
/// OverlayShell's <c>.checkBox</c>: a 14px square at <c>--radius-sm</c>, filled
/// <c>--color-black-20</c> under a 1px INSET outline at
/// <c>--color-pressed-overlay</c>. Checked it becomes solid
/// <c>--color-primary</c> with the outline dropped to transparent, which is why
/// the two states are one painter and not two boxes.
///
/// <para><c>--color-pressed-overlay</c> is declared by <c>tokens.css</c> but is
/// not carried by the generated projection, so it is derived here on the same
/// terms as <c>Chrome.DangerHover</c>: the theme's own overlay hue at .20.
/// </para>
/// </summary>
internal sealed class CheckBoxPainter : IInteractivePainter
{
    internal static readonly CheckBoxPainter Instance = new();

    private CheckBoxPainter()
    {
    }

    public PaintOutput Paint(in PaintInput input)
    {
        Theme theme = Poser.UI.Crystarium.ActiveTheme;
        bool @checked = input.Arg != 0;
        Vector4? outline = @checked
            ? null
            : theme.Chrome.ActiveOverlay with { W = 0.20f };
        Poser.UI.BoxRenderer.Draw(
            input.DrawList,
            input.Hit.ScreenMin,
            input.Hit.ScreenMin + input.BoxSize,
            new Poser.UI.BoxStyle
            {
                BackgroundColor = @checked
                    ? theme.Chrome.Primary
                    : theme.Chrome.InputWell,
                BorderRadius = theme.Radii.Medium,
                BorderWidth = outline is null ? 0f : 1f,
                BorderTopColor = outline,
                BorderRightColor = outline,
                BorderBottomColor = outline,
                BorderLeftColor = outline,
            });
        // .checkBoxChecked { color: rgba(255,255,255,.99) } — the glyph inside
        // takes it as currentColor rather than restating it.
        return new PaintOutput(@checked ? theme.Chrome.Checkmark : null, null);
    }
}
