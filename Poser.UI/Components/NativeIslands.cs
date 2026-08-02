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
/// </summary>
public sealed class NumericWellState
{
    internal readonly NumericWellIsland Island = new();
}

internal sealed class NumericWellIsland : INativeElement
{
    private float _value;
    private Action<float>? _onChange;
    private Action? _onCommit;
    private float _perPixel;
    private string _format = "0.00";
    private bool _disabled;

    internal void Bind(
        float value, Action<float> onChange, Action? onCommit,
        float perPixel, string format, bool disabled)
    {
        _value = value;
        _onChange = onChange;
        _onCommit = onCommit;
        _perPixel = perPixel;
        _format = format;
        _disabled = disabled;
    }

    public void Draw(string id, Vector2 min, Vector2 max)
    {
        float scale = ImGuiHelpers.GlobalScale;
        LegacyCrystarium.AxisWell(
            id,
            string.Empty,
            _value,
            _onChange ?? (static _ => { }),
            _onCommit,
            Crystarium.ActiveTheme.FormValue,
            _perPixel,
            _format,
            ControlStyle.Workspace with
            {
                Width = UiWidth.Fixed((max.X - min.X) / scale),
            },
            _disabled);
    }
}
