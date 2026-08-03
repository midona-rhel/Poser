namespace Poser.UI;

/// <summary>
/// Shared filter/search field used by navigation and collection surfaces —
/// GlassInput's <c>search</c> variant (<c>.searchWrap</c> + <c>.searchIcon</c>
/// + <c>.searchInput</c>): a borderless, unfilled 36px row with a leading
/// magnifier, plus Poser's own clear affordance. Panes take the variant from
/// here so they do not recreate slightly different search inputs.
///
/// <para>Its natural height is the CSS 36; the compact surfaces that want the
/// 26px workspace rhythm ask for it explicitly through
/// <see cref="ControlStyle"/>.</para>
/// </summary>
public static partial class Crystarium
{
    /// <param name="textRise">Lifts the text alone, negative raises; the box
    /// and the leading glyph stay put. Zero for every accepted surface.</param>
    public static bool FilterPill(
        string id,
        string value,
        System.Action<string> onChange,
        string placeholder,
        ControlStyle style = default,
        float textRise = 0f)
        => TextInputCore(
            id,
            value,
            onChange,
            style,
            placeholder,
            clearable: true,
            search: true,
            disabled: false,
            help: null,
            textRise);
}
