using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI;

internal readonly record struct SidebarVisibilityPlan(
    float EyeOpacity,
    Vector2 PupilCenter,
    float PupilRadius,
    float PupilOpacity);

public readonly record struct SidebarTrailingActionGeometry(
    Vector2 HitMin,
    Vector2 HitMax,
    Vector2 GlyphMin,
    Vector2 GlyphMax,
    Vector2 Center,
    Vector2 SpawnAnchor,
    float GlyphSide);
