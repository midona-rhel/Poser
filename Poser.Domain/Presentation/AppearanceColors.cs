using System;
using System.Numerics;

namespace Poser.Domain.Presentation;

public enum AppearanceColorChannel { Skin, Hair, Highlights, LeftEye, RightEye, Mouth, Feature }

public static class AppearanceColorSpace
{
    // Shader RGB is signed squared; opacity is linear, not a colour component.
    public static Vector4 ToShader(Vector4 value) => new(Square(value.X), Square(value.Y), Square(value.Z), value.W);
    public static Vector4 FromShader(Vector4 value) => new(Root(value.X), Root(value.Y), Root(value.Z), value.W);
    private static float Square(float value) => MathF.CopySign(value * value, value);
    private static float Root(float value) => MathF.CopySign(MathF.Sqrt(MathF.Abs(value)), value);
    public static bool IsFinite(Vector4 value) => float.IsFinite(value.X) && float.IsFinite(value.Y)
        && float.IsFinite(value.Z) && float.IsFinite(value.W);
}
