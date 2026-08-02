namespace Poser.UI;

/// <summary>
/// Which paint properties animate, and with what curve. One field per
/// animatable property rather than a list, so "Fill animates over 150ms CSS
/// ease" is a value the base painter reads, never arithmetic a control
/// writes.
///
/// <para>A null field is an INSTANT swap — exactly what CSS means by a
/// property missing from the transition list, which is why the accepted
/// button's border and text change with no ramp at all.</para>
/// </summary>
public readonly record struct MotionSheet
{
    /// <summary><c>transition: background 150ms ease</c>.</summary>
    public Transition? Fill { get; init; }
}
