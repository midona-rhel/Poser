using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.Application.Animation;
using Poser.Application.Scene;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.UI.Controls;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>
/// The Animation tab: a compact live mixer for the selected actor, not a
/// timeline-slot debugger.
///
/// Sections follow the task — transport, stance, layers (with scrub
/// inline), face and lips. Choosing an animation is always the shared
/// <see cref="AnimationPicker"/>, with the CALLER supplying the
/// destination.
///
/// LAYOUT DISCIPLINE. One 30px row grid; every control is vertically
/// centred in its row by its own real height (buttons 24, sliders 14,
/// switches 20, segmented pills 30). Trailing actions are placed from the
/// RIGHT edge at MEASURED widths, so they end flush at the content edge
/// and never overlap or fall short regardless of label length; the value
/// control fills whatever remains. Nothing uses hand-summed trailing
/// constants — that is what produced the earlier overlaps.
/// </summary>
public sealed class AnimationPane
{
    private readonly AnimationSession _animation;
    private readonly AnimationCatalog _catalog;
    private readonly AnimationSceneActions _sceneActions;
    private readonly Game.Animation.FacialPoseCapture _facialCapture;
    private readonly AnimationPicker _picker;
    private readonly SceneSession _scene;

    // View preferences, deliberately not per-actor. Disclosures start
    // collapsed and contribute only their header when closed.
    private bool _openAdvancedSlots;
    private bool _openAdvancedScrub;

    private (ActorId Actor, ScrubControlId Control)? _scrub;
    /// <summary>Last animation picked into each layer. A one-shot ends in
    /// the game and the slot reads 0 again; without this the row collapses
    /// back to "Add layer…" the moment the animation finishes, taking its
    /// controls with it mid-use.</summary>
    private readonly Dictionary<(ActorId, AnimationSlot), ushort> _layerPicks = new();
    // The advanced list's IDENTITY is frozen while a scrub is in flight:
    // pausing the actor can change which Havok controls exist, and a list
    // that gains or loses rows under the pointer moves the slider being
    // dragged. Readings are still merged live so times keep updating.
    private IReadOnlyList<ScrubControlReading>? _scrubFrozenControls;
    private string _status = string.Empty;
    private bool _sceneMenuRequested;

    // One grid for every row in the pane.
    private const float ContentPadding = 12f;
    private const float Row = 30f;
    private const float LabelColumn = 92f;
    private const float Gap = 8f;
    // Per-control vertical centring inside the 30px row.
    private const float ButtonY = 3f;   // 24px compact button
    private const float SliderY = 8f;   // 14px slider
    private const float SwitchY = 5f;   // 20px switch
    private const float TextY = 9f;     // 11-12px text

    /// <summary>The layers a user actually mixes. Parts and Overlay are
    /// engine slots and live under Advanced.</summary>
    private static readonly AnimationSlot[] PrimaryLayers =
    {
        AnimationSlot.UpperBody, AnimationSlot.Facial, AnimationSlot.Additive,
    };
    private static readonly AnimationSlot[] AdvancedLayers =
    {
        AnimationSlot.Parts1, AnimationSlot.Parts2, AnimationSlot.Parts3,
        AnimationSlot.Parts4, AnimationSlot.Overlay,
    };

    private static readonly string[] StanceLabels = { "Idle", "Chair", "Ground", "Sleep" };

    /// <summary>Display name for EVERY readable family, including the ones
    /// the picker does not offer — the pill must tell the truth about a
    /// weapon-drawn or umbrella state instead of claiming "Idle".</summary>
    private static string StanceName(AnimationStance stance) => stance switch
    {
        AnimationStance.Idle => "Idle",
        AnimationStance.WeaponDrawn => "Battle",
        AnimationStance.SitChair => "Chair",
        AnimationStance.SitGround => "Ground",
        AnimationStance.Sleeping => "Sleep",
        AnimationStance.Umbrella => "Umbrella",
        _ => "Accessory",
    };
    private static readonly AnimationStance[] StanceValues =
    {
        AnimationStance.Idle, AnimationStance.SitChair,
        AnimationStance.SitGround, AnimationStance.Sleeping,
    };

    public AnimationPane(
        AnimationSession animation,
        AnimationCatalog catalog,
        AnimationSceneActions sceneActions,
        Game.Animation.FacialPoseCapture facialCapture,
        AnimationPicker picker,
        SceneSession scene)
    {
        _animation = animation;
        _catalog = catalog;
        _sceneActions = sceneActions;
        _facialCapture = facialCapture;
        _picker = picker;
        _scene = scene;
    }

    /// <summary>The actor the tab acts on: the selected actor, or the
    /// owning actor of a selected bone. Selection itself is untouched, so
    /// posing continues while animation runs.</summary>
    private ActorId? TargetActor() => _scene.Selection.Primary switch
    {
        { Kind: SceneEntityKind.Actor, Actor: { } actor } => actor,
        { Kind: SceneEntityKind.Bone, Bone: { } bone } => bone.Skeleton.Actor,
        _ => null,
    };

    private ActorDescriptor? Describe(ActorId id)
    {
        foreach (var actor in _scene.Snapshot.Actors)
            if (actor.Id.Equals(id))
                return actor;
        return null;
    }

    public void Draw(Vector2 origin, Vector2 size)
    {
        float s = ImGuiHelpers.GlobalScale;
        float width = InspectorLayout.ClampContentWidth(size.X, s);

        if (TargetActor() is not { } actor)
        {
            InspectorLayout.EmptyState(origin, s);
            return;
        }

        if (!_animation.IsSupported(actor))
        {
            ViewText.Label(origin + new Vector2(0f, 8f) * s,
                "This actor does not support animation control.", 12f,
                FontWeight.Regular, InspectorLayout.HintColor);
            return;
        }

        // A selection change ends any scrub still bound to the old actor,
        // so its slider values cannot reach the previous gesture.
        if (_scrub is { } active && !active.Actor.Equals(actor))
        {
            EndScrub();
        }

        var reading = _animation.Read(actor) ?? ActorAnimationReading.Empty;
        var owned = _animation.OverridesFor(actor);

        // The shell's content child scrolls and owns the gutter; the page
        // supplies only its own vertical padding.
        ImGui.SetCursorScreenPos(origin + new Vector2(0f, ContentPadding * s));
        DrawPage(actor, reading, owned, width, s);

        if (_picker.Draw() is { } pick)
            Apply(actor, pick);
    }

    private void DrawPage(
        ActorId actor, ActorAnimationReading reading,
        AnimationOverrides owned, float width, float s)
    {
        var origin = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        float y = origin.Y;

        y += DrawTransport(actor, reading, owned, new Vector2(origin.X, y), width, s);

        // Failures render HERE, under the controls that cause most of
        // them, instead of five sections down where they scrolled out of
        // sight.
        if (_status.Length > 0)
        {
            ViewText.Label(new Vector2(origin.X, y + 2f * s), _status, 11f,
                FontWeight.Regular, InspectorLayout.HintColor);
            y += 20f * s;
        }
        y += SectionGap(s);
        y += DrawStance(actor, reading, new Vector2(origin.X, y), width, s);
        y += SectionGap(s);
        y += DrawLayers(actor, reading, owned, dl, new Vector2(origin.X, y), width, s);
        y += SectionGap(s);
        y += DrawFace(actor, reading, new Vector2(origin.X, y), width, s);

        // Register the content extent, including the trailing padding, so
        // scrolling to the bottom leaves the last row clear of the edge.
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(width, (y - origin.Y) + ContentPadding * s));
    }

    private static float SectionGap(float s) => 10f * s;

    /// <summary>Section caption on the shared grid.</summary>
    private static float Caption(Vector2 cursor, string text, float s)
    {
        ViewText.Label(cursor, text, 11f, FontWeight.SemiBold, InspectorLayout.LabelColor);
        return 20f * s;
    }

    /// <summary>Label cell for a row; returns the x where the value starts.</summary>
    private static float LabelCell(Vector2 rowOrigin, string label, float s)
    {
        ViewText.Label(rowOrigin + new Vector2(0f, TextY * s), label, 11f,
            FontWeight.Regular, InspectorLayout.LabelColor);
        return rowOrigin.X + LabelColumn * s;
    }

    private readonly record struct TrailingAction(
        string Label, string Id, string? Tip, bool Disabled, Action Click)
    {
        /// <summary>Reserve the width of the WIDEST alternative label so a
        /// button whose label toggles (Play/Pause) keeps one slot width and
        /// nothing to its left shifts when it toggles.</summary>
        public string? WidthLabel { get; init; }
    }

    /// <summary>
    /// Places buttons right-to-left from the row's right edge at MEASURED
    /// widths with uniform gaps, so trailing actions end flush at the
    /// content edge no matter what their labels measure. Returns the x
    /// where the trailing block begins; the value control fills up to it.
    /// </summary>
    private static float TrailingButtons(
        Vector2 rowOrigin, float width, float s, params TrailingAction[] actions)
    {
        float x = rowOrigin.X + width;
        for (int i = actions.Length - 1; i >= 0; i--)
        {
            var action = actions[i];
            float w = Crystarium.MeasureButton(action.Label, ControlStyle.Workspace).X;
            if (action.WidthLabel is { } alt)
                w = MathF.Max(w, Crystarium.MeasureButton(alt, ControlStyle.Workspace).X);
            x -= w;
            ImGui.SetCursorScreenPos(new Vector2(x, rowOrigin.Y + ButtonY * s));
            if (Crystarium.Button(action.Label,
                    id: action.Id,
                    help: action.Tip,
                    disabled: action.Disabled,
                    style: ControlStyle.Workspace with
                    {
                        Width = UiSize.Fixed(w / s),
                    }) && !action.Disabled)
                action.Click();
            x -= Gap * s;
        }
        return x;
    }

    /// <summary>The value-cell button that fills from the label column to
    /// the trailing block. Returns true when clicked.</summary>
    private static bool ValueButton(
        float valueX, float rowY, float rightEdge, float s,
        string label, string id, string? tip)
    {
        ImGui.SetCursorScreenPos(new Vector2(valueX, rowY + ButtonY * s));
        return Crystarium.Button(label,
            id: id,
            help: tip,
            style: ControlStyle.Workspace with
            {
                Width = UiSize.Fixed(MathF.Max(
                    70f, (rightEdge - valueX) / ImGuiHelpers.GlobalScale)),
            });
    }

    // ── A. Transport ──────────────────────────────────────────────────

    private float DrawTransport(
        ActorId actor, ActorAnimationReading reading,
        AnimationOverrides owned, Vector2 cursor, float width, float s)
    {
        float y = Caption(cursor, Describe(actor)?.Name ?? "ACTOR", s);

        ushort current = reading.BaseTimeline != 0
            ? reading.BaseTimeline
            : reading.TimelineFor(AnimationSlot.Base);
        var row = cursor + new Vector2(0f, y);
        float valueX = LabelCell(row, "Animation", s);

        bool paused = _animation.IsPaused(actor);
        float trailingX = TrailingButtons(row, width, s,
            new TrailingAction(paused ? "Play" : "Pause", "anim-play",
                paused ? "Resume from the current frame" : "Hold the current frame",
                false, () => Report(
                    paused ? _animation.Resume(actor) : _animation.Pause(actor),
                    "Playback"))
            { WidthLabel = "Pause" },
            new TrailingAction("Replay", "anim-replay",
                "Restart the current animation", current == 0,
                () => Report(_animation.Blend(actor, current), "Replay")),
            new TrailingAction("Restore", "anim-restore",
                "Restore this actor's incoming animation state", false,
                () => Report(_animation.ResetActor(actor), "Restore")));

        if (ValueButton(valueX, row.Y, trailingX, s,
                NameFor(current, "Choose…"), "anim-choose-base",
                "Choose the animation this actor plays"))
            // Restricted to full-body timelines: the transport row is the
            // base layer's control, and every other slot has its own row.
            _picker.Open(AnimationPickTarget.Base, AnimationSlot.Base,
                restrictToSlot: AnimationSlot.Base, caption: "Animation");
        y += Row * s;

        // Speed row: slider fills to the trailing 1× reset and the
        // scene-wide menu, whose label says what it is about.
        row = cursor + new Vector2(0f, y);
        valueX = LabelCell(row, "Speed", s);
        trailingX = TrailingButtons(row, width, s,
            new TrailingAction("Reset", "anim-speed-reset",
                "Hand playback speed back to the game", false,
                () => Report(_animation.ClearSpeed(actor), "Speed")),
            new TrailingAction("All actors…", "anim-scene",
                "Freeze, resume, replay or restore every actor in the scene",
                false, () => _sceneMenuRequested = true));

        // Ktisis' number-plus-slider line: the well IS the number (drag,
        // Ctrl fine, double-click to type), the slider is bare with 0 and
        // 1 marked so stop and natural speed are findable in -5..10.
        float speed = owned.OverallSpeed ?? reading.OverallSpeed;
        const float speedWell = 56f;
        if (AppShellView.DragValueCell(ImGui.GetWindowDrawList(),
                new Vector2(valueX, row.Y + 2f * s), speedWell * s,
                "anim-speed-well", ref speed, 0.01f, "0.00", s, out _))
            Report(_animation.SetSpeed(actor, speed), "Speed");
        float speedSliderX = valueX + (speedWell + Gap) * s;
        ImGui.SetCursorScreenPos(new Vector2(speedSliderX, row.Y + SliderY * s));
        Crystarium.Slider(
            "##anim-speed",
            speed,
            -5f,
            10f,
            next =>
            {
                speed = next;
                Report(_animation.SetSpeed(actor, next), "Speed");
            },
            new ControlStyle
                {
                    Width = UiSize.Fixed(MathF.Max(
                        60f, (trailingX - speedSliderX) / s - Gap)),
                },
            marks: new[] { 0f, 1f });
        y += Row * s;

        DrawSceneMenu();
        return y;
    }

    /// <summary>Scene-wide actions as a menu: they are secondary, and four
    /// labelled buttons compete with the actor's own transport.</summary>
    private void DrawSceneMenu()
    {
        if (_sceneMenuRequested)
        {
            _sceneMenuRequested = false;
            Crystarium.FloatingMenu.Open("##anim-scene-menu", ImGui.GetMousePos(), new[]
            {
                new ContextMenuItem("Freeze all", TablerIcon.PlayerPlay),
                new ContextMenuItem("Resume all", TablerIcon.PlayerPlay),
                new ContextMenuItem("Replay all", TablerIcon.Refresh),
                new ContextMenuItem("Restore all", TablerIcon.ArrowBackUp),
            });
        }
        int clicked = Crystarium.FloatingMenu.Draw("##anim-scene-menu");
        switch (clicked)
        {
            case 0: Report(_sceneActions.FreezeAll(), "Freeze all"); break;
            case 1: Report(_sceneActions.ResumeAll(), "Resume all"); break;
            case 2: Report(_sceneActions.ReplayAll(), "Replay all"); break;
            case 3: Report(_sceneActions.StopAll(), "Restore all"); break;
        }
    }

    // ── B. Stance ─────────────────────────────────────────────────────

    private float DrawStance(
        ActorId actor, ActorAnimationReading reading, Vector2 cursor, float width, float s)
    {
        float y = Caption(cursor, "STANCE", s);

        // Ktisis' combo, not a segmented pill: the trigger shows the TRUE
        // family (which may be "Battle" — not in the list), and re-picking
        // the shown entry still fires, which is what makes Idle reachable
        // from a weapon-drawn state.
        bool supportsStance = _animation.SupportsStance;
        int stanceIndex = Array.IndexOf(StanceValues, reading.Stance);
        var row = cursor + new Vector2(0f, y);
        float valueX = LabelCell(row, "Stance", s);
        ImGui.SetCursorScreenPos(new Vector2(valueX, row.Y + 2f * s));
        Crystarium.ActionDropdown(
            "##anim-stance",
            StanceLabels,
            stanceIndex,
            StanceName(reading.Stance),
            picked =>
            {
                int pose = StanceValues[picked] == reading.Stance ? reading.Pose : 0;
                Report(_animation.SetStance(
                    actor, StanceValues[picked], pose), "Stance");
            },
            new ControlStyle { Width = UiSize.Fixed(160f) },
            disabled: !supportsStance,
            help: supportsStance
                ? "Pose family — picking one returns the actor to it"
                : "Stance changes are unavailable: a required game function was not found");

        // The pose cycler lives ON the stance row — number, then the same
        // − / + icon buttons the rest of the product uses. Non-selectable
        // families (Battle, Umbrella…) step through Idle, whose native
        // path already routes to the battle variants when the weapon is
        // drawn.
        var poseFamily = stanceIndex >= 0 ? reading.Stance : AnimationStance.Idle;
        // The cycler drives the game's stance idles. While a picked
        // animation plays (or loops) the buttons would fight it, and a
        // one-pose family has nothing to cycle — enabled buttons that do
        // nothing are worse than none.
        var stanceOwned = _animation.OverridesFor(actor);
        bool poseDisabled = !supportsStance ||
            AnimationTimelines.PoseCount(poseFamily, reading.WeaponDrawn) <= 1 ||
            stanceOwned.LoopedSlots.ContainsKey(AnimationSlot.Base) ||
            (stanceOwned.BaseTimeline is { } basePick &&
                reading.TimelineFor(AnimationSlot.Base) == basePick);
        float poseX = valueX + (160f + 16f) * s;
        ViewText.Label(new Vector2(poseX, row.Y + TextY * s),
            reading.Pose.ToString(), 12f, FontWeight.Medium,
            InspectorLayout.ValueColor, mono: true);
        ImGui.SetCursorScreenPos(new Vector2(poseX + 14f * s, row.Y + ButtonY * s));
        if (Crystarium.IconButton(TablerIcon.Minus,
                id: "anim-pose-prev",
                help: "Previous pose (wraps)",
                disabled: poseDisabled,
                style: ControlStyle.Workspace with
                {
                    Size = UiSize.Fixed(24f),
                }))
            Report(
                _animation.SetStance(actor, poseFamily, reading.Pose - 1),
                "Pose");
        ImGui.SetCursorScreenPos(new Vector2(poseX + (14f + 24f + 4f) * s, row.Y + ButtonY * s));
        if (Crystarium.IconButton(TablerIcon.Plus,
                id: "anim-pose-next",
                help: "Next pose (wraps)",
                disabled: poseDisabled,
                style: ControlStyle.Workspace with
                {
                    Size = UiSize.Fixed(24f),
                }))
            Report(
                _animation.SetStance(actor, poseFamily, reading.Pose + 1),
                "Pose");
        y += Row * s;

        row = cursor + new Vector2(0f, y);
        valueX = LabelCell(row, "Weapon", s);
        ImGui.SetCursorScreenPos(new Vector2(valueX, row.Y + SwitchY * s));
        Crystarium.Switch(
            "##anim-weapon",
            reading.WeaponDrawn,
            next => Report(_animation.SetWeaponDrawn(actor, next), "Weapon"));

        ViewText.Label(new Vector2(valueX + 56f * s, row.Y + TextY * s), "Lock position",
            11f, FontWeight.Regular, InspectorLayout.LabelColor);
        bool locked = _animation.OverridesFor(actor).PositionLock;
        ImGui.SetCursorScreenPos(new Vector2(valueX + 148f * s, row.Y + SwitchY * s));
        Crystarium.Switch(
            "##anim-poslock",
            locked,
            next => Report(_animation.SetPositionLock(actor, next), "Position lock"));
        return y + Row * s;
    }

    // ── C. Layers ─────────────────────────────────────────────────────

    private float DrawLayers(
        ActorId actor, ActorAnimationReading reading, AnimationOverrides owned,
        ImDrawListPtr dl, Vector2 cursor, float width, float s)
    {
        float y = Caption(cursor, "LAYERS", s);

        // The Full body row IS what the references call blending: a pick
        // here is a one-shot played through the sequencer over the base
        // loop. There is no separate "blend" — every layer pick is the
        // same operation with a visible target.
        y += DrawLayerRow(actor, reading, owned, cursor, width, s, y,
            AnimationSlot.Base, "Full body", alwaysShow: true);

        foreach (var slot in PrimaryLayers)
            y += DrawLayerRow(actor, reading, owned, cursor, width, s, y,
                slot, AnimationSlots.DisplayName(slot), alwaysShow: false);

        y += InspectorLayout.Section(dl, cursor + new Vector2(0f, y), width,
            "anim", "ADVANCED SLOTS", ref _openAdvancedSlots, s, topBorder: false);
        if (_openAdvancedSlots)
            foreach (var slot in AdvancedLayers)
                y += DrawLayerRow(actor, reading, owned, cursor, width, s, y,
                    slot, AnimationSlots.DisplayName(slot), alwaysShow: true);

        // Raw Havok controls, for the slots the friendly rows do not
        // cover. The friendly scrub lives inline in the layer rows above.
        y += InspectorLayout.Section(dl, cursor + new Vector2(0f, y), width,
            "anim", "ADVANCED CONTROLS", ref _openAdvancedScrub, s, topBorder: false);
        if (_openAdvancedScrub)
        {
            var controls = AdvancedControls(reading);
            foreach (var control in controls)
                y += DrawScrubRow(actor, reading, cursor, width, s, y,
                    control.Id.ToString(), control);
            if (controls.Count == 0)
            {
                ViewText.Label(cursor + new Vector2(0f, y + 4f * s),
                    "No animation controls.", 11f,
                    FontWeight.Regular, InspectorLayout.HintColor);
                y += Row * s;
            }
        }
        return y;
    }

    /// <summary>
    /// One layer row: label, the animation name opening the picker for
    /// THAT destination, then pause, speed and reset placed from the right
    /// at measured widths. An inactive optional layer shows one "Add
    /// layer" invitation instead of controls for an empty engine slot.
    /// </summary>
    private float DrawLayerRow(
        ActorId actor, ActorAnimationReading reading, AnimationOverrides owned,
        Vector2 cursor, float width, float s, float y,
        AnimationSlot slot, string label, bool alwaysShow)
    {
        // The live timeline while one plays, else the last pick made here:
        // the game reads 0 again the moment a one-shot ends, and the row
        // must not forget what it was playing.
        ushort live = reading.TimelineFor(slot);
        ushort timeline = live != 0
            ? live
            : _layerPicks.TryGetValue((actor, slot), out var remembered) ? remembered : (ushort)0;
        bool active = timeline != 0;
        var row = cursor + new Vector2(0f, y);
        float valueX = LabelCell(row, label, s);

        if (!active && !alwaysShow && !owned.SlotSpeeds.ContainsKey(slot))
        {
            var capturedEmpty = slot;
            if (ValueButton(valueX, row.Y, row.X + width, s,
                    "Add layer…", $"anim-add-{(int)slot}",
                    $"Play an animation on the {label.ToLowerInvariant()} layer"))
                _picker.Open(AnimationPickTarget.Slot, capturedEmpty, capturedEmpty,
                    $"{label} layer");
            return Row * s;
        }

        var captured = slot;
        bool slotPaused = owned.SlotSpeeds.TryGetValue(slot, out var ownedSpeed)
            && ownedSpeed == 0f;

        // Right-to-left: Reset, speed slider, Pause — then the name fills.
        float x = row.X + width;
        float resetWidth = Crystarium.MeasureButton("Reset", ControlStyle.Workspace).X;
        x -= resetWidth;
        ImGui.SetCursorScreenPos(new Vector2(x, row.Y + ButtonY * s));
        bool resetDisabled = !owned.SlotSpeeds.ContainsKey(slot);
        if (Crystarium.Button("Reset",
                id: $"anim-layer-reset-{(int)slot}",
                help: "Hand this layer's speed back to the game",
                disabled: resetDisabled,
                style: ControlStyle.Workspace with
                {
                    Width = UiSize.Fixed(resetWidth / s),
                }) && !resetDisabled)
        {
            // A layer reset hands the layer's SPEED back; the timeline is
            // not restorable -- the references never write one, so neither
            // can we. What plays next is the game's own business.
            Report(_animation.ClearSlotSpeed(actor, captured), "Layer speed");
        }

        x -= (Gap + 130f) * s;
        float slotSpeed = owned.SlotSpeeds.TryGetValue(slot, out var over)
            ? over
            : reading.SpeedFor(slot);
        if (AppShellView.DragValueCell(ImGui.GetWindowDrawList(),
                new Vector2(x, row.Y + 2f * s), 48f * s,
                $"anim-layer-speed-well-{(int)slot}", ref slotSpeed,
                0.005f, "0.00", s, out _))
            Report(_animation.SetSlotSpeed(actor, captured, slotSpeed), "Layer speed");
        ImGui.SetCursorScreenPos(new Vector2(x + 52f * s, row.Y + SliderY * s));
        Crystarium.Slider(
            $"##anim-layer-speed-{(int)slot}",
            slotSpeed,
            0f,
            2f,
            next =>
            {
                slotSpeed = next;
                Report(_animation.SetSlotSpeed(actor, captured, next), "Layer speed");
            },
            new ControlStyle
                {
                    Width = UiSize.Fixed(78f),
                },
            marks: new[] { 1f });

        float pauseWidth = MathF.Max(MathF.Max(
            Crystarium.MeasureButton("Pause", ControlStyle.Workspace).X,
            Crystarium.MeasureButton("Play", ControlStyle.Workspace).X),
            Crystarium.MeasureButton("Replay", ControlStyle.Workspace).X);
        x -= (Gap * s) + pauseWidth;
        ImGui.SetCursorScreenPos(new Vector2(x, row.Y + ButtonY * s));
        if (live == 0)
        {
            // The one-shot ended, so there is nothing to pause — the slot
            // holds the pick instead, ready to play again.
            if (Crystarium.Button("Replay",
                    id: $"anim-layer-replay-{(int)slot}",
                    help: "Play this animation again",
                    style: ControlStyle.Workspace with
                    {
                        Width = UiSize.Fixed(pauseWidth / s),
                    }))
                Report(_animation.Blend(actor, timeline), label);
        }
        else if (Crystarium.Button(slotPaused ? "Play" : "Pause",
                id: $"anim-layer-pause-{(int)slot}",
                help: "Hold or release only this layer",
                style: ControlStyle.Workspace with
                {
                    Width = UiSize.Fixed(pauseWidth / s),
                }))
            Report(
                slotPaused
                    ? _animation.ClearSlotSpeed(actor, captured)
                    : _animation.SetSlotSpeed(actor, captured, 0f),
                "Layer playback");

        if (ValueButton(valueX, row.Y, x - Gap * s, s,
                NameFor(timeline, "Choose…"), $"anim-layer-{(int)slot}",
                "Choose an animation for this layer"))
            _picker.Open(AnimationPickTarget.Slot, captured, captured, $"{label} layer");
        float used = Row * s;

        // The scrub belongs to the layer it scrubs (Ktisis puts it inline
        // in the slot row); only the two body layers have a reliably
        // friendly Havok control. The row is ALWAYS there — a slider that
        // vanishes whenever nothing plays takes the loop switch with it.
        if (slot is AnimationSlot.Base or AnimationSlot.UpperBody)
        {
            var control = _animation.FindSlotControl(actor, slot)
                ?? new ScrubControlReading(
                    new ScrubControlId(-1, (int)slot), 0f, 0f, 0f);
            used += DrawScrubRow(actor, reading, cursor, width, s, y + used,
                "Time", control, slot, timeline);
        }
        return used;
    }

    /// <summary>
    /// The advanced list, with its identity frozen during a drag. Pausing
    /// the actor changes which Havok controls the game keeps, so a live
    /// list gains rows the moment a drag begins — a new slider appearing
    /// under the pointer. Rows are frozen to the set captured at Begin;
    /// their READINGS are merged from the live enumeration so times keep
    /// moving.
    /// </summary>
    private IReadOnlyList<ScrubControlReading> AdvancedControls(
        ActorAnimationReading reading)
    {
        if (_scrub == null || _scrubFrozenControls == null)
            return reading.Controls;
        var merged = new List<ScrubControlReading>(_scrubFrozenControls.Count);
        foreach (var frozen in _scrubFrozenControls)
        {
            ScrubControlReading current = frozen;
            foreach (var live in reading.Controls)
                if (live.Id.Equals(frozen.Id))
                    current = live;
            merged.Add(current);
        }
        return merged;
    }

    private float DrawScrubRow(
        ActorId actor, ActorAnimationReading reading,
        Vector2 cursor, float width, float s, float y,
        string label, ScrubControlReading control,
        AnimationSlot? loopSlot = null, ushort loopTimeline = 0)
    {
        var row = cursor + new Vector2(0f, y);
        float valueX = LabelCell(row, label, s);
        float time = control.Time;
        bool scrubbable = control.Duration > 0f;
        float duration = MathF.Max(control.Duration, 0.0001f);

        // Ktisis' number-plus-slider line: the well shows and edits the
        // time (drag or double-click type, clamped to the duration) and
        // commits through the SAME scrub gesture as the slider.
        const float wellW = 48f;
        bool wellReleased = false;
        float wellTime = time;
        bool wellChanged = AppShellView.DragValueCell(ImGui.GetWindowDrawList(),
            new Vector2(valueX, row.Y + 2f * s), wellW * s,
            $"anim-scrub-well-{control.Id.Partial}-{control.Id.Control}",
            ref wellTime, 0.01f, "0.00", s, out wellReleased,
            disabled: !scrubbable);
        float sliderX = valueX + (wellW + Gap) * s;

        // Fixed readout slot showing the duration (the well owns the
        // current time), right-aligned by measure so the slider's width
        // does not jitter with the digit count.
        const float readoutSlot = 48f;
        float loopWidth = loopSlot != null ? 44f : 0f;
        string readout = $"/{control.Duration:0.00}";
        float readoutWidth = ViewText.Measure(readout, 11f, mono: true);
        ViewText.Label(
            new Vector2(row.X + width - readoutWidth, row.Y + TextY * s),
            readout, 11f, FontWeight.Regular, InspectorLayout.HintColor, mono: true);

        // The layer's loop switch lives with its time, between slider and
        // readout; raw Havok rows have no slot to loop.
        if (loopSlot is { } armedSlot)
        {
            bool looped = _animation.OverridesFor(actor)
                .LoopedSlots.ContainsKey(armedSlot);
            ImGui.SetCursorScreenPos(new Vector2(
                row.X + width - (readoutSlot + loopWidth) * s,
                row.Y + SwitchY * s));
            Crystarium.Switch(
                $"##anim-loop-{(int)armedSlot}",
                looped,
                next => Report(
                    _animation.SetSlotLoop(actor, armedSlot, loopTimeline, next),
                    "Loop"));
            if (ImGui.IsItemHovered())
                Crystarium.HoverHelp.Explain($"anim-loop-help-{(int)armedSlot}",
                    ImGui.GetItemRectMin(), ImGui.GetItemRectMax(),
                    "Play this layer's animation again when it ends");
        }

        ImGui.SetCursorScreenPos(new Vector2(sliderX, row.Y + SliderY * s));
        bool changed = false;
        Crystarium.Slider(
            $"##anim-scrub-{control.Id.Partial}-{control.Id.Control}",
            time,
            0f,
            duration,
            next =>
            {
                time = next;
                changed = true;
            },
            new ControlStyle
            {
                Width = UiSize.Fixed(MathF.Max(
                        60f,
                        width / s - LabelColumn - wellW - Gap - readoutSlot -
                        loopWidth - Gap)),
            },
            disabled: !scrubbable);
        if (wellChanged && scrubbable)
        {
            time = Math.Clamp(wellTime, 0f, duration);
            changed = true;
        }
        if (!scrubbable)
            changed = false;

        // The drag owns the freeze for its length and releases the actor
        // paused on the frame it landed on.
        if (changed)
        {
            if (_scrub is not { } held || !held.Actor.Equals(actor) ||
                !held.Control.Equals(control.Id))
            {
                Report(_animation.BeginScrub(actor, control.Id), "Scrub");
                _scrub = (actor, control.Id);
                _scrubFrozenControls = reading.Controls;
            }
            Report(_animation.UpdateScrub(time), "Scrub");
        }
        if (_scrub is { } current && current.Actor.Equals(actor) &&
            current.Control.Equals(control.Id) &&
            (ImGui.IsItemDeactivated() || wellReleased))
        {
            EndScrub();
        }
        return Row * s;
    }

    private void EndScrub()
    {
        _animation.EndScrub();
        _scrub = null;
        _scrubFrozenControls = null;
    }

    // ── E. Face and lips ──────────────────────────────────────────────

    private float DrawFace(
        ActorId actor, ActorAnimationReading reading, Vector2 cursor, float width, float s)
    {
        float y = Caption(cursor, "FACE & LIPS", s);

        var row = cursor + new Vector2(0f, y);
        float valueX = LabelCell(row, "Expression", s);
        // The truthful current expression is the HELD one; the live slot
        // value is transient while a one-shot plays.
        ushort held = _animation.HeldExpressionFor(actor) ?? 0;
        ushort facial = held != 0 ? held : reading.TimelineFor(AnimationSlot.Facial);
        float trailingX = TrailingButtons(row, width, s,
            new TrailingAction("Preview", "anim-expression-preview",
                "Replay the held expression from its start", facial == 0,
                () => Report(
                    held != 0
                        ? _animation.HoldExpression(actor, held)
                        : _animation.Blend(actor, facial),
                    "Expression")),
            new TrailingAction("Release", "anim-expression-release",
                "Let the face go back to the base animation", held == 0,
                () => Report(_animation.ReleaseExpression(actor), "Expression")),
            new TrailingAction("Apply to face", "anim-apply-face",
                "Keep this face after the preview stops, as one undoable pose edit",
                _facialCapture.IsPending,
                () =>
                {
                    var descriptor = Describe(actor);
                    _status = descriptor == null
                        ? "Apply to face: actor is no longer in the scene."
                        : _facialCapture.Begin(actor, descriptor) is { Success: false } failed
                            ? $"Apply to face: {failed.Detail}"
                            : string.Empty;
                }));
        if (ValueButton(valueX, row.Y, trailingX, s,
                NameFor(facial, "Choose expression…"), "anim-expression",
                "Hold an expression on the face while the body animates"))
            _picker.Open(AnimationPickTarget.Expression, AnimationSlot.Facial,
                AnimationSlot.Facial, "Expression", AnimationKind.Expression);
        y += Row * s;

        // Lips: a known enumeration of speech timelines, not a catalog
        // query — the sheet does not classify them by slot.
        row = cursor + new Vector2(0f, y);
        valueX = LabelCell(row, "Lips", s);
        trailingX = TrailingButtons(row, width, s,
            new TrailingAction("None", "anim-lips-clear",
                "Restore the lip animation this actor arrived with",
                reading.LipsOverride == 0,
                () => Report(_animation.SetLips(actor, 0), "Lips")));
        if (ValueButton(valueX, row.Y, trailingX, s,
                NameFor(reading.LipsOverride, "Choose speech…"), "anim-lips",
                "Choose a speech animation"))
            _picker.Open(AnimationPickTarget.Lips, AnimationSlot.Lips, AnimationSlot.Lips,
                "Lips", entries: LipsEntries());
        return y + Row * s;
    }

    /// <summary>The valid speech timelines, enumerated from the known
    /// range rather than searched for.</summary>
    private IReadOnlyList<TimelineEntry> LipsEntries()
    {
        var entries = new List<TimelineEntry>();
        for (ushort id = AnimationTimelines.FirstLips;
             id <= AnimationTimelines.LastLips; id++)
        {
            entries.Add(_catalog.Find(id) ?? new TimelineEntry(
                id, $"Speech {id - AnimationTimelines.FirstLips + 1}",
                AnimationKind.RawTimeline, AnimationSlot.Lips));
        }
        return entries;
    }

    // ── Shared ────────────────────────────────────────────────────────

    private string NameFor(ushort timeline, string empty) =>
        timeline == 0
            ? empty
            : _catalog.Find(timeline) is { } entry
                ? entry.Name
                : $"Timeline {timeline}";

    /// <summary>Routes a pick to the destination the caller armed. The
    /// picker never decides where an animation goes.</summary>
    private void Apply(ActorId actor, AnimationPick pick)
    {
        var timeline = (ushort)pick.Entry.TimelineId;
        switch (pick.Target)
        {
            case AnimationPickTarget.Base:
            {
                // Plays through the sequencer like everything else (no
                // latch). Retention and loop arming happen only AFTER a
                // successful play, keyed by the timeline's ACTUAL slot.
                var played = _animation.PlayEntry(actor, pick.Entry,
                    asBase: true, playFromStart: true);
                if (!played.Success)
                {
                    Report(played, pick.Entry.Name);
                    break;
                }
                _layerPicks[(actor, pick.Entry.Slot)] = timeline;
                Report(ArmLoop(actor, pick.Entry.Slot, timeline, played),
                    pick.Entry.Name);
                break;
            }
            case AnimationPickTarget.Slot:
            {
                // The generic play: the timeline's own slot tag routes it
                // onto its layer -- the references never write a slot.
                var played = _animation.Blend(actor, timeline);
                if (!played.Success)
                {
                    Report(played, AnimationSlots.DisplayName(pick.Slot));
                    break;
                }
                _layerPicks[(actor, pick.Entry.Slot)] = timeline;
                Report(ArmLoop(actor, pick.Entry.Slot, timeline, played),
                    AnimationSlots.DisplayName(pick.Slot));
                break;
            }
            case AnimationPickTarget.Expression:
                // Expressions HOLD: play onto the face, pin the layer.
                Report(_animation.HoldExpression(actor, timeline), "Expression");
                break;
            case AnimationPickTarget.Lips:
                Report(_animation.SetLips(actor, timeline), "Lips");
                break;
        }
    }

    /// <summary>
    /// Arms looping for a successful pick, but ONLY on the slots that
    /// carry a loop control (the body layers' Time rows): the session is
    /// the single owner of loop state and the switch is its one visible
    /// control, so no slot is ever armed invisibly. A failed arm is
    /// returned instead of being swallowed behind the successful play.
    /// </summary>
    private AnimationResult ArmLoop(
        ActorId actor, AnimationSlot slot, ushort timeline, AnimationResult played)
    {
        if (slot is not (AnimationSlot.Base or AnimationSlot.UpperBody))
            return played;
        var armed = _animation.SetSlotLoop(actor, slot, timeline, true);
        return armed.Success ? played : armed;
    }

    private void Report(AnimationResult result, string what) =>
        _status = result.Success ? string.Empty : $"{what}: {result.Detail}";

    private void Report(AnimationSceneActions.SceneActionReport report, string verb) =>
        _status = report.Success && report.Skipped.Count == 0
            ? string.Empty
            : report.Summary(verb);
}
