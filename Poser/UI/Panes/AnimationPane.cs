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
/// The Animation tab: compact sections and controls, with no list of its
/// own.
///
/// Choosing an animation is one shared surface — <see cref="AnimationPicker"/>
/// — opened from Base, Blend, Lips, and each slot's Select action, with
/// the CALLER supplying the destination. There is therefore exactly one
/// search UI in the product rather than a permanent catalog column plus a
/// per-slot duplicate of it.
///
/// Every control's width comes from its own style, because Crystarium
/// controls size themselves from <c>Style.Width</c> or the ambient width
/// and ignore <c>ImGui.SetNextItemWidth</c> entirely. Widths are declared
/// UNSCALED; the framework applies global scale.
///
/// The pane holds no per-actor state: play mode, interrupt, from-start and
/// the direct id live in the session's <see cref="AnimationSelection"/>
/// keyed by actor. Only disclosure and the in-flight scrub identity are
/// local, and the scrub carries its actor so a slider value can never land
/// in a previous actor's gesture.
/// </summary>
public sealed class AnimationPane
{
    private readonly AnimationSession _animation;
    private readonly AnimationCatalog _catalog;
    private readonly AnimationSceneActions _sceneActions;
    private readonly Game.Animation.FacialPoseCapture _facialCapture;
    private readonly AnimationPicker _picker;
    private readonly SceneSession _scene;

    // Local, deliberately not per-actor: disclosure is a view preference.
    private readonly Dictionary<AnimationSlot, bool> _slotOpen = new();
    private bool _openPlayback = true;
    private bool _openStance = true;
    private bool _openSlots;
    private bool _openScrub = true;
    private bool _openLips;

    // The in-flight scrub, carrying its actor.
    private (ActorId Actor, ScrubControlId Control)? _scrub;
    private string _status = string.Empty;

    private const float RowHeight = 28f;
    private const float LabelColumn = 96f;

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
    /// owning actor of a selected bone. Selection itself is untouched.</summary>
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

        if (TargetActor() is not { } actor)
        {
            ViewText.Label(origin + new Vector2(0f, 8f) * s,
                "Select an actor or bone to control its animation.", 12f,
                FontWeight.Regular, InspectorLayout.HintColor);
            DrawSceneActions(origin + new Vector2(0f, 32f) * s,
                InspectorLayout.ClampContentWidth(size.X, s), s);
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

        // Sections read like a document, so they are capped at the
        // inspector's readable measure rather than stretched across the
        // full pane.
        float width = InspectorLayout.ClampContentWidth(size.X, s);

        ImGui.SetCursorScreenPos(origin);
        Crystarium.PushScrollbarStyle();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (ImGui.BeginChild("##anim-page", size, false, ImGuiWindowFlags.NoSavedSettings))
        {
            DrawSections(actor, reading, owned, selection, width, s);
        }
        ImGui.EndChild();
        ImGui.PopStyleVar();
        Crystarium.PopScrollbarStyle();

        // The picker is drawn at the pane's top level, OUTSIDE the scroll
        // child: a popup opened inside a child parents to it and closes
        // the same frame.
        if (_picker.Draw() is { } pick)
            Apply(actor, selection, pick);
    }

    private void DrawSections(
        ActorId actor, ActorAnimationReading reading, AnimationOverrides owned,
        AnimationSelection selection, float width, float s)
    {
        var cursor = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        float y = cursor.Y;

        y += DrawHeader(actor, reading, owned, new Vector2(cursor.X, y), width, s);

        y += InspectorLayout.Section(dl, new Vector2(cursor.X, y), width,
            "anim", "PLAYBACK", ref _openPlayback, s, topBorder: true);
        if (_openPlayback)
            y += DrawPlayback(actor, reading, selection, new Vector2(cursor.X, y), width, s);

        y += InspectorLayout.Section(dl, new Vector2(cursor.X, y), width,
            "anim", "STANCE", ref _openStance, s, topBorder: true);
        if (_openStance)
            y += DrawStance(actor, reading, new Vector2(cursor.X, y), width, s);

        y += InspectorLayout.Section(dl, new Vector2(cursor.X, y), width,
            "anim", "SLOTS", ref _openSlots, s, topBorder: true);
        if (_openSlots)
            y += DrawSlots(actor, reading, owned, new Vector2(cursor.X, y), width, s);

        y += InspectorLayout.Section(dl, new Vector2(cursor.X, y), width,
            "anim", "SCRUB", ref _openScrub, s, topBorder: true);
        if (_openScrub)
            y += DrawScrub(actor, new Vector2(cursor.X, y), width, s);

        y += InspectorLayout.Section(dl, new Vector2(cursor.X, y), width,
            "anim", "LIPS & FACE", ref _openLips, s, topBorder: true);
        if (_openLips)
            y += DrawLips(actor, reading, new Vector2(cursor.X, y), width, s);

        if (_status.Length > 0)
        {
            ViewText.Label(new Vector2(cursor.X, y + 6f * s), _status, 11f,
                FontWeight.Regular, InspectorLayout.HintColor, wrap: true);
            y += 30f * s;
        }

        // Register the content extent so the page can scroll to it.
        ImGui.SetCursorScreenPos(cursor);
        ImGui.Dummy(new Vector2(width, y - cursor.Y));
    }

    /// <summary>Routes a pick to the destination the caller armed. The
    /// picker never decides where an animation goes.</summary>
    private void Apply(ActorId actor, AnimationSelection selection, AnimationPick pick)
    {
        switch (pick.Target)
        {
            case AnimationPickTarget.Base:
                Report(
                    _animation.PlayEntry(actor, pick.Entry, asBase: true,
                        selection.Interrupt, selection.PlayFromStart, forceLoop: false),
                    pick.Entry.Name);
                break;
            case AnimationPickTarget.Blend:
                Report(
                    _animation.PlayEntry(actor, pick.Entry, asBase: false,
                        selection.Interrupt, selection.PlayFromStart, forceLoop: false),
                    pick.Entry.Name);
                break;
            case AnimationPickTarget.Slot:
                Report(
                    _animation.SetSlotTimeline(actor, pick.Slot, (ushort)pick.Entry.TimelineId),
                    $"{AnimationSlots.DisplayName(pick.Slot)} slot");
                break;
            case AnimationPickTarget.Lips:
                Report(
                    _animation.SetLips(actor, (ushort)pick.Entry.TimelineId), "Lips");
                break;
        }
    }

    private string TimelineCaption(ushort timeline) =>
        timeline == 0
            ? "—"
            : _catalog.Find(timeline) is { } entry
                ? $"{entry.Name} ({timeline})"
                : timeline.ToString();

    // ── Header ────────────────────────────────────────────────────────

    private float DrawHeader(
        ActorId actor, ActorAnimationReading reading,
        AnimationOverrides owned, Vector2 cursor, float width, float s)
    {
        var name = Describe(actor)?.Name ?? "Actor";
        ushort current = reading.BaseTimeline != 0
            ? reading.BaseTimeline
            : reading.TimelineFor(AnimationSlot.Base);

        ViewText.Label(cursor, name, 13f, FontWeight.Medium, InspectorLayout.ValueColor);
        ViewText.Label(cursor + new Vector2(0f, 17f) * s,
            current == 0 ? "No animation" : TimelineCaption(current), 11f,
            FontWeight.Regular, InspectorLayout.HintColor, mono: true);

        float y = 38f * s;
        bool paused = _animation.IsPaused(actor);
        ImGui.SetCursorScreenPos(cursor + new Vector2(0f, y));
        if (Crystarium.Button(paused ? "Resume" : "Pause", new ButtonProps
            {
                Id = "anim-pause",
                Classes = Cls.Compact,
                Tooltip = paused
                    ? "Continue from the current frame"
                    : "Hold the actor on the current frame",
                Style = new ButtonStyle { Width = Sizing.Fixed(72f) },
            }))
            Report(paused ? _animation.Resume(actor) : _animation.Pause(actor), "Playback");

        ImGui.SameLine(0f, 6f * s);
        if (Crystarium.Button("Reset animation", new ButtonProps
            {
                Id = "anim-stop",
                Classes = Cls.Compact,
                Tooltip = "Restore this actor's incoming animation state",
                Style = new ButtonStyle { Width = Sizing.Fixed(116f) },
            }))
            Report(_animation.ResetActor(actor), "Reset animation");
        y += 32f * s;

        // Speed −5..10, normal 1. Reset drops the override so the game's
        // own speed returns rather than being pinned to 1.
        float speed = owned.OverallSpeed ?? reading.OverallSpeed;
        ViewText.Label(cursor + new Vector2(0f, y + 6f * s), "Speed", 11f,
            FontWeight.Regular, InspectorLayout.LabelColor);
        ImGui.SetCursorScreenPos(cursor + new Vector2(LabelColumn * s, y + 4f * s));
        if (Crystarium.Slider("##anim-speed", ref speed, -5f, 10f, new SliderProps
            {
                Style = new SliderStyle
                {
                    Width = Sizing.Fixed(MathF.Max(60f, width / s - LabelColumn - 84f)),
                },
            }))
            Report(_animation.SetSpeed(actor, speed), "Speed");
        ImGui.SetCursorScreenPos(cursor + new Vector2(width - 72f * s, y + 1f * s));
        if (Crystarium.Button("Normal", new ButtonProps
            {
                Id = "anim-speed-reset",
                Classes = Cls.Compact,
                Tooltip = "Hand playback speed back to the game",
                Style = new ButtonStyle { Width = Sizing.Fixed(64f) },
            }))
            Report(_animation.ClearSpeed(actor), "Speed");
        y += RowHeight * s;

        y += DrawSceneActions(cursor + new Vector2(0f, y), width, s);
        return y + 6f * s;
    }

    private float DrawSceneActions(Vector2 cursor, float width, float s)
    {
        // Measured widths are also the rendered widths, so the gaps cannot
        // drift, and the strip wraps rather than overflowing a narrow pane.
        var actions = new (string Label, string Id, string Tip,
            Func<AnimationSceneActions.SceneActionReport> Run)[]
        {
            ("Freeze all", "anim-freeze-all", "Pause every actor in the scene",
                _sceneActions.FreezeAll),
            ("Resume all", "anim-resume-all", "Resume every actor in the scene",
                _sceneActions.ResumeAll),
            ("Replay", "anim-replay-all", "Restart what each actor is already playing",
                _sceneActions.ReplayAll),
            ("Restore all", "anim-stop-all", "Restore every actor's incoming animation state",
                _sceneActions.StopAll),
        };

        float x = cursor.X;
        float y = cursor.Y;
        float gap = 6f * s;
        foreach (var action in actions)
        {
            float w = Crystarium.MeasureButton(action.Label, Cls.Compact).X;
            if (x > cursor.X && x + w > cursor.X + width)
            {
                x = cursor.X;
                y += 30f * s;
            }
            ImGui.SetCursorScreenPos(new Vector2(x, y));
            if (Crystarium.Button(action.Label, new ButtonProps
                {
                    Id = action.Id,
                    Classes = Cls.Compact,
                    Tooltip = action.Tip,
                    Style = new ButtonStyle { Width = Sizing.Fixed(w / s) },
                }))
                Report(action.Run(), action.Label);
            x += w + gap;
        }
        return (y - cursor.Y) + 30f * s;
    }

    // ── Playback ──────────────────────────────────────────────────────

    private float DrawPlayback(
        ActorId actor, ActorAnimationReading reading,
        AnimationSelection selection, Vector2 cursor, float width, float s)
    {
        float y = InspectorLayout.BodyGap * s;

        y += DrawPickRow(
            cursor, width, s, y, "Base",
            TimelineCaption(reading.BaseTimeline),
            "anim-pick-base",
            () => _picker.Open(AnimationPickTarget.Base, AnimationSlot.Base,
                restrictToSlot: null, caption: "Play as Base"));

        y += DrawPickRow(
            cursor, width, s, y, "Blend",
            "sequencer picks the slot",
            "anim-pick-blend",
            () => _picker.Open(AnimationPickTarget.Blend, AnimationSlot.Base,
                restrictToSlot: null, caption: "Play as Blend"));

        bool interrupt = selection.Interrupt;
        ViewText.Label(cursor + new Vector2(0f, y + 5f * s), "Interrupt", 11f,
            FontWeight.Regular, InspectorLayout.LabelColor);
        ImGui.SetCursorScreenPos(cursor + new Vector2(LabelColumn * s, y + 3f * s));
        if (Crystarium.Switch("##anim-interrupt", ref interrupt))
            _animation.SetSelection(actor, selection with { Interrupt = interrupt });

        bool fromStart = selection.PlayFromStart;
        ViewText.Label(cursor + new Vector2((LabelColumn + 52f) * s, y + 5f * s),
            "From start", 11f, FontWeight.Regular, InspectorLayout.LabelColor);
        ImGui.SetCursorScreenPos(cursor + new Vector2((LabelColumn + 130f) * s, y + 3f * s));
        if (Crystarium.Switch("##anim-fromstart", ref fromStart))
            _animation.SetSelection(actor, selection with { PlayFromStart = fromStart });
        y += RowHeight * s;

        int direct = selection.DirectTimelineId;
        ViewText.Label(cursor + new Vector2(0f, y + 6f * s), "Timeline id", 11f,
            FontWeight.Regular, InspectorLayout.LabelColor);
        ImGui.SetCursorScreenPos(cursor + new Vector2(LabelColumn * s, y + 2f * s));
        ImGui.SetNextItemWidth(80f * s); // raw ImGui widget: this one honours it
        if (ImGui.InputInt("##anim-id", ref direct, 0, 0))
            _animation.SetSelection(actor, selection with
            {
                DirectTimelineId = Math.Max(0, direct),
            });
        ImGui.SetCursorScreenPos(cursor + new Vector2((LabelColumn + 88f) * s, y + 1f * s));
        if (Crystarium.Button("Play id", new ButtonProps
            {
                Id = "anim-play-id",
                Classes = Cls.Compact,
                Disabled = selection.DirectTimelineId <= 0,
                Tooltip = "Play this id through the same path as a picked row",
                Style = new ButtonStyle { Width = Sizing.Fixed(60f) },
            }) && selection.DirectTimelineId > 0)
        {
            uint id = (uint)selection.DirectTimelineId;
            var entry = _catalog.Find(id) ?? new TimelineEntry(
                id, $"Timeline {id}", AnimationKind.RawTimeline, AnimationSlot.Base);
            Apply(actor, selection, new AnimationPick(
                entry,
                selection.PlayAsBase ? AnimationPickTarget.Base : AnimationPickTarget.Blend,
                AnimationSlot.Base));
        }
        return y + RowHeight * s + 6f * s;
    }

    /// <summary>Label · current value · Select — the one row shape every
    /// picker entry point uses, so Base, Blend, Lips and the slots all
    /// read the same.</summary>
    private float DrawPickRow(
        Vector2 cursor, float width, float s, float y,
        string label, string value, string id, Action open)
    {
        ViewText.Label(cursor + new Vector2(0f, y + 6f * s), label, 11f,
            FontWeight.Regular, InspectorLayout.LabelColor);
        ViewText.Label(cursor + new Vector2(LabelColumn * s, y + 6f * s), value, 11f,
            FontWeight.Regular, InspectorLayout.ValueColor, mono: true);
        ImGui.SetCursorScreenPos(cursor + new Vector2(width - 68f * s, y + 1f * s));
        if (Crystarium.Button("Select", new ButtonProps
            {
                Id = id,
                Classes = Cls.Compact,
                Tooltip = "Choose an animation",
                Style = new ButtonStyle { Width = Sizing.Fixed(60f) },
            }))
            open();
        return RowHeight * s;
    }

    // ── Stance ────────────────────────────────────────────────────────

    private float DrawStance(
        ActorId actor, ActorAnimationReading reading, Vector2 cursor, float width, float s)
    {
        float y = InspectorLayout.BodyGap * s;

        int stanceIndex = Array.IndexOf(StanceValues, reading.Stance);
        if (stanceIndex < 0)
            stanceIndex = 0;
        ViewText.Label(cursor + new Vector2(0f, y + 6f * s), "Stance", 11f,
            FontWeight.Regular, InspectorLayout.LabelColor);
        ImGui.SetCursorScreenPos(cursor + new Vector2(LabelColumn * s, y + 2f * s));
        if (Crystarium.SegmentedControl("##anim-stance", StanceLabels, ref stanceIndex,
                MathF.Min(240f, width / s - LabelColumn)))
            Report(_animation.SetStance(actor, StanceValues[stanceIndex], 0), "Stance");
        y += RowHeight * s;

        ViewText.Label(cursor + new Vector2(0f, y + 6f * s),
            $"Pose {reading.Pose}", 11f, FontWeight.Regular, InspectorLayout.LabelColor);
        ImGui.SetCursorScreenPos(cursor + new Vector2(LabelColumn * s, y + 1f * s));
        if (Crystarium.Button("Previous", new ButtonProps
            {
                Id = "anim-pose-prev", Classes = Cls.Compact,
                Tooltip = "Wraps to the last valid pose for this stance",
                Style = new ButtonStyle { Width = Sizing.Fixed(72f) },
            }))
            Report(
                _animation.SetStance(actor, StanceValues[stanceIndex], reading.Pose - 1),
                "Pose");
        ImGui.SameLine(0f, 6f * s);
        if (Crystarium.Button("Next", new ButtonProps
            {
                Id = "anim-pose-next", Classes = Cls.Compact,
                Tooltip = "Wraps to the first valid pose for this stance",
                Style = new ButtonStyle { Width = Sizing.Fixed(56f) },
            }))
            Report(
                _animation.SetStance(actor, StanceValues[stanceIndex], reading.Pose + 1),
                "Pose");
        y += RowHeight * s;

        bool drawn = reading.WeaponDrawn;
        ViewText.Label(cursor + new Vector2(0f, y + 5f * s), "Weapon", 11f,
            FontWeight.Regular, InspectorLayout.LabelColor);
        ImGui.SetCursorScreenPos(cursor + new Vector2(LabelColumn * s, y + 3f * s));
        if (Crystarium.Switch("##anim-weapon", ref drawn))
            Report(_animation.SetWeaponDrawn(actor, drawn), "Weapon");

        bool locked = _animation.OverridesFor(actor).PositionLock;
        ViewText.Label(cursor + new Vector2((LabelColumn + 52f) * s, y + 5f * s),
            "Lock position", 11f, FontWeight.Regular, InspectorLayout.LabelColor);
        ImGui.SetCursorScreenPos(cursor + new Vector2((LabelColumn + 148f) * s, y + 3f * s));
        if (Crystarium.Switch("##anim-poslock", ref locked))
            Report(_animation.SetPositionLock(actor, locked), "Position lock");
        return y + RowHeight * s + 6f * s;
    }

    // ── Slots ─────────────────────────────────────────────────────────

    private float DrawSlots(
        ActorId actor, ActorAnimationReading reading, AnimationOverrides owned,
        Vector2 cursor, float width, float s)
    {
        float y = InspectorLayout.BodyGap * s;
        var dl = ImGui.GetWindowDrawList();

        foreach (var slot in AnimationSlots.All)
        {
            ushort timeline = reading.TimelineFor(slot);
            bool open = _slotOpen.TryGetValue(slot, out var wasOpen) && wasOpen;
            y += InspectorLayout.Section(
                dl, cursor + new Vector2(12f * s, y), width - 12f * s,
                $"anim-slot-{(int)slot}",
                $"{AnimationSlots.DisplayName(slot)} · {TimelineCaption(timeline)}",
                ref open, s, topBorder: false);
            _slotOpen[slot] = open;
            if (!open)
                continue;

            var rowOrigin = cursor + new Vector2(24f * s, y);
            float rowWidth = width - 24f * s;

            var capturedSlot = slot;
            ImGui.SetCursorScreenPos(rowOrigin + new Vector2(0f, 2f * s));
            if (Crystarium.Button("Select", new ButtonProps
                {
                    Id = $"anim-slot-pick-{(int)slot}",
                    Classes = Cls.Compact,
                    Tooltip = "Choose an animation for this slot",
                    Style = new ButtonStyle { Width = Sizing.Fixed(60f) },
                }))
            {
                // Restricted to this slot, so a pick can never land a body
                // timeline in the face.
                _picker.Open(AnimationPickTarget.Slot, capturedSlot, capturedSlot,
                    $"Play into {AnimationSlots.DisplayName(capturedSlot)}");
            }

            bool slotPaused = owned.SlotSpeeds.TryGetValue(slot, out var ownedSpeed)
                && ownedSpeed == 0f;
            ImGui.SameLine(0f, 6f * s);
            if (Crystarium.Button(slotPaused ? "Resume" : "Pause", new ButtonProps
                {
                    Id = $"anim-slot-pause-{(int)slot}",
                    Classes = Cls.Compact,
                    Tooltip = "Hold or release just this slot",
                    Style = new ButtonStyle { Width = Sizing.Fixed(68f) },
                }))
                Report(
                    slotPaused
                        ? _animation.ClearSlotSpeed(actor, slot)
                        : _animation.SetSlotSpeed(actor, slot, 0f),
                    "Slot playback");

            ImGui.SameLine(0f, 6f * s);
            if (Crystarium.Button("Reset", new ButtonProps
                {
                    Id = $"anim-slot-reset-{(int)slot}",
                    Classes = Cls.Compact,
                    Disabled = !owned.SlotSpeeds.ContainsKey(slot) &&
                        _animation.CapturedSlotTimeline(actor, slot) == null,
                    Tooltip = "Restore this slot's incoming timeline and speed",
                    Style = new ButtonStyle { Width = Sizing.Fixed(60f) },
                }))
            {
                Report(_animation.ClearSlotSpeed(actor, slot), "Slot speed");
                Report(_animation.RestoreSlotTimeline(actor, slot), "Slot timeline");
            }
            y += RowHeight * s;

            float slotSpeed = owned.SlotSpeeds.TryGetValue(slot, out var over)
                ? over
                : reading.SpeedFor(slot);
            ViewText.Label(rowOrigin + new Vector2(0f, RowHeight * s + 6f * s), "Speed", 11f,
                FontWeight.Regular, InspectorLayout.LabelColor);
            ImGui.SetCursorScreenPos(rowOrigin + new Vector2(56f * s, RowHeight * s + 4f * s));
            if (Crystarium.Slider($"##anim-slot-speed-{(int)slot}", ref slotSpeed, 0f, 2f,
                    new SliderProps
                    {
                        Style = new SliderStyle
                        {
                            Width = Sizing.Fixed(MathF.Max(60f, rowWidth / s - 56f - 8f)),
                        },
                    }))
                Report(_animation.SetSlotSpeed(actor, slot, slotSpeed), "Slot speed");
            y += RowHeight * s + 4f * s;
        }
        return y + 6f * s;
    }

    // ── Scrub ─────────────────────────────────────────────────────────

    private float DrawScrub(ActorId actor, Vector2 cursor, float width, float s)
    {
        float y = InspectorLayout.BodyGap * s;
        bool any = false;

        foreach (var slot in AnimationSlots.Scrubbable)
        {
            if (_animation.FindSlotControl(actor, slot) is not { } control)
                continue;
            any = true;
            float time = control.Time;
            float duration = MathF.Max(control.Duration, 0.0001f);

            ViewText.Label(cursor + new Vector2(0f, y + 6f * s),
                AnimationSlots.DisplayName(slot), 11f, FontWeight.Regular,
                InspectorLayout.LabelColor);
            ImGui.SetCursorScreenPos(cursor + new Vector2(LabelColumn * s, y + 4f * s));
            bool changed = Crystarium.Slider(
                $"##anim-scrub-{(int)slot}", ref time, 0f, duration, new SliderProps
                {
                    Style = new SliderStyle
                    {
                        Width = Sizing.Fixed(MathF.Max(60f, width / s - LabelColumn - 76f)),
                    },
                });

            // The drag owns the freeze for its duration and releases the
            // actor paused on the landed frame.
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

            ViewText.Label(cursor + new Vector2(width - 70f * s, y + 6f * s),
                $"{control.Time:0.00}/{control.Duration:0.00}", 11f,
                FontWeight.Regular, InspectorLayout.HintColor, mono: true);
            y += RowHeight * s;
        }

        if (!any)
        {
            ViewText.Label(cursor + new Vector2(0f, y + 4f * s),
                "Nothing is playing on the full-body or upper-body slots.", 11f,
                FontWeight.Regular, InspectorLayout.HintColor);
            y += RowHeight * s;
        }
        return y + 6f * s;
    }

    // ── Lips and face ─────────────────────────────────────────────────

    private float DrawLips(
        ActorId actor, ActorAnimationReading reading, Vector2 cursor, float width, float s)
    {
        float y = InspectorLayout.BodyGap * s;

        y += DrawPickRow(
            cursor, width, s, y, "Lips",
            TimelineCaption(reading.LipsOverride),
            "anim-pick-lips",
            () => _picker.Open(AnimationPickTarget.Lips, AnimationSlot.Lips,
                AnimationSlot.Lips, "Set lip animation"));

        ImGui.SetCursorScreenPos(cursor + new Vector2(LabelColumn * s, y + 1f * s));
        if (Crystarium.Button("Clear lips", new ButtonProps
            {
                Id = "anim-lips-clear",
                Classes = Cls.Compact,
                Disabled = reading.LipsOverride == 0,
                Tooltip = "Restore the lip timeline this actor arrived with",
                Style = new ButtonStyle { Width = Sizing.Fixed(80f) },
            }))
            Report(_animation.SetLips(actor, 0), "Lips");
        y += RowHeight * s;

        // Baking is a POSE edit, not an animation override, so it sits
        // beside the facial controls but commits through transform history.
        ImGui.SetCursorScreenPos(cursor + new Vector2(0f, y + 2f * s));
        if (Crystarium.Button("Apply face to pose", new ButtonProps
            {
                Id = "anim-apply-face",
                Classes = Cls.Compact,
                Disabled = _facialCapture.IsPending,
                Tooltip = "Keep the previewed face after playback stops, as one undoable pose edit",
                Style = new ButtonStyle { Width = Sizing.Fixed(140f) },
            }))
        {
            var descriptor = Describe(actor);
            _status = descriptor == null
                ? "Apply face to pose: actor is no longer in the scene."
                : _facialCapture.Begin(actor, descriptor) is { Success: false } failed
                    ? $"Apply face to pose: {failed.Detail}"
                    : string.Empty;
        }
        if (_facialCapture.IsPending)
        {
            ViewText.Label(cursor + new Vector2(148f * s, y + 7f * s),
                "Settling…", 11f, FontWeight.Regular, InspectorLayout.HintColor);
        }
        return y + RowHeight * s + 6f * s;
    }

    // ── Status ────────────────────────────────────────────────────────

    private void Report(AnimationResult result, string what) =>
        _status = result.Success ? string.Empty : $"{what}: {result.Detail}";

    private void Report(AnimationSceneActions.SceneActionReport report, string verb) =>
        _status = report.Success && report.Skipped.Count == 0
            ? string.Empty
            : report.Summary(verb);
}
