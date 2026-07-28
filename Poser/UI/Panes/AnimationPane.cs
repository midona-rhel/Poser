using System;
using System.Collections.Generic;
using Poser.Application.Animation;
using Poser.Application.Scene;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.UI.Controls;

namespace Poser.UI;

/// <summary>
/// Actor animation mixer authored entirely through the shared Page/Form
/// contract. Runtime ownership remains in <see cref="AnimationSession"/>.
/// </summary>
public sealed class AnimationPane
{
    private readonly AnimationSession _animation;
    private readonly AnimationCatalog _catalog;
    private readonly AnimationSceneActions _sceneActions;
    private readonly Game.Animation.FacialPoseCapture _facialCapture;
    private readonly AnimationPicker _picker;
    private readonly SceneSession _scene;

    private bool _openGeneralPlayback = true;
    private bool _openStance = true;
    private bool _openLayers = true;
    private bool _openFace = true;
    private bool _openAdvancedSlots;
    private bool _openAdvancedControls;

    private (ActorId Actor, ScrubControlId Control)? _scrub;
    private IReadOnlyList<ScrubControlReading>? _scrubFrozenControls;
    private readonly Dictionary<(ActorId, AnimationSlot), ushort> _layerPicks =
        new();
    private string _status = string.Empty;
    private bool _sceneMenuRequested;

    private static readonly AnimationSlot[] PrimaryLayers =
    [
        AnimationSlot.UpperBody,
        AnimationSlot.Facial,
        AnimationSlot.Additive,
    ];

    private static readonly AnimationSlot[] AdvancedLayers =
    [
        AnimationSlot.Parts1,
        AnimationSlot.Parts2,
        AnimationSlot.Parts3,
        AnimationSlot.Parts4,
        AnimationSlot.Overlay,
    ];

    private static readonly string[] StanceLabels =
        ["Idle", "Chair", "Ground", "Sleep"];

    private static readonly AnimationStance[] StanceValues =
    [
        AnimationStance.Idle,
        AnimationStance.SitChair,
        AnimationStance.SitGround,
        AnimationStance.Sleeping,
    ];

    public Func<ActorDescriptor, string>? DisplayNameProvider;

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

    public void Draw(System.Numerics.Vector2 origin, System.Numerics.Vector2 size)
    {
        Crystarium.Page("animation", origin, size, page =>
        {
            if (TargetActor() is not { } actor)
            {
                page.EmptyState();
                return;
            }
            if (!_animation.IsSupported(actor))
            {
                page.EmptyState(
                    "This actor does not support animation control.");
                return;
            }
            if (_scrub is { } active && !active.Actor.Equals(actor))
                EndScrub();

            var reading =
                _animation.Read(actor) ?? ActorAnimationReading.Empty;
            var owned = _animation.OverridesFor(actor);
            var descriptor = Describe(actor);
            page.Status(descriptor == null
                ? "Actor"
                : DisplayNameProvider?.Invoke(descriptor) ?? "Actor");
            page.Status(_status);

            page.Section(
                "GENERAL PLAYBACK",
                _openGeneralPlayback,
                next => _openGeneralPlayback = next,
                form => DrawPlayback(form, actor, reading, owned));
            page.Section(
                "STANCE",
                _openStance,
                next => _openStance = next,
                form => DrawStance(form, actor, reading));
            page.Section(
                "LAYERS",
                _openLayers,
                next => _openLayers = next,
                form =>
                {
                    DrawLayer(
                        form, actor, reading, owned,
                        AnimationSlot.Base, "Full body", alwaysShow: true);
                    foreach (var slot in PrimaryLayers)
                        DrawLayer(
                            form, actor, reading, owned, slot,
                            AnimationSlots.DisplayName(slot),
                            alwaysShow: false);
                });
            page.Section(
                "FACE & LIPS",
                _openFace,
                next => _openFace = next,
                form => DrawFace(form, actor, reading));
            page.Section(
                "ADVANCED SLOTS",
                _openAdvancedSlots,
                next => _openAdvancedSlots = next,
                form =>
                {
                    foreach (var slot in AdvancedLayers)
                        DrawLayer(
                            form, actor, reading, owned, slot,
                            AnimationSlots.DisplayName(slot),
                            alwaysShow: true);
                });
            page.Section(
                "ADVANCED CONTROLS",
                _openAdvancedControls,
                next => _openAdvancedControls = next,
                form => DrawAdvancedControls(form, actor, reading));
        });

        if (_picker.Draw() is { } pick && TargetActor() is { } target)
            Apply(target, pick);
    }

    private void DrawPlayback(
        Crystarium.FormScope form,
        ActorId actor,
        ActorAnimationReading reading,
        AnimationOverrides owned)
    {
        ushort current = reading.BaseTimeline != 0
            ? reading.BaseTimeline
            : reading.TimelineFor(AnimationSlot.Base);
        bool paused = _animation.IsPaused(actor);
        form.Picker(
            "Animation",
            NameFor(current, "Choose…"),
            () => _picker.Open(
                AnimationPickTarget.Base,
                AnimationSlot.Base,
                restrictToSlot: AnimationSlot.Base,
                caption: "Animation"),
            actions =>
            {
                actions.Button(
                    paused ? "Play" : "Pause",
                    () => Report(
                        paused
                            ? _animation.Resume(actor)
                            : _animation.Pause(actor),
                        "Playback"),
                    help: paused
                        ? "Resume from the current frame"
                        : "Hold the current frame");
                actions.Button(
                    "Replay",
                    () => Report(
                        _animation.Blend(actor, current), "Replay"),
                    disabled: current == 0,
                    help: "Restart the current animation");
                actions.Button(
                    "Restore",
                    () => Report(
                        _animation.ResetActor(actor), "Restore"),
                    help: "Restore this actor's incoming animation state");
            },
            help: "Choose the animation this actor plays");

        float speed = owned.OverallSpeed ?? reading.OverallSpeed;
        form.NumericSlider(
            "Speed",
            speed,
            -5f,
            10f,
            next => Report(_animation.SetSpeed(actor, next), "Speed"),
            perPixel: 0.01f,
            marks: [0f, 1f],
            help: "Actor playback speed");
        form.Actions("Playback", actions =>
        {
            actions.Button(
                "Reset speed",
                () => Report(_animation.ClearSpeed(actor), "Speed"),
                help: "Hand playback speed back to the game");
            actions.Button(
                "All actors…",
                () => _sceneMenuRequested = true,
                help: "Freeze, resume, replay or restore every actor");
        });
        DrawSceneMenu();
    }

    private void DrawStance(
        Crystarium.FormScope form,
        ActorId actor,
        ActorAnimationReading reading)
    {
        bool supported = _animation.SupportsStance;
        int stanceIndex = Array.IndexOf(StanceValues, reading.Stance);
        form.ActionDropdown(
            "Stance",
            StanceLabels,
            stanceIndex,
            StanceName(reading.Stance),
            picked =>
            {
                int pose = StanceValues[picked] == reading.Stance
                    ? reading.Pose
                    : 0;
                Report(
                    _animation.SetStance(
                        actor, StanceValues[picked], pose),
                    "Stance");
            },
            disabled: !supported,
            help: supported
                ? "Pose family — picking one returns the actor to it"
                : "Stance changes are unavailable");

        var poseFamily = stanceIndex >= 0
            ? reading.Stance
            : AnimationStance.Idle;
        var owned = _animation.OverridesFor(actor);
        bool poseDisabled = !supported
            || AnimationTimelines.PoseCount(
                poseFamily, reading.WeaponDrawn) <= 1
            || owned.LoopedSlots.ContainsKey(AnimationSlot.Base)
            || (owned.BaseTimeline is { } basePick
                && reading.TimelineFor(AnimationSlot.Base) == basePick);
        form.Actions($"Pose {reading.Pose}", actions =>
        {
            actions.Button(
                "Previous",
                () => Report(
                    _animation.SetStance(
                        actor, poseFamily, reading.Pose - 1),
                    "Pose"),
                disabled: poseDisabled,
                help: "Previous pose (wraps)");
            actions.Button(
                "Next",
                () => Report(
                    _animation.SetStance(
                        actor, poseFamily, reading.Pose + 1),
                    "Pose"),
                disabled: poseDisabled,
                help: "Next pose (wraps)");
        });
        form.Switch(
            "Weapon",
            reading.WeaponDrawn,
            next => Report(
                _animation.SetWeaponDrawn(actor, next), "Weapon"));
        form.Switch(
            "Lock position",
            owned.PositionLock,
            next => Report(
                _animation.SetPositionLock(actor, next), "Position lock"));
    }

    private void DrawLayer(
        Crystarium.FormScope form,
        ActorId actor,
        ActorAnimationReading reading,
        AnimationOverrides owned,
        AnimationSlot slot,
        string label,
        bool alwaysShow)
    {
        ushort live = reading.TimelineFor(slot);
        ushort timeline = live != 0
            ? live
            : _layerPicks.TryGetValue(
                (actor, slot), out var remembered)
                ? remembered
                : (ushort)0;
        bool active = timeline != 0;
        bool hasOwnedSpeed = owned.SlotSpeeds.ContainsKey(slot);
        bool compactEmpty = !active && !alwaysShow && !hasOwnedSpeed;
        var captured = slot;
        bool paused = owned.SlotSpeeds.TryGetValue(
            slot, out var ownedSpeed) && ownedSpeed == 0f;

        form.Picker(
            label,
            active ? NameFor(timeline, "Choose…") : "Add layer…",
            () => _picker.Open(
                AnimationPickTarget.Slot,
                captured,
                captured,
                $"{label} layer"),
            compactEmpty
                ? null
                : actions =>
                {
                    if (live == 0)
                    {
                        actions.Button(
                            "Replay",
                            () => Report(
                                _animation.Blend(actor, timeline), label),
                            disabled: timeline == 0,
                            help: "Play this animation again");
                    }
                    else
                    {
                        actions.Button(
                            paused ? "Play" : "Pause",
                            () => Report(
                                paused
                                    ? _animation.ClearSlotSpeed(
                                        actor, captured)
                                    : _animation.SetSlotSpeed(
                                        actor, captured, 0f),
                                "Layer playback"),
                            help: "Hold or release only this layer");
                    }
                    actions.Button(
                        "Reset",
                        () => Report(
                            _animation.ClearSlotSpeed(actor, captured),
                            "Layer speed"),
                        disabled: !hasOwnedSpeed,
                        help: "Hand this layer's speed back to the game");
                },
            help: $"Choose an animation for the {label.ToLowerInvariant()} layer");

        if (!compactEmpty)
        {
            float speed = owned.SlotSpeeds.TryGetValue(
                slot, out var overrideSpeed)
                ? overrideSpeed
                : reading.SpeedFor(slot);
            form.NumericSlider(
                $"{label} speed",
                speed,
                0f,
                2f,
                next => Report(
                    _animation.SetSlotSpeed(actor, captured, next),
                    "Layer speed"),
                perPixel: 0.005f,
                marks: [1f],
                help: $"Playback speed for the {label.ToLowerInvariant()} layer");
        }

        if (slot is AnimationSlot.Base or AnimationSlot.UpperBody)
        {
            var control = _animation.FindSlotControl(actor, slot)
                ?? new ScrubControlReading(
                    new ScrubControlId(-1, (int)slot),
                    0f,
                    0f,
                    0f);
            DrawScrub(
                form,
                actor,
                reading,
                label,
                control,
                slot,
                timeline);
        }
    }

    private void DrawAdvancedControls(
        Crystarium.FormScope form,
        ActorId actor,
        ActorAnimationReading reading)
    {
        var controls = AdvancedControls(reading);
        if (controls.Count == 0)
        {
            form.Status("No animation controls.");
            return;
        }
        foreach (var control in controls)
            DrawScrub(
                form,
                actor,
                reading,
                control.Id.ToString(),
                control);
    }

    private void DrawScrub(
        Crystarium.FormScope form,
        ActorId actor,
        ActorAnimationReading reading,
        string label,
        ScrubControlReading control,
        AnimationSlot? loopSlot = null,
        ushort loopTimeline = 0)
    {
        bool scrubbable = control.Duration > 0f;
        float duration = MathF.Max(control.Duration, 0.0001f);

        void EnsureScrub()
        {
            if (_scrub is { } held
                && held.Actor.Equals(actor)
                && held.Control.Equals(control.Id))
                return;
            Report(
                _animation.BeginScrub(actor, control.Id), "Scrub");
            _scrub = (actor, control.Id);
            _scrubFrozenControls = reading.Controls;
        }

        void Commit()
        {
            if (_scrub is { } held
                && held.Actor.Equals(actor)
                && held.Control.Equals(control.Id))
                EndScrub();
        }

        form.NumericSlider(
            label,
            control.Time,
            0f,
            duration,
            next =>
            {
                EnsureScrub();
                Report(
                    _animation.UpdateScrub(
                        Math.Clamp(next, 0f, duration)),
                    "Scrub");
            },
            perPixel: 0.01f,
            disabled: !scrubbable,
            help: scrubbable
                ? $"Animation time / {control.Duration:0.00}"
                : "No active animation control",
            onBegin: EnsureScrub,
            onCommit: Commit);

        if (loopSlot is { } slot)
        {
            bool looped = _animation.OverridesFor(actor)
                .LoopedSlots.ContainsKey(slot);
            form.Switch(
                $"{label} loop",
                looped,
                next => Report(
                    _animation.SetSlotLoop(
                        actor, slot, loopTimeline, next),
                    "Loop"),
                help: "Play this layer's animation again when it ends");
        }
    }

    private IReadOnlyList<ScrubControlReading> AdvancedControls(
        ActorAnimationReading reading)
    {
        if (_scrub == null || _scrubFrozenControls == null)
            return reading.Controls;
        var merged = new List<ScrubControlReading>(
            _scrubFrozenControls.Count);
        foreach (var frozen in _scrubFrozenControls)
        {
            var current = frozen;
            foreach (var live in reading.Controls)
                if (live.Id.Equals(frozen.Id))
                    current = live;
            merged.Add(current);
        }
        return merged;
    }

    private void DrawFace(
        Crystarium.FormScope form,
        ActorId actor,
        ActorAnimationReading reading)
    {
        ushort held = _animation.HeldExpressionFor(actor) ?? 0;
        ushort facial = held != 0
            ? held
            : reading.TimelineFor(AnimationSlot.Facial);
        form.Picker(
            "Expression",
            NameFor(facial, "Choose expression…"),
            () => _picker.Open(
                AnimationPickTarget.Expression,
                AnimationSlot.Facial,
                AnimationSlot.Facial,
                "Expression",
                AnimationKind.Expression),
            actions =>
            {
                actions.Button(
                    "Preview",
                    () => Report(
                        held != 0
                            ? _animation.HoldExpression(actor, held)
                            : _animation.Blend(actor, facial),
                        "Expression"),
                    disabled: facial == 0,
                    help: "Replay the held expression from its start");
                actions.Button(
                    "Release",
                    () => Report(
                        _animation.ReleaseExpression(actor), "Expression"),
                    disabled: held == 0,
                    help: "Let the face return to the base animation");
                actions.Button(
                    "Apply to face",
                    () =>
                    {
                        var descriptor = Describe(actor);
                        _status = descriptor == null
                            ? "Apply to face: actor is no longer in the scene."
                            : _facialCapture.Begin(actor, descriptor)
                                is { Success: false } failed
                                ? $"Apply to face: {failed.Detail}"
                                : string.Empty;
                    },
                    disabled: _facialCapture.IsPending,
                    help: "Keep this face as one undoable pose edit");
            });

        form.Picker(
            "Lips",
            NameFor(reading.LipsOverride, "Choose speech…"),
            () => _picker.Open(
                AnimationPickTarget.Lips,
                AnimationSlot.Lips,
                AnimationSlot.Lips,
                "Lips",
                entries: LipsEntries()),
            actions => actions.Button(
                "None",
                () => Report(_animation.SetLips(actor, 0), "Lips"),
                disabled: reading.LipsOverride == 0,
                help: "Restore the incoming lip animation"));
    }

    private void DrawSceneMenu()
    {
        if (_sceneMenuRequested)
        {
            _sceneMenuRequested = false;
            Crystarium.FloatingMenu.Open(
                "##anim-scene-menu",
                Dalamud.Bindings.ImGui.ImGui.GetMousePos(),
                [
                    new ContextMenuItem(
                        "Freeze all",
                        TablerIcon.PlayerPlay),
                    new ContextMenuItem(
                        "Resume all",
                        TablerIcon.PlayerPlay),
                    new ContextMenuItem(
                        "Replay all",
                        TablerIcon.Refresh),
                    new ContextMenuItem(
                        "Restore all",
                        TablerIcon.ArrowBackUp),
                ]);
        }
        switch (Crystarium.FloatingMenu.Draw("##anim-scene-menu"))
        {
            case 0:
                Report(_sceneActions.FreezeAll(), "Freeze all");
                break;
            case 1:
                Report(_sceneActions.ResumeAll(), "Resume all");
                break;
            case 2:
                Report(_sceneActions.ReplayAll(), "Replay all");
                break;
            case 3:
                Report(_sceneActions.StopAll(), "Restore all");
                break;
        }
    }

    private ActorId? TargetActor() => _scene.Selection.Primary switch
    {
        { Kind: SceneEntityKind.Actor, Actor: { } actor } => actor,
        { Kind: SceneEntityKind.Bone, Bone: { } bone } =>
            bone.Skeleton.Actor,
        _ => null,
    };

    private ActorDescriptor? Describe(ActorId id)
    {
        foreach (var actor in _scene.Snapshot.Actors)
            if (actor.Id.Equals(id))
                return actor;
        return null;
    }

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

    private IReadOnlyList<TimelineEntry> LipsEntries()
    {
        var entries = new List<TimelineEntry>();
        for (ushort id = AnimationTimelines.FirstLips;
             id <= AnimationTimelines.LastLips;
             id++)
        {
            entries.Add(_catalog.Find(id) ?? new TimelineEntry(
                id,
                $"Speech {id - AnimationTimelines.FirstLips + 1}",
                AnimationKind.RawTimeline,
                AnimationSlot.Lips));
        }
        return entries;
    }

    private string NameFor(ushort timeline, string empty) =>
        timeline == 0
            ? empty
            : _catalog.Find(timeline) is { } entry
                ? entry.Name
                : $"Timeline {timeline}";

    private void Apply(ActorId actor, AnimationPick pick)
    {
        var timeline = (ushort)pick.Entry.TimelineId;
        switch (pick.Target)
        {
            case AnimationPickTarget.Base:
            {
                var played = _animation.PlayEntry(
                    actor, pick.Entry, asBase: true, playFromStart: true);
                if (!played.Success)
                {
                    Report(played, pick.Entry.Name);
                    break;
                }
                _layerPicks[(actor, pick.Entry.Slot)] = timeline;
                Report(
                    ArmLoop(
                        actor, pick.Entry.Slot, timeline, played),
                    pick.Entry.Name);
                break;
            }
            case AnimationPickTarget.Slot:
            {
                var played = _animation.Blend(actor, timeline);
                if (!played.Success)
                {
                    Report(
                        played,
                        AnimationSlots.DisplayName(pick.Slot));
                    break;
                }
                _layerPicks[(actor, pick.Entry.Slot)] = timeline;
                Report(
                    ArmLoop(
                        actor, pick.Entry.Slot, timeline, played),
                    AnimationSlots.DisplayName(pick.Slot));
                break;
            }
            case AnimationPickTarget.Expression:
                Report(
                    _animation.HoldExpression(actor, timeline),
                    "Expression");
                break;
            case AnimationPickTarget.Lips:
                Report(
                    _animation.SetLips(actor, timeline),
                    "Lips");
                break;
        }
    }

    private AnimationResult ArmLoop(
        ActorId actor,
        AnimationSlot slot,
        ushort timeline,
        AnimationResult played)
    {
        if (slot is not (AnimationSlot.Base or AnimationSlot.UpperBody))
            return played;
        var armed = _animation.SetSlotLoop(
            actor, slot, timeline, true);
        return armed.Success ? played : armed;
    }

    private void EndScrub()
    {
        _animation.EndScrub();
        _scrub = null;
        _scrubFrozenControls = null;
    }

    private void Report(AnimationResult result, string what) =>
        _status = result.Success
            ? string.Empty
            : $"{what}: {result.Detail}";

    private void Report(
        AnimationSceneActions.SceneActionReport report,
        string verb) =>
        _status = report.Success && report.Skipped.Count == 0
            ? string.Empty
            : report.Summary(verb);
}
