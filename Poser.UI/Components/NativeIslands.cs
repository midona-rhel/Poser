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

/// <summary>
/// A picker-trigger bridge, retained by the caller. The legacy picker's
/// <c>Open</c> captures its anchor from the JUST-RESERVED item — a contract
/// the retained path cannot honour, because dispatch runs after the walk when
/// the last item is the root's own reservation. The trigger therefore stays
/// the imperative button inside the tree's box until the picker itself
/// migrates, and dies with it.
/// </summary>
public sealed class PickerTriggerState
{
    internal readonly PickerTriggerIsland Island = new();
}

internal sealed class PickerTriggerIsland : INativeElement
{
    private string _value = string.Empty;
    private Action? _onOpen;
    private bool _disabled;
    private string? _help;

    internal void Bind(string value, Action onOpen, bool disabled, string? help)
    {
        _value = value;
        _onOpen = onOpen;
        _disabled = disabled;
        _help = help;
    }

    public void Draw(string id, Vector2 min, Vector2 max)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float width = (max.X - min.X) / scale;
        // The imperative picker row's own recipe: the caption cut to the
        // trigger's padded box before the button renders it.
        string caption = LegacyCrystarium.TruncateText(
            _value,
            new TextStyle { Size = Crystarium.ActiveTheme.Typography.LabelSize },
            MathF.Max(
                1f,
                (max.X - min.X)
                    - Crystarium.ActiveTheme.Spacing.Six * 2f * scale));
        LegacyCrystarium.Button(
            caption,
            _onOpen,
            style: ControlStyle.Workspace with
            {
                Width = UiWidth.Fixed(width),
            },
            disabled: _disabled,
            help: _help,
            id: id);
    }
}
