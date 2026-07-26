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
/// It is organised by what the user is doing — transport, stance, the
/// layers that are actually active, scrubbing, face and lips — rather than
/// by the engine's slot array. Empty engine slots are not the interface:
/// Parts and Overlay live behind one collapsed Advanced disclosure, which
/// is the same presentation split the native reference makes between
/// ordinary controls and raw slots.
///
/// Choosing an animation is always the shared <see cref="AnimationPicker"/>,
/// opened from whichever row wants it, with the CALLER supplying the
/// destination. Searching a numeric id in that picker is how a raw id is
/// played; the page carries no developer id field.
///
/// Every control's width comes from its own style — Crystarium controls
/// size themselves and ignore <c>ImGui.SetNextItemWidth</c>. Widths are
/// declared UNSCALED. One alignment grid throughout: label, flexible
/// value, trailing actions.
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
    private string _status = string.Empty;

    // One grid for every row in the pane.
    private const float Row = 26f;
    private const float RowGap = 4f;
    private const float LabelColumn = 92f;
    private const float ActionWidth = 60f;

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
        // The shell has already excluded its scrollbar gutter from this
        // width, so nothing further is reserved here.
        float width = InspectorLayout.ClampContentWidth(size.X, s);

        if (TargetActor() is not { } actor)
        {
            ViewText.Label(origin + new Vector2(0f, 8f) * s,
                "Select an actor to mix its animation.", 12f,
                FontWeight.Regular, InspectorLayout.HintColor);
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
            _animation.EndScrub();
            _scrub = null;
        }

        var reading = _animation.Read(actor) ?? ActorAnimationReading.Empty;
        var owned = _animation.OverridesFor(actor);
        var selection = _animation.SelectionFor(actor);

        // No scroll child of its own: the shell's content child scrolls,
        // and it is the thing that reserves the gutter. Opening a child
        // inside the already-inset content would put this page's scrollbar
        // over its own trailing actions.
        ImGui.SetCursorScreenPos(origin);
        DrawPage(actor, reading, owned, width, s);

        // The picker is a popup and so is unaffected by the shell's scroll.
        if (_picker.Draw() is { } pick)
            Apply(actor, selection, pick);
    }

    private void DrawPage(
        ActorId actor, ActorAnimationReading reading,
        AnimationOverrides owned, float width, float s)
    {
        var origin = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        float y = origin.Y;

        y += DrawTransport(actor, reading, owned, new Vector2(origin.X, y), width, s);
        y += SectionGap(s);
        y += DrawStance(actor, reading, new Vector2(origin.X, y), width, s);
        y += SectionGap(s);
        y += DrawLayers(actor, reading, owned, dl, new Vector2(origin.X, y), width, s);
        y += SectionGap(s);
        y += DrawScrub(actor, reading, dl, new Vector2(origin.X, y), width, s);
        y += SectionGap(s);
        y += DrawFace(actor, reading, new Vector2(origin.X, y), width, s);

        if (_status.Length > 0)
        {
            ViewText.Label(new Vector2(origin.X, y + 6f * s), _status, 11f,
                FontWeight.Regular, InspectorLayout.HintColor);
            y += (Row + RowGap) * s;
        }

        // Register the content extent so the page can scroll to it.
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(width, y - origin.Y));
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
        ViewText.Label(rowOrigin + new Vector2(0f, 6f * s), label, 11f,
            FontWeight.Regular, InspectorLayout.LabelColor);
        return rowOrigin.X + LabelColumn * s;
    }

    // ── A. Transport ──────────────────────────────────────────────────

    private float DrawTransport(
        ActorId actor, ActorAnimationReading reading,
        AnimationOverrides owned, Vector2 cursor, float width, float s)
    {
        float y = Caption(cursor, Describe(actor)?.Name ?? "ACTOR", s);

        // Current animation, opening the picker; then the transport
        // actions, trailing-aligned.
        ushort current = reading.BaseTimeline != 0
            ? reading.BaseTimeline
            : reading.TimelineFor(AnimationSlot.Base);
        var row = cursor + new Vector2(0f, y);
        float valueX = LabelCell(row, "Animation", s);

        float actionsWidth = (ActionWidth * 3f + 12f) * s;
        ImGui.SetCursorScreenPos(new Vector2(valueX, row.Y));
        if (Crystarium.Button(NameFor(current, "Choose…"), new ButtonProps
            {
                Id = "anim-choose-base",
                Classes = Cls.Compact,
                Tooltip = "Choose the animation this actor plays",
                Style = new ButtonStyle
                {
                    Width = Sizing.Fixed(
                        MathF.Max(80f, (width - actionsWidth) / s - LabelColumn - 8f)),
                },
            }))
            _picker.Open(AnimationPickTarget.Base, AnimationSlot.Base,
                restrictToSlot: null, caption: "Animation");

        float ax = cursor.X + width - actionsWidth;
        bool paused = _animation.IsPaused(actor);
        ImGui.SetCursorScreenPos(new Vector2(ax, row.Y));
        if (Crystarium.Button(paused ? "Play" : "Pause", new ButtonProps
            {
                Id = "anim-play",
                Classes = Cls.Compact,
                Tooltip = paused ? "Resume from the current frame" : "Hold the current frame",
                Style = new ButtonStyle { Width = Sizing.Fixed(ActionWidth) },
            }))
            Report(paused ? _animation.Resume(actor) : _animation.Pause(actor), "Playback");

        ImGui.SameLine(0f, 6f * s);
        if (Crystarium.Button("Replay", new ButtonProps
            {
                Id = "anim-replay",
                Classes = Cls.Compact,
                Disabled = current == 0,
                Tooltip = "Restart the current animation",
                Style = new ButtonStyle { Width = Sizing.Fixed(ActionWidth) },
            }) && current != 0)
            Report(_animation.Blend(actor, current), "Replay");

        ImGui.SameLine(0f, 6f * s);
        if (Crystarium.Button("Restore", new ButtonProps
            {
                Id = "anim-restore",
                Classes = Cls.Compact,
                Tooltip = "Restore this actor's incoming animation state",
                Style = new ButtonStyle { Width = Sizing.Fixed(ActionWidth) },
            }))
            Report(_animation.ResetActor(actor), "Restore");
        y += (Row + RowGap) * s;

        // Speed, with the scene actions trailing right.
        row = cursor + new Vector2(0f, y);
        valueX = LabelCell(row, "Speed", s);
        float sceneWidth = 86f;
        ImGui.SetCursorScreenPos(new Vector2(valueX, row.Y + 6f * s));
        float speed = owned.OverallSpeed ?? reading.OverallSpeed;
        if (Crystarium.Slider("##anim-speed", ref speed, -5f, 10f, new SliderProps
            {
                Style = new SliderStyle
                {
                    Width = Sizing.Fixed(MathF.Max(
                        80f, (width / s) - LabelColumn - sceneWidth - 60f)),
                },
            }))
            Report(_animation.SetSpeed(actor, speed), "Speed");

        ImGui.SetCursorScreenPos(
            new Vector2(cursor.X + width - (sceneWidth + 48f) * s, row.Y));
        if (Crystarium.Button("1×", new ButtonProps
            {
                Id = "anim-speed-reset",
                Classes = Cls.Compact,
                Tooltip = "Hand playback speed back to the game",
                Style = new ButtonStyle { Width = Sizing.Fixed(42f) },
            }))
            Report(_animation.ClearSpeed(actor), "Speed");

        ImGui.SetCursorScreenPos(new Vector2(cursor.X + width - sceneWidth * s, row.Y));
        if (Crystarium.Button("Scene ⋯", new ButtonProps
            {
                Id = "anim-scene",
                Classes = Cls.Compact,
                Tooltip = "Freeze, resume, replay or restore every actor",
                Style = new ButtonStyle { Width = Sizing.Fixed(sceneWidth) },
            }))
            _sceneMenuRequested = true;
        y += (Row + RowGap) * s;

        DrawSceneMenu();
        return y;
    }

    private bool _sceneMenuRequested;

    /// <summary>Scene-wide actions as a menu rather than a button strip:
    /// they are secondary, and a strip of four competes with the actor's
    /// own transport for the same row.</summary>
    private void DrawSceneMenu()
    {
        if (_sceneMenuRequested)
        {
            ImGui.OpenPopup("##anim-scene-menu");
            _sceneMenuRequested = false;
        }
        int clicked = Crystarium.ContextMenu("##anim-scene-menu", new[]
        {
            new ContextMenuItem("Freeze all", TablerIcon.PlayerPlay),
            new ContextMenuItem("Resume all", TablerIcon.PlayerPlay),
            new ContextMenuItem("Replay all", TablerIcon.Refresh),
            new ContextMenuItem("Restore all", TablerIcon.ArrowBackUp),
        });
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

        int stanceIndex = Array.IndexOf(StanceValues, reading.Stance);
        if (stanceIndex < 0)
            stanceIndex = 0;
        var row = cursor + new Vector2(0f, y);
        float valueX = LabelCell(row, "Stance", s);
        ImGui.SetCursorScreenPos(new Vector2(valueX, row.Y + 1f * s));
        if (Crystarium.SegmentedControl("##anim-stance", StanceLabels, ref stanceIndex,
                MathF.Min(240f, width / s - LabelColumn)))
            Report(_animation.SetStance(actor, StanceValues[stanceIndex], 0), "Stance");
        y += (Row + RowGap) * s;

        // Pose stepper: previous / number / next on the shared grid, with
        // both directions wrapping against the game's own pose count.
        row = cursor + new Vector2(0f, y);
        valueX = LabelCell(row, "Pose", s);
        ImGui.SetCursorScreenPos(new Vector2(valueX, row.Y));
        if (Crystarium.IconButton(TablerIcon.ChevronRight, new ButtonProps
            {
                Id = "anim-pose-prev",
                Classes = Cls.Compact,
                Tooltip = "Previous pose (wraps)",
                FlipX = true,
                Style = new ButtonStyle { Width = Sizing.Fixed(Row), Height = Sizing.Fixed(Row) },
            }))
            Report(
                _animation.SetStance(actor, StanceValues[stanceIndex], reading.Pose - 1),
                "Pose");
        ViewText.Label(new Vector2(valueX + (Row + 10f) * s, row.Y + 6f * s),
            reading.Pose.ToString(), 12f, FontWeight.Medium,
            InspectorLayout.ValueColor, mono: true);
        ImGui.SetCursorScreenPos(new Vector2(valueX + (Row + 34f) * s, row.Y));
        if (Crystarium.IconButton(TablerIcon.ChevronRight, new ButtonProps
            {
                Id = "anim-pose-next",
                Classes = Cls.Compact,
                Tooltip = "Next pose (wraps)",
                Style = new ButtonStyle { Width = Sizing.Fixed(Row), Height = Sizing.Fixed(Row) },
            }))
            Report(
                _animation.SetStance(actor, StanceValues[stanceIndex], reading.Pose + 1),
                "Pose");
        y += (Row + RowGap) * s;

        row = cursor + new Vector2(0f, y);
        valueX = LabelCell(row, "Weapon", s);
        bool drawn = reading.WeaponDrawn;
        ImGui.SetCursorScreenPos(new Vector2(valueX, row.Y + 3f * s));
        if (Crystarium.Switch("##anim-weapon", ref drawn))
            Report(_animation.SetWeaponDrawn(actor, drawn), "Weapon");

        ViewText.Label(new Vector2(valueX + 56f * s, row.Y + 6f * s), "Lock position", 11f,
            FontWeight.Regular, InspectorLayout.LabelColor);
        bool locked = _animation.OverridesFor(actor).PositionLock;
        ImGui.SetCursorScreenPos(new Vector2(valueX + 148f * s, row.Y + 3f * s));
        if (Crystarium.Switch("##anim-poslock", ref locked))
            Report(_animation.SetPositionLock(actor, locked), "Position lock");
        return y + (Row + RowGap) * s;
    }

    // ── C. Active layers ──────────────────────────────────────────────

    private float DrawLayers(
        ActorId actor, ActorAnimationReading reading, AnimationOverrides owned,
        ImDrawListPtr dl, Vector2 cursor, float width, float s)
    {
        float y = Caption(cursor, "LAYERS", s);

        y += DrawLayerRow(actor, reading, owned, cursor, width, s, y,
            AnimationSlot.Base, "Base", alwaysShow: true);

        // Blend has no slot of its own — it is whatever the sequencer is
        // currently blending — so its row offers the action rather than a
        // slot's state.
        var row = cursor + new Vector2(0f, y);
        float valueX = LabelCell(row, "Blend", s);
        ImGui.SetCursorScreenPos(new Vector2(valueX, row.Y));
        if (Crystarium.Button("Add blend…", new ButtonProps
            {
                Id = "anim-add-blend",
                Classes = Cls.Compact,
                Tooltip = "Blend an animation through the game's sequencer",
                Style = new ButtonStyle
                {
                    Width = Sizing.Fixed(MathF.Max(80f, width / s - LabelColumn - 8f)),
                },
            }))
            _picker.Open(AnimationPickTarget.Blend, AnimationSlot.Base,
                restrictToSlot: null, caption: "Blend");
        y += (Row + RowGap) * s;

        foreach (var slot in PrimaryLayers)
            y += DrawLayerRow(actor, reading, owned, cursor, width, s, y,
                slot, AnimationSlots.DisplayName(slot), alwaysShow: false);

        y += InspectorLayout.Section(dl, cursor + new Vector2(0f, y), width,
            "anim", "ADVANCED SLOTS", ref _openAdvancedSlots, s, topBorder: false);
        if (_openAdvancedSlots)
            foreach (var slot in AdvancedLayers)
                y += DrawLayerRow(actor, reading, owned, cursor, width, s, y,
                    slot, AnimationSlots.DisplayName(slot), alwaysShow: true);
        return y;
    }

    /// <summary>
    /// One layer row on the shared grid: label, the animation name as the
    /// button that opens the picker for THAT destination, then pause,
    /// speed and reset. An inactive optional layer shows "Add layer"
    /// instead of a name, so the row is an invitation rather than an empty
    /// engine slot.
    /// </summary>
    private float DrawLayerRow(
        ActorId actor, ActorAnimationReading reading, AnimationOverrides owned,
        Vector2 cursor, float width, float s, float y,
        AnimationSlot slot, string label, bool alwaysShow)
    {
        ushort timeline = reading.TimelineFor(slot);
        bool active = timeline != 0;
        if (!active && !alwaysShow && !owned.SlotSpeeds.ContainsKey(slot))
        {
            // Optional and inactive: one invitation row, no controls.
            var empty = cursor + new Vector2(0f, y);
            float emptyX = LabelCell(empty, label, s);
            ImGui.SetCursorScreenPos(new Vector2(emptyX, empty.Y));
            if (Crystarium.Button("Add layer…", new ButtonProps
                {
                    Id = $"anim-add-{(int)slot}",
                    Classes = Cls.Compact,
                    Tooltip = $"Play an animation on the {label.ToLowerInvariant()} layer",
                    Style = new ButtonStyle
                    {
                        Width = Sizing.Fixed(MathF.Max(80f, width / s - LabelColumn - 8f)),
                    },
                }))
                _picker.Open(AnimationPickTarget.Slot, slot, slot,
                    $"{label} layer");
            return (Row + RowGap) * s;
        }

        var row = cursor + new Vector2(0f, y);
        float valueX = LabelCell(row, label, s);
        float trailing = (ActionWidth + 96f + ActionWidth + 18f) * s;

        ImGui.SetCursorScreenPos(new Vector2(valueX, row.Y));
        if (Crystarium.Button(NameFor(timeline, "Choose…"), new ButtonProps
            {
                Id = $"anim-layer-{(int)slot}",
                Classes = Cls.Compact,
                Tooltip = "Choose an animation for this layer",
                Style = new ButtonStyle
                {
                    Width = Sizing.Fixed(MathF.Max(
                        70f, (width - trailing) / s - LabelColumn - 8f)),
                },
            }))
            _picker.Open(AnimationPickTarget.Slot, slot, slot, $"{label} layer");

        float tx = cursor.X + width - trailing;
        bool slotPaused = owned.SlotSpeeds.TryGetValue(slot, out var ownedSpeed)
            && ownedSpeed == 0f;
        ImGui.SetCursorScreenPos(new Vector2(tx, row.Y));
        if (Crystarium.Button(slotPaused ? "Play" : "Pause", new ButtonProps
            {
                Id = $"anim-layer-pause-{(int)slot}",
                Classes = Cls.Compact,
                Tooltip = "Hold or release only this layer",
                Style = new ButtonStyle { Width = Sizing.Fixed(ActionWidth) },
            }))
            Report(
                slotPaused
                    ? _animation.ClearSlotSpeed(actor, slot)
                    : _animation.SetSlotSpeed(actor, slot, 0f),
                "Layer playback");

        float slotSpeed = owned.SlotSpeeds.TryGetValue(slot, out var over)
            ? over
            : reading.SpeedFor(slot);
        ImGui.SetCursorScreenPos(
            new Vector2(tx + (ActionWidth + 6f) * s, row.Y + 6f * s));
        if (Crystarium.Slider($"##anim-layer-speed-{(int)slot}", ref slotSpeed, 0f, 2f,
                new SliderProps { Style = new SliderStyle { Width = Sizing.Fixed(90f) } }))
            Report(_animation.SetSlotSpeed(actor, slot, slotSpeed), "Layer speed");

        ImGui.SetCursorScreenPos(
            new Vector2(tx + (ActionWidth + 102f) * s, row.Y));
        if (Crystarium.Button("Reset", new ButtonProps
            {
                Id = $"anim-layer-reset-{(int)slot}",
                Classes = Cls.Compact,
                Disabled = !owned.SlotSpeeds.ContainsKey(slot) &&
                    _animation.CapturedSlotTimeline(actor, slot) == null,
                Tooltip = "Restore this layer's incoming animation and speed",
                Style = new ButtonStyle { Width = Sizing.Fixed(ActionWidth) },
            }))
        {
            Report(_animation.ClearSlotSpeed(actor, slot), "Layer speed");
            Report(_animation.RestoreSlotTimeline(actor, slot), "Layer");
        }
        return (Row + RowGap) * s;
    }

    // ── D. Scrub ──────────────────────────────────────────────────────

    private float DrawScrub(
        ActorId actor, ActorAnimationReading reading, ImDrawListPtr dl,
        Vector2 cursor, float width, float s)
    {
        float y = Caption(cursor, "SCRUB", s);
        bool any = false;

        foreach (var slot in AnimationSlots.Scrubbable)
        {
            if (_animation.FindSlotControl(actor, slot) is not { } control)
                continue;
            any = true;
            y += DrawScrubRow(actor, cursor, width, s, y,
                AnimationSlots.DisplayName(slot), control);
        }

        if (!any)
        {
            ViewText.Label(cursor + new Vector2(0f, y + 4f * s),
                "Nothing playing on the body layers.", 11f,
                FontWeight.Regular, InspectorLayout.HintColor);
            y += (Row + RowGap) * s;
        }

        // Every Havok control the actor reports, for the cases the friendly
        // rows cannot cover.
        y += InspectorLayout.Section(dl, cursor + new Vector2(0f, y), width,
            "anim", "ADVANCED CONTROLS", ref _openAdvancedScrub, s, topBorder: false);
        if (_openAdvancedScrub)
        {
            foreach (var control in reading.Controls)
                y += DrawScrubRow(actor, cursor, width, s, y,
                    control.Id.ToString(), control);
            if (reading.Controls.Count == 0)
            {
                ViewText.Label(cursor + new Vector2(0f, y + 4f * s),
                    "No animation controls.", 11f,
                    FontWeight.Regular, InspectorLayout.HintColor);
                y += (Row + RowGap) * s;
            }
        }
        return y;
    }

    private float DrawScrubRow(
        ActorId actor, Vector2 cursor, float width, float s, float y,
        string label, ScrubControlReading control)
    {
        var row = cursor + new Vector2(0f, y);
        float valueX = LabelCell(row, label, s);
        float time = control.Time;
        float duration = MathF.Max(control.Duration, 0.0001f);
        float readoutWidth = 76f;

        ImGui.SetCursorScreenPos(new Vector2(valueX, row.Y + 6f * s));
        bool changed = Crystarium.Slider(
            $"##anim-scrub-{control.Id.Partial}-{control.Id.Control}",
            ref time, 0f, duration, new SliderProps
            {
                Style = new SliderStyle
                {
                    Width = Sizing.Fixed(MathF.Max(
                        80f, width / s - LabelColumn - readoutWidth - 8f)),
                },
            });

        // The drag owns the freeze for its length and releases the actor
        // paused on the frame it landed on.
        if (changed)
        {
            if (_scrub is not { } held || !held.Actor.Equals(actor) ||
                !held.Control.Equals(control.Id))
            {
                Report(_animation.BeginScrub(actor, control.Id), "Scrub");
                _scrub = (actor, control.Id);
            }
            Report(_animation.UpdateScrub(time), "Scrub");
        }
        if (_scrub is { } current && current.Actor.Equals(actor) &&
            current.Control.Equals(control.Id) && ImGui.IsItemDeactivated())
        {
            _animation.EndScrub();
            _scrub = null;
        }

        ViewText.Label(
            new Vector2(cursor.X + width - readoutWidth * s, row.Y + 6f * s),
            $"{control.Time:0.00}/{control.Duration:0.00}", 11f,
            FontWeight.Regular, InspectorLayout.HintColor, mono: true);
        return (Row + RowGap) * s;
    }

    // ── E. Face and lips ──────────────────────────────────────────────

    private float DrawFace(
        ActorId actor, ActorAnimationReading reading, Vector2 cursor, float width, float s)
    {
        float y = Caption(cursor, "FACE & LIPS", s);

        // Expression: the Expression catalog, previewed on the facial slot.
        var row = cursor + new Vector2(0f, y);
        float valueX = LabelCell(row, "Expression", s);
        float trailing = (ActionWidth + 110f + 12f) * s;
        ushort facial = reading.TimelineFor(AnimationSlot.Facial);
        ImGui.SetCursorScreenPos(new Vector2(valueX, row.Y));
        if (Crystarium.Button(NameFor(facial, "Choose expression…"), new ButtonProps
            {
                Id = "anim-expression",
                Classes = Cls.Compact,
                Tooltip = "Preview a facial expression",
                Style = new ButtonStyle
                {
                    Width = Sizing.Fixed(MathF.Max(
                        90f, (width - trailing) / s - LabelColumn - 8f)),
                },
            }))
            _picker.Open(AnimationPickTarget.Expression, AnimationSlot.Facial,
                AnimationSlot.Facial, "Expression", AnimationKind.Expression);

        float tx = cursor.X + width - trailing;
        ImGui.SetCursorScreenPos(new Vector2(tx, row.Y));
        if (Crystarium.Button("Preview", new ButtonProps
            {
                Id = "anim-expression-preview",
                Classes = Cls.Compact,
                Disabled = facial == 0,
                Tooltip = "Replay the current expression",
                Style = new ButtonStyle { Width = Sizing.Fixed(ActionWidth) },
            }) && facial != 0)
            Report(_animation.SetSlotTimeline(actor, AnimationSlot.Facial, facial),
                "Expression");

        ImGui.SetCursorScreenPos(new Vector2(tx + (ActionWidth + 6f) * s, row.Y));
        if (Crystarium.Button("Apply to face", new ButtonProps
            {
                Id = "anim-apply-face",
                Classes = Cls.Compact,
                Disabled = _facialCapture.IsPending,
                Tooltip = "Keep this face after the preview stops, as one undoable pose edit",
                Style = new ButtonStyle { Width = Sizing.Fixed(104f) },
            }))
        {
            var descriptor = Describe(actor);
            _status = descriptor == null
                ? "Apply to face: actor is no longer in the scene."
                : _facialCapture.Begin(actor, descriptor) is { Success: false } failed
                    ? $"Apply to face: {failed.Detail}"
                    : string.Empty;
        }
        y += (Row + RowGap) * s;

        // Lips: a known enumeration of speech timelines, not a catalog
        // query — the sheet's own slot data does not classify them as lip
        // animations, so a generic search returns nothing.
        row = cursor + new Vector2(0f, y);
        valueX = LabelCell(row, "Lips", s);
        ImGui.SetCursorScreenPos(new Vector2(valueX, row.Y));
        if (Crystarium.Button(NameFor(reading.LipsOverride, "None"), new ButtonProps
            {
                Id = "anim-lips",
                Classes = Cls.Compact,
                Tooltip = "Choose a speech animation",
                Style = new ButtonStyle
                {
                    Width = Sizing.Fixed(MathF.Max(
                        90f, (width - (ActionWidth + 12f) * s) / s - LabelColumn - 8f)),
                },
            }))
            _picker.Open(AnimationPickTarget.Lips, AnimationSlot.Lips, AnimationSlot.Lips,
                "Lips", entries: LipsEntries());

        ImGui.SetCursorScreenPos(
            new Vector2(cursor.X + width - ActionWidth * s, row.Y));
        if (Crystarium.Button("None", new ButtonProps
            {
                Id = "anim-lips-clear",
                Classes = Cls.Compact,
                Disabled = reading.LipsOverride == 0,
                Tooltip = "Restore the lip animation this actor arrived with",
                Style = new ButtonStyle { Width = Sizing.Fixed(ActionWidth) },
            }))
            Report(_animation.SetLips(actor, 0), "Lips");
        return y + (Row + RowGap) * s;
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
    private void Apply(ActorId actor, AnimationSelection selection, AnimationPick pick)
    {
        var timeline = (ushort)pick.Entry.TimelineId;
        switch (pick.Target)
        {
            case AnimationPickTarget.Base:
                Report(
                    _animation.PlayEntry(actor, pick.Entry, asBase: true,
                        selection.Interrupt && pick.PlayImmediately,
                        selection.PlayFromStart, forceLoop: false),
                    pick.Entry.Name);
                break;
            case AnimationPickTarget.Blend:
                Report(
                    _animation.PlayEntry(actor, pick.Entry, asBase: false,
                        selection.Interrupt, selection.PlayFromStart, forceLoop: false),
                    pick.Entry.Name);
                break;
            case AnimationPickTarget.Slot:
            case AnimationPickTarget.Expression:
                Report(_animation.SetSlotTimeline(actor, pick.Slot, timeline),
                    AnimationSlots.DisplayName(pick.Slot));
                break;
            case AnimationPickTarget.Lips:
                Report(_animation.SetLips(actor, timeline), "Lips");
                break;
        }
    }

    private void Report(AnimationResult result, string what) =>
        _status = result.Success ? string.Empty : $"{what}: {result.Detail}";

    private void Report(AnimationSceneActions.SceneActionReport report, string verb) =>
        _status = report.Success && report.Skipped.Count == 0
            ? string.Empty
            : report.Summary(verb);
}
