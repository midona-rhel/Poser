using System;

namespace Poser.UI;

/// <summary>
/// Typed pseudo-class state. Bit-flags so an element can be in multiple states
/// (e.g. <c>Hover | Active</c>). Selector matching: a rule applies when all
/// of its required pseudos are present in the current state.
/// </summary>
[Flags]
public enum PseudoState
{
    None     = 0,
    Hover    = 1 << 0,
    Active   = 1 << 1,
    Disabled = 1 << 2,
}
