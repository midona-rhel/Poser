using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

internal readonly record struct SwatchLayoutPlan(
    float HitSide,
    float DotRadius,
    float SlotGap,
    float CenterPitch,
    float PaletteWidth,
    float ActiveOuterRadius);
