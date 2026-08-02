namespace Poser.UI;

/// <summary>A horizontal flow. Composition is the point: a container states a
/// sheet and children and nothing else.</summary>
public readonly record struct Row
{
    /// <summary>The family sheet; unset is the plain flow.</summary>
    public SheetRef Sheet { get; init; }

    public ElementSheet? Style { get; init; }

    public UiChildren Children { get; init; }

    public UiKey Key { get; init; }

    /// <summary>A single child needs no collection: user-defined
    /// conversions do not chain, so the one-child form is stated.</summary>
    public static implicit operator UiChildren(Row row) => (UiNode)row;

    public static implicit operator UiNode(Row row) => new Element
    {
        Sheet = row.Sheet.IsNone ? SheetFamily.Row : row.Sheet,
        Style = row.Style,
        Children = row.Children,
        Key = row.Key,
    };
}

/// <inheritdoc cref="Row"/>
public readonly record struct Column
{
    /// <inheritdoc cref="Row.Sheet"/>
    public SheetRef Sheet { get; init; }

    public ElementSheet? Style { get; init; }

    public UiChildren Children { get; init; }

    public UiKey Key { get; init; }

    /// <summary>A single child needs no collection: user-defined
    /// conversions do not chain, so the one-child form is stated.</summary>
    public static implicit operator UiChildren(Column column) => (UiNode)column;

    public static implicit operator UiNode(Column column) => new Element
    {
        Sheet = column.Sheet.IsNone ? SheetFamily.Column : column.Sheet,
        Style = column.Style,
        Children = column.Children,
        Key = column.Key,
    };
}

/// <summary>An in-rect stack: every child is placed in the same box.</summary>
public readonly record struct Stack
{
    /// <inheritdoc cref="Row.Sheet"/>
    public SheetRef Sheet { get; init; }

    public ElementSheet? Style { get; init; }

    public UiChildren Children { get; init; }

    public UiKey Key { get; init; }

    /// <summary>A single child needs no collection: user-defined
    /// conversions do not chain, so the one-child form is stated.</summary>
    public static implicit operator UiChildren(Stack stack) => (UiNode)stack;

    public static implicit operator UiNode(Stack stack) => new Element
    {
        Sheet = stack.Sheet.IsNone ? SheetFamily.Stack : stack.Sheet,
        Style = stack.Style,
        Children = stack.Children,
        Key = stack.Key,
    };
}
