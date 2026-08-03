using System;
using System.Numerics;
using Poser.UI.Reactive;

namespace Poser.UI;

/// <summary>
/// The shared filter/search field's RETAINED half. The field is a native
/// island — a text field's caret, selection, clipboard and IME composition are
/// ImGui's own retained widget state — so the tree reserves the box and the one
/// legacy <see cref="LegacyCrystarium.FilterPill"/> draws and edits inside it.
///
/// <para>The caller retains this holder, exactly as it retains a
/// <see cref="NumericWellState"/>: one per field, for the field's lifetime.
/// The picker's own filter is NOT this holder — it is a component-local draft
/// nobody outside the open surface can act on — and this is the seam for the
/// fields whose value belongs to the caller's view model.</para>
/// </summary>
public sealed class FilterFieldState
{
    internal readonly FilterFieldIsland Island = new();
}

internal sealed class FilterFieldIsland : INativeElement
{
    private static readonly Action<string> Ignore = static _ => { };

    private string _value = string.Empty;
    private Action<string> _onChange = Ignore;
    private string _placeholder = string.Empty;
    private ControlStyle _style;
    private float _textRise;

    internal void Bind(
        string value,
        Action<string> onChange,
        string placeholder,
        in ControlStyle style,
        float textRise)
    {
        _value = value;
        _onChange = onChange;
        _placeholder = placeholder;
        _style = style;
        _textRise = textRise;
    }

    public void Draw(string id, Vector2 min, Vector2 max)
    {
        _ = min;
        _ = max;
        LegacyCrystarium.FilterPill(
            id, _value, _onChange, _placeholder, _style, _textRise);
    }
}

public static partial class Crystarium
{
    /// <summary>
    /// Declares the shared filter field at <paramref name="logicalSize"/>. The
    /// BOX is the tree's — the flow decides where the field sits and how much
    /// room the next sibling gets — while <paramref name="style"/> is the
    /// field's own density and width, which is the legacy control's business
    /// and stays stated in its own terms.
    /// </summary>
    public static UiNode FilterField(
        FilterFieldState state,
        string value,
        Action<string> onChange,
        string placeholder,
        Vector2 logicalSize,
        ControlStyle style = default,
        float textRise = 0f,
        UiKey key = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(onChange);
        state.Island.Bind(value, onChange, placeholder, in style, textRise);
        return Native(state.Island, logicalSize, key);
    }
}
