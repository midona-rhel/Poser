using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

/// <summary>A segmented control shown under the search field.</summary>
public readonly record struct PickerStrip(
    string[] Labels, int Selected, Action<int> OnChange);

/// <summary>Optional picker behavior and row presentation.</summary>
public record struct PickerOptions<T> where T : class
{
    /// <summary>Returns the visible items for a query.</summary>
    public Func<string, IReadOnlyList<T>>? Query;

    /// <summary>Returns a row texture. A glyph is used when this returns zero.</summary>
    public Func<T, nint>? Texture;

    public Func<T, TablerIcon?>? Glyph;

    /// <summary>Right-aligned mono readout.</summary>
    public Func<T, string?>? Badge;

    /// <summary>Whether a row owns a selectable mark in multi-select mode.</summary>
    public Func<T, bool>? IsSelectable;

    /// <summary>A row's own colour under the overlays — a dye's. The text
    /// on it contrasts against that colour, not the theme.</summary>
    public Func<T, Vector4?>? RowFill;

    public PickerStrip? Strip;
    public PickerStrip? SecondStrip;

    /// <summary>Logical panel width; 0 takes the theme's picker width.</summary>
    public float Width;
}
