using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public record struct FloatingSurfaceProps
{
    public float Width;
    public float Height;
    public float Padding;
    public Vector2 AnchorMin;
    public Vector2 AnchorMax;
    public FloatingSurfaceTreatment Treatment;
}

public enum FloatingSurfaceTreatment
{
    Glass,
    Unframed,
}
