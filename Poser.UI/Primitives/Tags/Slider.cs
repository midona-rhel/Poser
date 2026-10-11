using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

/// <summary>
/// Maps slider travel to its value range.
/// </summary>
public enum SliderScale
{
    /// <summary>Position follows the value fraction.</summary>
    Linear,

    /// <summary>Position follows an exponential value fraction.</summary>
    Log,

    /// <summary>The measured multi-decade mapping: LINEAR from the
    /// minimum to max/10^decades across the FIRST HALF of the travel,
    /// then one decade per equal remaining segment — 0→1 to the middle,
    /// 10 at three-quarters, 100 at the end of a 0–100 range. The
    /// curvature parameter carries the decade count for this scale.</summary>
    Decades,
}
