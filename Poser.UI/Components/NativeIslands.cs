using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.UI.Reactive;

namespace Poser.UI;

/// <summary>
/// The numeric well's retained half. The well is a NATIVE island — its
/// type-in mode is ImGui's own InputFloat, squarely in the IME escape-hatch
/// class, and its drag is the imperative control's per-pixel scrub — so the
/// tree reserves the box and the ONE legacy implementation draws and edits
/// inside it. The caller retains this holder exactly as it retains a file
/// dialog: one per row, for the row's lifetime.
///
/// <para>A well also carries the VECTOR adapter a three-axis row needs: the
/// row hands out a <see cref="Vector3"/> handler and a well reports a float,
/// and the bridge between them is allocated once per well rather than three
/// times per frame.</para>
/// </summary>
public sealed class NumericWellState
{
    internal readonly NumericWellIsland Island = new();

    // Written when a vector row binds the well, read at dispatch.
    private Vector3 _vector;
    private int _axis;
    private Action<Vector3>? _onVectorChange;

    internal readonly Action<float> AxisChanged;

    public NumericWellState() => AxisChanged = ChangeAxis;

    internal void BindAxis(Vector3 vector, int axis, Action<Vector3> onChange)
    {
        _vector = vector;
        _axis = axis;
        _onVectorChange = onChange;
    }

    /// <summary>The imperative row's captured copy, preserved: the composed
    /// vector becomes the well's own running value, so a move that lands
    /// after the declaration composes onto what the well already reported
    /// rather than onto the stale declaration.</summary>
    private void ChangeAxis(float next)
    {
        Vector3 changed = _vector;
        if (_axis == 0)
            changed.X = next;
        else if (_axis == 1)
            changed.Y = next;
        else
            changed.Z = next;
        _vector = changed;
        _onVectorChange?.Invoke(changed);
    }
}

internal sealed class NumericWellIsland : INativeElement
{
    private float _value;
    private Action<float>? _onChange;
    private Action? _onCommit;
    private float _perPixel;
    private string _format = "0.00";
    private bool _disabled;
    private string _axis = string.Empty;
    private Vector4 _accent;

    internal void Bind(
        float value, Action<float> onChange, Action? onCommit,
        float perPixel, string format, bool disabled,
        string axis, Vector4 accent)
    {
        _value = value;
        _onChange = onChange;
        _onCommit = onCommit;
        _perPixel = perPixel;
        _format = format;
        _disabled = disabled;
        _axis = axis;
        _accent = accent;
    }

    public void Draw(string id, Vector2 min, Vector2 max)
    {
        float scale = ImGuiHelpers.GlobalScale;
        LegacyCrystarium.AxisWell(
            id,
            _axis,
            _value,
            _onChange ?? (static _ => { }),
            _onCommit,
            _accent,
            _perPixel,
            _format,
            ControlStyle.Workspace with
            {
                Width = UiWidth.Fixed((max.X - min.X) / scale),
            },
            _disabled);
    }
}
