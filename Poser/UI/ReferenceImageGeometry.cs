using System;
using System.Numerics;

namespace Poser.UI;

/// <summary>
/// The reference window's ONE sizing rule: its aspect ratio IS the picture's,
/// and every resize resolves back to it.
///
/// <para>Resolved by CLAMP-ON-FRAME rather than by an ImGui size-constraint
/// callback. Ktisis takes the callback route
/// (<c>Ktisis/Interface/Overlay/RefOverlay.cs:83-107</c>), which needs an
/// <c>unsafe</c> static and a pinned struct because the callback carries no
/// managed state; Poser's windows are <c>Dalamud.Interface.Windowing.Window</c>
/// instances that already state <c>Size</c>/<c>SizeCondition</c> from
/// <c>PreDraw</c> (MainWindow's collapse), so the same
/// seam carries this and the codebase keeps one sizing idiom.</para>
///
/// <para>WHICH AXIS WINS is the whole subtlety. Ktisis always derives height
/// from width, so dragging the bottom edge of a window fights the callback and
/// the window appears frozen vertically. Here the axis the pointer actually
/// moved further on drives, measured against the last CONFORMANT size — so a
/// bottom-edge drag resizes by height and a right-edge drag by width, and a
/// corner drag follows whichever the hand favoured.</para>
/// </summary>
public static class ReferenceImageGeometry
{
    /// <summary>Logical floor on either side. Small enough to park a picture
    /// as a thumbnail, large enough that the title bar's controls still fit
    /// when it fades in.</summary>
    public const float MinimumSide = 120f;

    /// <summary>
    /// The aspect-locked size for a requested one.
    /// </summary>
    /// <param name="previous">The last size this window actually wore — the
    /// reference the requested size's drift is measured against. Zero on the
    /// first frame, where the drift comparison degenerates to "width drives",
    /// which is what a freshly seated window wants.</param>
    /// <param name="requested">What ImGui ended up with after the user's
    /// resize.</param>
    /// <param name="aspect">width / height of the PICTURE. Non-positive means
    /// no picture has resolved yet, and the requested size passes through
    /// unchanged rather than collapsing to zero.</param>
    public static Vector2 ResolveAspect(
        Vector2 previous,
        Vector2 requested,
        float aspect,
        float minimumSide = MinimumSide)
    {
        if (!(aspect > 0f) || !float.IsFinite(aspect))
            return requested;
        if (!float.IsFinite(requested.X) || !float.IsFinite(requested.Y))
            return previous;

        float floor = MathF.Max(1f, minimumSide);
        float width;
        float height;
        if (MathF.Abs(requested.Y - previous.Y)
            > MathF.Abs(requested.X - previous.X))
        {
            height = requested.Y;
            width = height * aspect;
        }
        else
        {
            width = requested.X;
            height = width / aspect;
        }

        // The floor applies to the SHORT side and then re-derives the other,
        // so clamping can never itself break the ratio.
        if (width < floor)
        {
            width = floor;
            height = width / aspect;
        }
        if (height < floor)
        {
            height = floor;
            width = height * aspect;
        }
        return new Vector2(width, height);
    }

    /// <summary>
    /// The size a picture is first seated at: its own pixels, shrunk to fit a
    /// share of the viewport when they are larger than it. Never grown — a
    /// small sheet stays small rather than being blown up to a soft mess.
    /// </summary>
    public static Vector2 InitialSize(
        Vector2 pixels, Vector2 viewport, float viewportShare = 0.45f)
    {
        if (!(pixels.X > 0f) || !(pixels.Y > 0f))
            return Vector2.Zero;
        float aspect = pixels.X / pixels.Y;
        var budget = viewport * MathF.Max(0.05f, viewportShare);
        float scale = 1f;
        if (budget.X > 0f && pixels.X > budget.X)
            scale = budget.X / pixels.X;
        if (budget.Y > 0f && pixels.Y * scale > budget.Y)
            scale = budget.Y / pixels.Y;
        return ResolveAspect(
            Vector2.Zero, pixels * scale, aspect);
    }
}
