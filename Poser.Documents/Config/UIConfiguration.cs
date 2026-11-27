using System;

namespace Poser.Documents.Config;
public enum UITheme
{
    Auto,
    Light,
    LightGray,
    Gray,
    Dark,
    Blue,
    Purple,
}

public class UIConfiguration
{
    /// <summary>The least fill Poser's windows are drawn with. Below half,
    /// text on a window no longer reads over a bright scene. The Window
    /// opacity slider starts here, and a lower value in an edited or older
    /// settings file is raised to it.</summary>
    public const float MinimumFillOpacity = 0.5f;

    private float _fillOpacity = 1f;
    public UITheme Theme { get; set; } = UITheme.Dark;
    public int AccentIndex { get; set; } = 0;
    public float FillOpacity
    {
        get => _fillOpacity;
        set => _fillOpacity = ClampFillOpacity(value);
    }
    public bool BackdropBlur { get; set; } = true;
    private static float ClampFillOpacity(float value) =>
        float.IsFinite(value) ? Math.Clamp(value, MinimumFillOpacity, 1f) : 1f;
    public bool DetachedShell { get; set; }
    public bool HideHandlesByDefault { get; set; }

    public System.Collections.Generic.Dictionary<string, bool> SectionDisclosure { get; set; } = new();
    public System.Collections.Generic.Dictionary<string, WindowPlacement> DetachedPlacements { get; set; } = new();

    public bool ShowTreeGuides { get; set; } = true;
    public bool MapMirrorSelection { get; set; }
    public bool ShowInGPose { get; set; } = true;
    public bool ShowInCutscene { get; set; } = true;
    public bool ShowWhenGameUiHidden { get; set; }
    public bool SwapRotationXY { get; set; }

    /// <summary>The frame profiler window (Settings, General, DIAGNOSTICS):
    /// per-draw-unit frame costs. Off by default — measuring costs a little,
    /// and the ledger only records while somebody is asking.</summary>
    public bool ShowFrameProfiler { get; set; }

    /// <summary>The game function check window (Settings, General, DIAGNOSTICS):
    /// every game function, hook and offset Poser depends on, checked against the
    /// running game.</summary>
    public bool ShowNativeCheck { get; set; }

    /// <summary>Which panel the inspector shows: 0 the selected target,
    /// 1 the environment, 2 the scene. Selecting any entity snaps back
    /// to the target panel.</summary>
    public int InspectorMode { get; set; }


    /// <summary>Hide the windows while a gizmo drag is held (#77) — the
    /// overlays stay, the scene clears. Off by default.</summary>
    public bool HideWhileManipulating { get; set; }

    /// <summary>Hide the windows while the camera moves: flight keys held,
    /// a look drag, or a drag on empty space that turns the camera.</summary>
    public bool HideWhileMovingCamera { get; set; }

    /// <summary>The gizmo tool in use when Poser last ran: Move, Rotate,
    /// Scale or Universal, by the tool enum's name.</summary>
    public string LastTransformTool { get; set; } = "Rotate";

    /// <summary>The inspector rail lives in its own window — the third
    /// split, beside the detached shell.</summary>
    public bool SplitInspector { get; set; }

    /// <summary>The dependent option: the world gizmo's chrome hides with
    /// the shell — the drag's own sweep and readout never do.</summary>
    public bool HideGizmoWhileManipulating { get; set; }

    public bool KeepIkGizmoVisibleWhileManipulating { get; set; } = true;

    /// <summary>
    /// One key binding per action, as settings files older than version 3
    /// stored them. Read from such a file and moved into
    /// <see cref="Bindings"/> by <see cref="MigrateKeybindsToSlots"/>; never
    /// written to the file.
    /// </summary>
    public System.Collections.Generic.Dictionary<string, string> Keybinds { get; set; } = new();
    public System.Collections.Generic.Dictionary<string, KeybindSlots> Bindings { get; set; } = new();
    public void MigrateKeybindsToSlots()
    {
        foreach (var (action, chord) in Keybinds)
        {
            if (string.IsNullOrWhiteSpace(chord) || Bindings.ContainsKey(action))
                continue;
            Bindings[action] = new KeybindSlots(chord);
        }
        Keybinds.Clear();
    }
}

public sealed record WindowPlacement(System.Numerics.Vector2 Position, System.Numerics.Vector2 Size);
