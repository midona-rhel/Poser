using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI.Controls;

/// <summary>
/// The frame-stamped manipulation state the shell windows, the overlays and
/// the inspector rail share: who owns the pointer, whether a drag is held,
/// and how far the shell has faded for it. One instance per plugin load,
/// written by the gizmo surfaces and the UI root, read by every window.
/// </summary>
public sealed class ManipulationState
{
    private const float FadeSeconds = 0.10f;

    private int _dragUntilFrame = -1;
    private int _shellUntilFrame = -1;
    private int _readoutFrame = -1;
    private (Vector2 Min, string Text) _readout;
    private int _pointerFrame = -1;

    // ── pointer ownership ────────────────────────────────────────────────
    // While a ring drag — or its release frame — owns the pointer, selection
    // surfaces (skeleton overlay, 3D view) must not treat the click as a
    // bone/actor pick.

    /// <summary>Call every frame the pointer engages a custom gizmo.</summary>
    public void HoldPointer() =>
        _pointerFrame = ImGui.GetFrameCount();

    public bool PointerOwned =>
        DragHeld || ImGui.GetFrameCount() == _pointerFrame;

    // ── the held drag ────────────────────────────────────────────────────
    // Frame-stamped hold for a LIVE world drag — the hide signal. Distinct
    // from pointer ownership, which hover also holds so a click on a handle
    // is never a pick: only a held gesture holds this.

    public void HoldDrag() =>
        _dragUntilFrame = ImGui.GetFrameCount() + 1;

    public bool DragHeld =>
        ImGui.GetFrameCount() <= _dragUntilFrame;

    /// <summary>A drag held on a control INSIDE a shell window (the
    /// inspector's rotation ball): the shell fades like it does for a
    /// world drag, but the window keeps drawing at zero alpha so the
    /// held item lives on, and the readout is handed to the overlay.</summary>
    public void HoldDragFromShell(Vector2 readoutMin, string readout)
    {
        _shellUntilFrame = ImGui.GetFrameCount() + 1;
        _readoutFrame = ImGui.GetFrameCount() + 1;
        _readout = (readoutMin, readout);
    }

    public bool ShellDragHeld =>
        ImGui.GetFrameCount() <= _shellUntilFrame;

    /// <summary>The shell drag's readout for this frame, if any.</summary>
    public (Vector2 Min, string Text)? ShellReadout =>
        ImGui.GetFrameCount() <= _readoutFrame ? _readout : null;

    // ── hide while manipulating (#77) ────────────────────────────────────
    // The setting AND a live drag — hovering a handle never hides. Written
    // once per frame by the UI root; the shell fades rather than popping,
    // and windows skip their draw only when fully faded. Reference images
    // and the overlays deliberately stay visible.

    /// <summary>Whether the shell windows hide this frame.</summary>
    public bool HideActive { get; set; }

    /// <summary>The dependent option: the world gizmo's CHROME rides the
    /// same fade — the drag's own sweep and readout never do.</summary>
    public bool HideGizmo { get; set; }

    /// <summary>The shell's eased opacity: 1 shown, 0 hidden.</summary>
    public float Opacity { get; private set; } = 1f;

    /// <summary>Advanced once per frame by the UI root, after
    /// <see cref="HideActive"/> is written.</summary>
    public void AdvanceHide()
    {
        float step = ImGui.GetIO().DeltaTime / FadeSeconds;
        Opacity = HideActive
            ? MathF.Max(0f, Opacity - step)
            : MathF.Min(1f, Opacity + step);
    }

    /// <summary>Fully faded: the shell windows skip their draw.</summary>
    public bool Hidden => Opacity <= 0f;

    /// <summary>Scopes the fade over one window's draw: pushes the global
    /// alpha (which every widget color multiplies through) while the
    /// shell is mid-fade, and pops it on ANY exit path.</summary>
    public FadeHandle FadeScope()
    {
        bool pushed = Opacity < 1f;
        // Never exactly zero: ImGui hides a window whose alpha is zero
        // and skips its items, and a drag held on one of them would end.
        // A hair above zero is invisible and alive.
        if (pushed)
            ImGui.PushStyleVar(
                ImGuiStyleVar.Alpha,
                ImGui.GetStyle().Alpha * MathF.Max(0.002f, Opacity));
        return new FadeHandle(pushed);
    }

    public readonly struct FadeHandle : IDisposable
    {
        private readonly bool _pushed;
        internal FadeHandle(bool pushed) => _pushed = pushed;
        public void Dispose()
        {
            if (_pushed)
                ImGui.PopStyleVar();
        }
    }
}
