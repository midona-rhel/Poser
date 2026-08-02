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
internal sealed class PickerRulePainter : IPainter
{
    internal static readonly PickerRulePainter Instance = new();

    private PickerRulePainter()
    {
    }

    public bool NeedsHit => false;

    public PaintResult Paint(in PaintContext context)
    {
        float scale = ImGuiHelpers.GlobalScale;
        Vector2 max = context.Max;
        context.DrawList.AddRectFilled(
            new Vector2(context.Min.X, max.Y - scale),
            max,
            ImGui.ColorConvertFloat4ToU32(
                Poser.UI.ColorEx.ApplyAlpha(Poser.UI.Crystarium.ActiveTheme.Border)));
        return default;
    }
}

/// <summary>
/// OverlayShell's <c>.checkBox</c>: a 14px square filled <c>--color-black-20</c>
/// under a 1px INSET outline at <c>--color-pressed-overlay</c>. Checked it
/// becomes solid <c>--color-primary</c> with the outline dropped, which is why
/// the two states are one hook and not two boxes — and why it is a hook at
/// all: the per-side mitred outline is not the base's single-stroke border.
/// </summary>
internal sealed class CheckBoxPainter : IPainter
{
    internal static readonly CheckBoxPainter Instance = new();

    private CheckBoxPainter()
    {
    }

    public bool NeedsHit => false;

    public PaintResult Paint(in PaintContext context)
    {
        Theme theme = Poser.UI.Crystarium.ActiveTheme;
        bool @checked = context.Record.Selected;
        // --color-pressed-overlay is declared by tokens.css but is not carried
        // by the generated projection, so it is derived on the same terms as
        // Chrome.DangerHover: the theme's own overlay hue at .20.
        Vector4? outline = @checked
            ? null
            : theme.Chrome.ActiveOverlay with { W = 0.20f };
        Poser.UI.BoxRenderer.Draw(
            context.DrawList,
            context.Min,
            context.Max,
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
        return new PaintResult(@checked ? theme.Chrome.Checkmark : null, null);
    }
}
