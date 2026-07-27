using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI.Views;

/// <summary>
/// Bindable state for <see cref="SettingsView"/>. The plugin binder maps it to
/// ConfigurationService (see docs/architecture/ui-workspace.md).
/// </summary>
public sealed class SettingsViewModel
{
    public int Category = 1;                        // 0 General, 1 Display, 2 Skeleton, 3 UI, 4 Keybinds, 5 About
    public float BoneDotRadius = 5f;
    public Vector4 OverlaySelected  = Hex(0x3297FF);
    public Vector4 OverlayHovered   = Hex(0xFFFFFF);
    public Vector4 OverlayInactive  = Hex(0x6B6E76);
    public Vector4 OverlayIkChain   = Hex(0xFF9F0A);
    public Vector4 OverlayMirrored  = Hex(0x7ED3A0);
    public bool NsfwBones;
    public bool AnonymousMode = true;
    public int AccentIndex;

    // General
    public bool OpenOnGPose = true;
    public bool CloseWithGPose;

    // Skeleton overlay drawing
    public bool ShowSkeletonLines = true;
    public float BoneLineThickness = 1.0f;
    public float BoneLineOpacity = 0.23f;

    // Layout configurability (user requirement: everything detachable/rearrangeable).
    public int SidebarDock;      // 0 Left, 1 Right, 2 Floating, 3 Hidden
    public int InspectorDock = 1; // 0 Left, 1 Right, 2 Floating, 3 Hidden
    public bool TreeGuides = true;

    // Keybinds: (action label, current binding). Rebinding captures the next key press.
    public (string Action, string Binding)[] Keybinds =
    {
        ("Undo", "Ctrl+Z"), ("Redo", "Ctrl+Y"),
        ("Translate mode", "Ctrl+1"), ("Rotate mode", "Ctrl+2"), ("Scale mode", "Ctrl+3"),
        ("Universal mode", "Ctrl+4"),
        ("Hide UI", "Ctrl+H"),
    };
    public int RebindingIndex = -1;

    public string Version = "dev";

    public Action? OnSave;
    public Action? OnCancel;
    public Action? OnClose;
    public Action? OnOpenRepository;

    internal static Vector4 Hex(uint rgb) => new(
        ((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
}

/// <summary>
/// Settings window — pixel transcription of the approved M5 mockup
/// (docs/mockups/m5-settings.html): 720×520 window, 44px header, 200px nav rail
/// (26px SidebarRows), settings pane (.ph section headers, .setRow rows with
/// 130px min-width 13px/600 labels), 44px footer (black @ .10 band, Cancel +
/// primary Save). Draws at an absolute origin inside the in-game window shell.
/// </summary>
public static class SettingsView
{
    public const float DesignWidth = 720f;
    public const float DesignHeight = 520f;

    private static readonly Vector4 BgApp          = new(24 / 255f, 25 / 255f, 27 / 255f, 1f);
    private static readonly Vector4 Surface1       = new(36 / 255f, 37 / 255f, 40 / 255f, 1f);
    private static readonly Vector4 TextPrimary    = new(1f, 1f, 1f, 1f);
    private static readonly Vector4 TextSecondary  = new(1f, 1f, 1f, 0.72f);
    private static readonly Vector4 TextTertiary   = new(1f, 1f, 1f, 0.50f);
    private static readonly Vector4 BorderPrimary  = new(1f, 1f, 1f, 0.14f);
    private static readonly Vector4 BorderSecondary = new(1f, 1f, 1f, 0.08f);
    private static readonly Vector4 Black10        = new(0f, 0f, 0f, 0.10f);

    private static readonly (TablerIcon Icon, string Label)[] Nav =
    {
        (TablerIcon.Sliders,     "General"),
        (TablerIcon.Monitor,     "Display"),
        (TablerIcon.Bone,        "Skeleton"),
        (TablerIcon.LayoutPanel, "UI"),
        (TablerIcon.Keyboard,    "Keybinds"),
        (TablerIcon.Info,        "About"),
    };

    public static void Draw(SettingsViewModel vm, Vector2 origin)
    {
        float s = ImGuiHelpers.GlobalScale;
        var size = new Vector2(DesignWidth, DesignHeight) * s;
        var min = origin;
        var max = origin + size;
        var dl = ImGui.GetWindowDrawList();

        dl.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(BgApp)), 10f * s);
        Crystarium.FloatingSurface.DrawBorder(min, max, 10f);

        float headH = 44f * s, footH = 44f * s, navW = 200f * s;
        float bodyTop = min.Y + headH, bodyBottom = max.Y - footH;

        // ── header: title 14px/500 at 16px padding, X close at the right, inset bottom hairline
        ViewText.Label(new Vector2(min.X + 16f * s, min.Y + (headH - 20f * s) / 2f), "Settings", 14f, FontWeight.Medium, TextPrimary);
        dl.AddRectFilled(new Vector2(min.X, bodyTop - 1f * s), new Vector2(max.X, bodyTop),
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(BorderSecondary)));
        ImGui.SetCursorScreenPos(new Vector2(max.X - (16f + 24f) * s, min.Y + (headH - 24f * s) / 2f));
        ViewText.CloseBox("##settings-close", dl, s, vm.OnClose);

        // ── nav rail: surface-1, border-right, 8px padding, 26px SidebarRows
        dl.AddRectFilled(new Vector2(min.X, bodyTop), new Vector2(min.X + navW, bodyBottom),
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(Surface1)));
        dl.AddRectFilled(new Vector2(min.X + navW - 1f * s, bodyTop), new Vector2(min.X + navW, bodyBottom),
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(BorderPrimary)));
        ImGui.SetCursorScreenPos(new Vector2(min.X + 8f * s, bodyTop + 8f * s));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (ImGui.BeginChild("##settings-nav", new Vector2(navW - 16f * s, bodyBottom - bodyTop - 16f * s), false,
            ImGuiWindowFlags.NoScrollbar))
        {
            for (int i = 0; i < Nav.Length; i++)
            {
                if (Crystarium.SidebarRow($"##nav-{i}", Nav[i].Label, new SidebarRowProps
                    {
                        Icon = Nav[i].Icon,
                        Selected = vm.Category == i,
                    }))
                    vm.Category = i;
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleVar();

        // ── pane: 12px 20px 20px padding, scrollable
        ImGui.SetCursorScreenPos(new Vector2(min.X + navW, bodyTop));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(20f * s, 12f * s));
        if (ImGui.BeginChild("##settings-pane", new Vector2(max.X - min.X - navW, bodyBottom - bodyTop), false,
            ImGuiWindowFlags.AlwaysUseWindowPadding)) // borderless children ignore WindowPadding otherwise
        {
            switch (vm.Category)
            {
                case 0: DrawGeneralPane(vm, s); break;
                case 1: DrawDisplayPane(vm, s); break;
                case 2: DrawSkeletonPane(vm, s); break;
                case 3: DrawUiPane(vm, s); break;
                case 4: DrawKeybindsPane(vm, s); break;
                default: DrawAboutPane(vm, s); break;
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleVar();

        // ── footer: black-10 band, inset top hairline, right-aligned Cancel + primary Save
        dl.AddRectFilled(new Vector2(min.X, bodyBottom), max,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(Black10)),
            10f * s, ImDrawFlags.RoundCornersBottom);
        dl.AddRectFilled(new Vector2(min.X, bodyBottom), new Vector2(max.X, bodyBottom + 1f * s),
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(BorderSecondary)));
        // widths pinned (glyph advances differ browser-vs-ImGui; same rule as the modal cell)
        float saveW = 64f, cancelW = 76f, gap = 6f;
        float bx = max.X - 12f * s - (saveW + gap + cancelW) * s;
        ImGui.SetCursorScreenPos(new Vector2(bx, bodyBottom + (footH - 32f * s) / 2f));
        if (Crystarium.Button("Cancel", width: cancelW))
            vm.OnCancel?.Invoke();
        ImGui.SameLine(0f, gap * s);
        if (Crystarium.Button("Save", primary: true, width: saveW))
            vm.OnSave?.Invoke();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Display pane — M5's exemplar pane (BONE OVERLAY / FILTERS & PRIVACY / THEME)

    private static void DrawDisplayPane(SettingsViewModel vm, float s)
    {
        SectionHeader("BONE OVERLAY", first: true, s);

        // Bone dot radius: slider (max-width 220) + mono value
        RowStart(s);
        RowLabel("Bone dot radius", s, controlY: 10f);  // slider 14px
        Crystarium.Slider("##dot-radius", ref vm.BoneDotRadius, 2f, 12f, new SliderProps
        {
            Style = new SliderStyle { Width = Sizing.Fixed(220) },
        });
        ImGui.SameLine(0f, 12f * s);
        ViewText.Label(ImGui.GetCursorScreenPos() + new Vector2(0f, 1f * s),
            $"{(int)MathF.Round(vm.BoneDotRadius)} px", 12f, FontWeight.Regular, TextSecondary, mono: true);
        ImGui.Dummy(new Vector2(50f * s, 14f * s)); // advance past the value label
        RowEnd(s);

        // Overlay colors: five captioned wells
        RowStart(s, tall: true);
        RowLabel("Overlay colors", s, controlY: 6f);    // M5 tall row pads 6
        WellStack("##ov-sel", ref vm.OverlaySelected, "selected", s);
        WellStack("##ov-hov", ref vm.OverlayHovered, "hovered", s);
        WellStack("##ov-ina", ref vm.OverlayInactive, "inactive", s);
        WellStack("##ov-ik",  ref vm.OverlayIkChain, "IK chain", s);
        WellStack("##ov-mir", ref vm.OverlayMirrored, "mirrored", s);
        RowEnd(s, tall: true);

        SectionHeader("FILTERS & PRIVACY", first: false, s);

        RowStart(s);
        RowLabel("NSFW bones", s, controlY: 7f);        // switch 20px
        Crystarium.Switch("##nsfw", ref vm.NsfwBones);
        ImGui.SameLine(0f, 12f * s);
        ViewText.Label(ImGui.GetCursorScreenPos() + new Vector2(0f, 4f * s),
            "show IVCS / extended bone groups in the bone map and sidebar tree", 11f, FontWeight.Regular, TextTertiary, wrap: true);
        RowEnd(s);

        RowStart(s);
        RowLabel("Anonymous mode", s, controlY: 7f);
        Crystarium.Switch("##anon", ref vm.AnonymousMode);
        ImGui.SameLine(0f, 12f * s);
        ViewText.Label(ImGui.GetCursorScreenPos() + new Vector2(0f, 4f * s),
            "mask character names (\"Actor 1\", \"Actor 2\") in the UI", 11f, FontWeight.Regular, TextTertiary, wrap: true);
        RowEnd(s);

        SectionHeader("THEME", first: false, s);

        RowStart(s);
        RowLabel("Accent", s, controlY: 3f);            // swatch 28px
        var accents = new[] { SettingsViewModel.Hex(0x3297FF), SettingsViewModel.Hex(0x7ED3A0), SettingsViewModel.Hex(0xE8C15A), SettingsViewModel.Hex(0xB78CFF), SettingsViewModel.Hex(0xFF8FA3) };
        for (int i = 0; i < accents.Length; i++)
        {
            if (i > 0) ImGui.SameLine(0f, 12f * s);
            if (Crystarium.Swatch($"##accent-{i}", accents[i], active: vm.AccentIndex == i))
                vm.AccentIndex = i;
        }
        RowEnd(s);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Remaining panes — same M5 row grammar as the Display pane.

    private static void DrawGeneralPane(SettingsViewModel vm, float s)
    {
        SectionHeader("BEHAVIOR", first: true, s);

        RowStart(s);
        RowLabel("Open with GPose", s, controlY: 7f);
        Crystarium.Switch("##open-gpose", ref vm.OpenOnGPose);
        ImGui.SameLine(0f, 12f * s);
        ViewText.Label(ImGui.GetCursorScreenPos() + new Vector2(0f, 4f * s),
            "show the Poser window automatically when entering GPose", 11f, FontWeight.Regular, TextTertiary, wrap: true);
        RowEnd(s);

        RowStart(s);
        RowLabel("Close with GPose", s, controlY: 7f);
        Crystarium.Switch("##close-gpose", ref vm.CloseWithGPose);
        ImGui.SameLine(0f, 12f * s);
        ViewText.Label(ImGui.GetCursorScreenPos() + new Vector2(0f, 4f * s),
            "hide all Poser windows when leaving GPose", 11f, FontWeight.Regular, TextTertiary, wrap: true);
        RowEnd(s);
    }

    private static void DrawSkeletonPane(SettingsViewModel vm, float s)
    {
        SectionHeader("SKELETON LINES", first: true, s);

        RowStart(s);
        RowLabel("Show lines", s, controlY: 7f);
        Crystarium.Switch("##skel-lines", ref vm.ShowSkeletonLines);
        ImGui.SameLine(0f, 12f * s);
        ViewText.Label(ImGui.GetCursorScreenPos() + new Vector2(0f, 4f * s),
            "connect parent and child bones in the overlay", 11f, FontWeight.Regular, TextTertiary, wrap: true);
        RowEnd(s);

        RowStart(s);
        RowLabel("Line thickness", s, controlY: 10f);
        Crystarium.Slider("##line-th", ref vm.BoneLineThickness, 0.5f, 4f, new SliderProps
        {
            Style = new SliderStyle { Width = Sizing.Fixed(220) },
        });
        ImGui.SameLine(0f, 12f * s);
        ViewText.Label(ImGui.GetCursorScreenPos() + new Vector2(0f, 1f * s),
            vm.BoneLineThickness.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " px", 12f, FontWeight.Regular, TextSecondary, mono: true);
        RowEnd(s);

        RowStart(s);
        RowLabel("Line opacity", s, controlY: 10f);
        Crystarium.Slider("##line-op", ref vm.BoneLineOpacity, 0f, 1f, new SliderProps
        {
            Style = new SliderStyle { Width = Sizing.Fixed(220) },
        });
        ImGui.SameLine(0f, 12f * s);
        ViewText.Label(ImGui.GetCursorScreenPos() + new Vector2(0f, 1f * s),
            $"{(int)MathF.Round(vm.BoneLineOpacity * 100)} %", 12f, FontWeight.Regular, TextSecondary, mono: true);
        RowEnd(s);
    }

    private static readonly string[] DockOptions = { "Left", "Right", "Floating", "Hidden" };

    private static void DrawUiPane(SettingsViewModel vm, float s)
    {
        // Layout configurability — user requirement: every panel dockable either
        // side, detachable to its own window, or hidden.
        SectionHeader("LAYOUT", first: true, s);

        RowStart(s);
        RowLabel("Entity sidebar", s, controlY: 2f); // seg 30px
        Crystarium.SegmentedControl("##dock-sidebar", DockOptions, ref vm.SidebarDock);
        RowEnd(s);

        RowStart(s);
        RowLabel("Inspector", s, controlY: 2f);
        Crystarium.SegmentedControl("##dock-inspector", DockOptions, ref vm.InspectorDock);
        RowEnd(s);

        SectionHeader("TREE", first: false, s);

        RowStart(s);
        RowLabel("Tree guide lines", s, controlY: 7f);
        Crystarium.Switch("##tree-guides", ref vm.TreeGuides);
        ImGui.SameLine(0f, 12f * s);
        ViewText.Label(ImGui.GetCursorScreenPos() + new Vector2(0f, 4f * s),
            "connector lines showing hierarchy depth in the scene tree and bone lists", 11f, FontWeight.Regular, TextTertiary, wrap: true);
        RowEnd(s);
    }

    private static void DrawKeybindsPane(SettingsViewModel vm, float s)
    {
        SectionHeader("KEYBINDS", first: true, s);

        for (int i = 0; i < vm.Keybinds.Length; i++)
        {
            RowStart(s);
            RowLabel(vm.Keybinds[i].Action, s, controlY: 6f);
            bool rebinding = vm.RebindingIndex == i;
            KbdChip(rebinding ? "press a key…" : vm.Keybinds[i].Binding, s, listening: rebinding);
            // Rebind buttons in an aligned column regardless of chip width
            ImGui.SetCursorScreenPos(_rowOrigin + new Vector2(280f * s, 1f * s));
            if (Crystarium.Button(rebinding ? "Cancel" : "Rebind",
                id: "kb-" + i, width: 72f))
                vm.RebindingIndex = rebinding ? -1 : i;
            RowEnd(s);
        }

        if (vm.RebindingIndex >= 0)
            CaptureRebind(vm);
    }

    private static void DrawAboutPane(SettingsViewModel vm, float s)
    {
        SectionHeader("ABOUT", first: true, s);

        RowStart(s);
        RowLabel("Poser", s);
        ViewText.Label(_rowOrigin + new Vector2(142f * s, 9f * s), vm.Version, 12f, FontWeight.Regular, TextSecondary, mono: true);
        RowEnd(s);

        RowStart(s);
        RowLabel("Stack", s);
        ViewText.Label(_rowOrigin + new Vector2(142f * s, 9f * s), "Norvrandt · Crystarium · PosingCore", 12f, FontWeight.Regular, TextSecondary);
        RowEnd(s);

        RowStart(s);
        RowLabel("Source", s, controlY: 1f);
        if (Crystarium.Button("Open repository", width: 140f))
            vm.OnOpenRepository?.Invoke();
        RowEnd(s);

        ImGui.Dummy(new Vector2(0f, 6f * s));
        ViewText.Label(ImGui.GetCursorScreenPos(), "Design system transcribed from picto/DisplayFrame. Brio and Ktisis studied as references.",
            11f, FontWeight.Regular, TextTertiary, wrap: true);
    }

    /// <summary>kbd chip: 22px, radius 4, black-20 well, 1px border, 11px mono.</summary>
    private static void KbdChip(string text, float s, bool listening = false)
    {
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        float w = ViewText.Measure(text, 11f, mono: true) + 16f * s;
        var min = pos;
        var max = pos + new Vector2(w, 22f * s);
        dl.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(new Vector4(0f, 0f, 0f, 0.20f))), 4f * s);
        dl.AddRect(min + new Vector2(0.5f, 0.5f), max - new Vector2(0.5f, 0.5f),
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(listening
                ? new Vector4(50 / 255f, 151 / 255f, 255 / 255f, 0.60f)   // primary-60 while listening
                : BorderSecondary)), 4f * s, ImDrawFlags.None, 1f * s);
        ViewText.Label(pos + new Vector2(8f * s, 5f * s), text, 11f, FontWeight.Regular,
            listening ? TextTertiary : TextSecondary, mono: true);
        ImGui.SetCursorScreenPos(new Vector2(max.X, pos.Y));
        ImGui.Dummy(Vector2.Zero);
        ImGui.SameLine(0f, 0f);
    }


    /// <summary>Capture the next key press (with Ctrl/Shift/Alt modifiers) as the new binding.</summary>
    private static void CaptureRebind(SettingsViewModel vm)
    {
        var io = ImGui.GetIO();
        for (var key = ImGuiKey.A; key <= ImGuiKey.F12; key++)
        {
            if (key is ImGuiKey.LeftCtrl or ImGuiKey.RightCtrl or ImGuiKey.LeftShift or ImGuiKey.RightShift
                or ImGuiKey.LeftAlt or ImGuiKey.RightAlt) continue;
            if (!ImGui.IsKeyPressed(key)) continue;

            string name = key.ToString();
            if (name.StartsWith("_")) name = name[1..];
            string binding = (io.KeyCtrl ? "Ctrl+" : "") + (io.KeyShift ? "Shift+" : "") + (io.KeyAlt ? "Alt+" : "") + name;
            vm.Keybinds[vm.RebindingIndex] = (vm.Keybinds[vm.RebindingIndex].Action, binding);
            vm.RebindingIndex = -1;
            return;
        }
        if (ImGui.IsKeyPressed(ImGuiKey.Escape))
            vm.RebindingIndex = -1;
    }

    // ─────────────────────────────────────────────────────────────────────
    // M5 row/label grammar

    /// <summary>.ph — 26px, 12px/600 tertiary; non-first: 10px top margin + top hairline + 8px padding.</summary>
    private static void SectionHeader(string text, bool first, float s)
    {
        var pos = ImGui.GetCursorScreenPos();
        float availX = ImGui.GetContentRegionAvail().X;
        if (!first)
        {
            ImGui.GetWindowDrawList().AddRectFilled(pos + new Vector2(0f, 10f * s), pos + new Vector2(availX, 11f * s),
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(BorderSecondary)));
            pos += new Vector2(0f, 18f * s); // 10px margin + hairline + 8px padding
        }
        ViewText.Label(pos + new Vector2(0f, (26f - 16f) * s / 2f), text, 12f, FontWeight.SemiBold, TextTertiary);
        ImGui.SetCursorScreenPos(pos + new Vector2(0f, 26f * s)); // absolute advance — Element already moved the cursor
    }

    private static Vector2 _rowOrigin;

    /// <summary>.setRow — min-height 34 (wells row taller), label column then control.</summary>
    private static void RowStart(float s, bool tall = false)
    {
        _rowOrigin = ImGui.GetCursorScreenPos();
    }

    private static void RowEnd(float s, bool tall = false)
    {
        float h = (tall ? 56f : 34f) * s;
        ImGui.SetCursorScreenPos(_rowOrigin + new Vector2(0f, h));
    }

    /// <summary>Label cell: 13px/600, min-width 130, gap 12 — control starts at x+142.
    /// <paramref name="controlY"/> = flex-centering offset for this row's control
    /// height within the 34px row ((34 − h) / 2), like M5's align-items center.</summary>
    private static void RowLabel(string text, float s, float controlY = 10f)
    {
        ViewText.Label(_rowOrigin + new Vector2(0f, 8f * s), text, 13f, FontWeight.SemiBold, TextPrimary);
        ImGui.SetCursorScreenPos(_rowOrigin + new Vector2(142f * s, controlY * s));
    }

    /// <summary>M5 .stack — color well + 10px tertiary caption (centered), 3px gap,
    /// 10px between stacks. Stack width = max(well, caption) like the flex column.</summary>
    private static void WellStack(string id, ref Vector4 color, string caption, float s)
    {
        var pos = ImGui.GetCursorScreenPos();
        float capW = ViewText.Measure(caption, 10f);
        float stackW = MathF.Max(28f * s, capW);
        ImGui.SetCursorScreenPos(new Vector2(pos.X + (stackW - 28f * s) / 2f, pos.Y));
        Crystarium.ColorWell(id, ref color);
        ViewText.Label(new Vector2(pos.X + (stackW - capW) / 2f, pos.Y + (28f + 3f) * s), caption, 10f, FontWeight.Regular, TextTertiary);
        ImGui.SetCursorScreenPos(new Vector2(pos.X + stackW + 10f * s, pos.Y));
    }



}
