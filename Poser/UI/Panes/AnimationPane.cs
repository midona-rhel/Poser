using System;
using System.Collections.Generic;
using System.Numerics;
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
///
/// <para>The page is DECLARED, not drawn: one <see cref="UiRoot"/> renders the
/// whole tree each frame from a props struct. The imperative survivors are the
/// legacy animation picker and the scene floating menu, both pumped after the
/// render as named legacy boundaries.</para>
/// </summary>
public sealed class AnimationPane
{
    private readonly AnimationSession _animation;
    private readonly AnimationCatalog _catalog;
    private readonly AnimationSceneActions _sceneActions;
    private readonly Game.Animation.FacialPoseCapture _facialCapture;
    private readonly AnimationPicker _picker;
    private readonly SceneSession _scene;
    private readonly UiRoot _root = new();

    private bool _openGeneral = true;
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

    /// <summary>The per-actor callbacks for whichever actor is selected, kept
    /// until the target changes. See <see cref="ActorHandlers"/>.</summary>
    private ActorHandlers? _handlers;

    /// <summary>The exact actor and slot captured when the picker opened. A
    /// selection change while the popover is open never retargets the pending
    /// pick, and the row that opened the picker is the row that remembers
    /// it.</summary>
    private ActorId? _pickActor;
    private AnimationSlot? _pickSlot;

    // ── retained native islands ──────────────────────────────────────────
    private readonly PickerTriggerState _baseTrigger = new();
    private readonly PickerTriggerState _expressionTrigger = new();
    private readonly PickerTriggerState _lipsTrigger = new();
    private readonly NumericWellState _speedWell = new();

    /// <summary>One holder per slot, created on first use and kept for the
    /// pane's life. The slot enum bounds the dictionary.</summary>
    private readonly Dictionary<AnimationSlot, SlotUi> _slotUi = new();

    /// <summary>One holder per advanced control, bounded by the skeleton's
    /// partials.</summary>
    private readonly Dictionary<ScrubControlId, ScrubUi> _scrubUi = new();

    /// <summary>The lips catalogue is static once loaded, so it is built once
    /// rather than per frame.</summary>
    private IReadOnlyList<TimelineEntry>? _lipsEntries;

    // ── hoisted handlers ─────────────────────────────────────────────────
    // A build path may allocate no delegate, so every callback the tree names
    // is a field. These seven depend on nothing per-actor.
    private readonly Action<bool> _toggleGeneral;
    private readonly Action<bool> _toggleStance;
    private readonly Action<bool> _toggleLayers;
    private readonly Action<bool> _toggleFace;
    private readonly Action<bool> _toggleAdvancedSlots;
    private readonly Action<bool> _toggleAdvancedControls;
    private readonly Action _openSceneMenu;

    /// <summary>Grow-only scratch for the sections whose row COUNT is decided
    /// per frame. <see cref="UiChildren.Create"/> copies into the frame arena,
    /// so one buffer serves every section in turn.</summary>
    private UiNode[] _rows = new UiNode[32];
    private int _rowCount;

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

    private static readonly float[] SpeedMarks = [0f, 1f];
    private static readonly float[] UnitMarks = [1f];

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
        _toggleGeneral = next => _openGeneral = next;
        _toggleStance = next => _openStance = next;
        _toggleLayers = next => _openLayers = next;
        _toggleFace = next => _openFace = next;
        _toggleAdvancedSlots = next => _openAdvancedSlots = next;
        _toggleAdvancedControls = next => _openAdvancedControls = next;
        _openSceneMenu = () => _sceneMenuRequested = true;
    }

    /// <summary>Everything one frame's build is TOLD. The pane reference is
    /// what the static builder reaches its services through — reading a service
    /// allocates nothing, and a closure over them would allocate every
    /// frame.</summary>
    private readonly record struct Props(
        AnimationPane Pane, ActorHandlers? Handlers);

    public void Draw(Vector2 origin, Vector2 size)
    {
        Props props = new(this, Handlers());
        _root.Render(origin, size, in props, static (in Props p) => p.Pane.Build(in p));

        // The pending pick belongs to the actor that opened the picker, not to
        // whatever the sidebar selects while the popover is up.
        if (_picker.Draw() is { } pick && _pickActor is { } frozen)
            Apply(frozen, pick);
        DrawSceneMenu();
    }

    private ActorHandlers? Handlers()
    {
        if (TargetActor() is not { } actor)
            return null;
        if (_handlers is not { } cached || !cached.Actor.Equals(actor))
            _handlers = new ActorHandlers(this, actor);
        return _handlers;
    }

    private UiNode Build(in Props props)
    {
        if (props.Handlers is not { } handlers)
            return Crystarium.Page(Crystarium.PageEmptyState());

        ActorId actor = handlers.Actor;
        if (!_animation.IsSupported(actor))
        {
            return Crystarium.Page(Crystarium.PageEmptyState(
                "This actor does not support animation control."));
        }

        if (_scrub is { } active && !active.Actor.Equals(actor))
            EndScrub();

        var reading = _animation.Read(actor) ?? ActorAnimationReading.Empty;
        var owned = _animation.OverridesFor(actor);

        return Crystarium.Page(
        [
            Crystarium.PageStatus(_status),
            new Section
            {
                Title = "GENERAL",
                NoDivider = true,
                Expanded = _openGeneral,
                OnExpandedChange = _toggleGeneral,
                Children = _openGeneral
                    ? GeneralRows(handlers, reading, owned)
                    : UiChildren.Empty,
                Key = "general",
            },
            new Section
            {
                Title = "STANCE",
                Expanded = _openStance,
                OnExpandedChange = _toggleStance,
                Children = _openStance
                    ? StanceRows(handlers, reading)
                    : UiChildren.Empty,
                Key = "stance",
            },
            new Section
            {
                Title = "LAYERS",
                Expanded = _openLayers,
                OnExpandedChange = _toggleLayers,
                Children = _openLayers
                    ? LayerRows(actor, reading, owned)
                    : UiChildren.Empty,
                Key = "layers",
            },
            new Section
            {
                Title = "FACE & LIPS",
                Expanded = _openFace,
                OnExpandedChange = _toggleFace,
                Children = _openFace
                    ? FaceRows(handlers, reading)
                    : UiChildren.Empty,
                Key = "face",
            },
            new Section
            {
                Title = "ADVANCED SLOTS",
                Expanded = _openAdvancedSlots,
                OnExpandedChange = _toggleAdvancedSlots,
                Children = _openAdvancedSlots
                    ? AdvancedSlotRows(actor, reading, owned)
                    : UiChildren.Empty,
                Key = "advanced-slots",
            },
            new Section
            {
                Title = "ADVANCED CONTROLS",
                Expanded = _openAdvancedControls,
                OnExpandedChange = _toggleAdvancedControls,
                Children = _openAdvancedControls
                    ? AdvancedControlRows(actor, reading)
                    : UiChildren.Empty,
                Key = "advanced-controls",
            },
        ]);
    }

    // ── sections ─────────────────────────────────────────────────────────

    private UiChildren GeneralRows(
        ActorHandlers handlers,
        ActorAnimationReading reading,
        AnimationOverrides owned)
    {
        ActorId actor = handlers.Actor;
        ushort current = reading.BaseTimeline != 0
            ? reading.BaseTimeline
            : reading.TimelineFor(AnimationSlot.Base);
        bool paused = _animation.IsPaused(actor);
        handlers.BaseTimeline = current;
        handlers.BasePaused = paused;

        float speed = owned.OverallSpeed ?? reading.OverallSpeed;
        return
        [
            Crystarium.FormPickerActions(
                "Animation",
                NameFor(current, "Choose…"),
                handlers.OpenBase,
                _baseTrigger,
                [
                    new Button
                    {
                        Label = paused ? "Play" : "Pause",
                        Dense = true,
                        OnClick = handlers.PlayPause,
                        Help = paused
                            ? "Resume from the current frame"
                            : "Hold the current frame",
                    },
                    new Button
                    {
                        Label = "Replay",
                        Dense = true,
                        OnClick = handlers.ReplayBase,
                        Disabled = current == 0,
                        Help = "Restart the current animation",
                    },
                    new Button
                    {
                        Label = "Restore",
                        Dense = true,
                        OnClick = handlers.RestoreActor,
                        Help = "Restore this actor's incoming animation state",
                    },
                ],
                triggerHelp: "Choose the animation this actor plays"),
            Crystarium.FormNumericSlider(
                "Speed",
                speed,
                -5f,
                10f,
                handlers.SetSpeed,
                _speedWell,
                0.01f,
                marks: SpeedMarks,
                help: "Actor playback speed"),
            Crystarium.FormActions(
                "Playback",
                [
                    new Button
                    {
                        Label = "Reset speed",
                        Dense = true,
                        OnClick = handlers.ResetSpeed,
                        Help = "Hand playback speed back to the game",
                    },
                    new Button
                    {
                        Label = "All actors…",
                        Dense = true,
                        OnClick = _openSceneMenu,
                        Help = "Freeze, resume, replay or restore every actor",
                    },
                ]),
        ];
    }

    private UiChildren StanceRows(
        ActorHandlers handlers, ActorAnimationReading reading)
    {
        ActorId actor = handlers.Actor;
        bool supported = _animation.SupportsStance;
        int stanceIndex = Array.IndexOf(StanceValues, reading.Stance);
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

        handlers.Stance = reading.Stance;
        handlers.Pose = reading.Pose;
        handlers.PoseFamily = poseFamily;

        return
        [
            Crystarium.FormPair(
                "Stance",
                new Dropdown
                {
                    Items = StanceLabels,
                    Selected = stanceIndex,
                    OnChange = handlers.PickStance,
                    Preview = StanceName(reading.Stance),
                    ReselectFires = true,
                    Disabled = !supported,
                    Help = supported
                        ? "Pose family — picking one returns the actor to it"
                        : "Stance changes are unavailable",
                },
                $"Pose {reading.Pose}",
                new Row
                {
                    Sheet = SheetFamily.ActionGroup,
                    Children =
                    [
                        new Button
                        {
                            Label = "Previous",
                            Dense = true,
                            OnClick = handlers.PreviousPose,
                            Disabled = poseDisabled,
                            Help = "Previous pose (wraps)",
                        },
                        new Button
                        {
                            Label = "Next",
                            Dense = true,
                            OnClick = handlers.NextPose,
                            Disabled = poseDisabled,
                            Help = "Next pose (wraps)",
                        },
                    ],
                }),
            Crystarium.FormPair(
                "Weapon",
                new Switch
                {
                    Value = reading.WeaponDrawn,
                    OnToggle = handlers.SetWeaponDrawn,
                },
                "Lock position",
                new Switch
                {
                    Value = owned.PositionLock,
                    OnToggle = handlers.SetPositionLock,
                }),
        ];
    }

    private UiChildren LayerRows(
        ActorId actor,
        ActorAnimationReading reading,
        AnimationOverrides owned)
    {
        BeginRows();
        EmitLayer(
            actor, reading, owned,
            AnimationSlot.Base, "Full body", alwaysShow: true);
        for (int i = 0; i < PrimaryLayers.Length; i++)
        {
            var slot = PrimaryLayers[i];
            EmitLayer(
                actor, reading, owned, slot,
                AnimationSlots.DisplayName(slot), alwaysShow: false);
        }
        return EndRows();
    }

    private UiChildren AdvancedSlotRows(
        ActorId actor,
        ActorAnimationReading reading,
        AnimationOverrides owned)
    {
        BeginRows();
        for (int i = 0; i < AdvancedLayers.Length; i++)
        {
            var slot = AdvancedLayers[i];
            EmitLayer(
                actor, reading, owned, slot,
                AnimationSlots.DisplayName(slot), alwaysShow: true);
        }
        return EndRows();
    }

    private UiChildren AdvancedControlRows(
        ActorId actor, ActorAnimationReading reading)
    {
        var controls = AdvancedControls(reading);
        if (controls.Count == 0)
            return Crystarium.FormStatus("No animation controls.");

        BeginRows();
        for (int i = 0; i < controls.Count; i++)
        {
            var control = controls[i];
            var ui = ScrubFor(control.Id);
            EmitScrub(actor, reading, ui.Label, control, ui, loop: null);
        }
        return EndRows();
    }

    private UiChildren FaceRows(
        ActorHandlers handlers, ActorAnimationReading reading)
    {
        ActorId actor = handlers.Actor;
        ushort held = _animation.HeldExpressionFor(actor) ?? 0;
        ushort facial = held != 0
            ? held
            : reading.TimelineFor(AnimationSlot.Facial);
        handlers.Held = held;
        handlers.Facial = facial;

        return
        [
            Crystarium.FormPickerActions(
                "Expression",
                NameFor(facial, "Choose expression…"),
                handlers.OpenExpression,
                _expressionTrigger,
                [
                    new Button
                    {
                        Label = "Preview",
                        Dense = true,
                        OnClick = handlers.PreviewExpression,
                        Disabled = facial == 0,
                        Help = "Replay the held expression from its start",
                    },
                    new Button
                    {
                        Label = "Release",
                        Dense = true,
                        OnClick = handlers.ReleaseExpression,
                        Disabled = held == 0,
                        Help = "Let the face return to the base animation",
                    },
                    new Button
                    {
                        Label = "Apply to face",
                        Dense = true,
                        OnClick = handlers.ApplyToFace,
                        Disabled = _facialCapture.IsPending,
                        Help = "Keep this face as one undoable pose edit",
                    },
                ]),
            Crystarium.FormPickerActions(
                "Lips",
                NameFor(reading.LipsOverride, "Choose speech…"),
                handlers.OpenLips,
                _lipsTrigger,
                new Button
                {
                    Label = "None",
                    Dense = true,
                    OnClick = handlers.ClearLips,
                    Disabled = reading.LipsOverride == 0,
                    Help = "Restore the incoming lip animation",
                }),
        ];
    }

    // ── row emitters ─────────────────────────────────────────────────────

    private void EmitLayer(
        ActorId actor,
        ActorAnimationReading reading,
        AnimationOverrides owned,
        AnimationSlot slot,
        string label,
        bool alwaysShow)
    {
        var ui = SlotFor(slot, label);
        ui.Actor = actor;

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
        bool paused = owned.SlotSpeeds.TryGetValue(
            slot, out var ownedSpeed) && ownedSpeed == 0f;

        ui.Timeline = timeline;
        ui.Paused = paused;
        ui.HasOwnedSpeed = hasOwnedSpeed;

        AddRow(Crystarium.FormPickerActions(
            label,
            active ? NameFor(timeline, "Choose…") : "Add layer…",
            ui.Open,
            ui.Trigger,
            compactEmpty ? UiChildren.Empty : LayerActions(ui, live),
            triggerHelp: ui.PickerHelp));

        if (!compactEmpty)
        {
            float speed = owned.SlotSpeeds.TryGetValue(
                slot, out var overrideSpeed)
                ? overrideSpeed
                : reading.SpeedFor(slot);
            AddRow(Crystarium.FormNumericSlider(
                ui.SpeedLabel,
                speed,
                0f,
                2f,
                ui.SetSpeed,
                ui.SpeedWell,
                0.005f,
                marks: UnitMarks,
                help: ui.SpeedHelp));
        }

        if (slot is AnimationSlot.Base or AnimationSlot.UpperBody)
        {
            var control = _animation.FindSlotControl(actor, slot)
                ?? new ScrubControlReading(
                    new ScrubControlId(-1, (int)slot),
                    0f,
                    0f,
                    0f);
            ui.LoopTimeline = timeline;
            EmitScrub(actor, reading, label, control, ui.Scrub, ui);
        }
    }

    private static UiChildren LayerActions(SlotUi ui, ushort live)
    {
        if (live == 0)
        {
            return
            [
                new Button
                {
                    Label = "Replay",
                    Dense = true,
                    OnClick = ui.Replay,
                    Disabled = ui.Timeline == 0,
                    Help = "Play this animation again",
                },
                new Button
                {
                    Label = "Reset",
                    Dense = true,
                    OnClick = ui.ResetSpeed,
                    Disabled = !ui.HasOwnedSpeed,
                    Help = "Hand this layer's speed back to the game",
                },
            ];
        }
        return
        [
            new Button
            {
                Label = ui.Paused ? "Play" : "Pause",
                Dense = true,
                OnClick = ui.PlayPause,
                Help = "Hold or release only this layer",
            },
            new Button
            {
                Label = "Reset",
                Dense = true,
                OnClick = ui.ResetSpeed,
                Disabled = !ui.HasOwnedSpeed,
                Help = "Hand this layer's speed back to the game",
            },
        ];
    }

    private void EmitScrub(
        ActorId actor,
        ActorAnimationReading reading,
        string label,
        ScrubControlReading control,
        ScrubUi ui,
        SlotUi? loop)
    {
        bool scrubbable = control.Duration > 0f;
        float duration = MathF.Max(control.Duration, 0.0001f);

        // The scrub handlers are built once and dispatch against these, so the
        // gesture always addresses the control the row is SHOWING.
        ui.Actor = actor;
        ui.Id = control.Id;
        ui.Duration = duration;
        ui.Controls = reading.Controls;

        AddRow(Crystarium.FormNumericSlider(
            label,
            control.Time,
            0f,
            duration,
            ui.Changed,
            ui.Well,
            0.01f,
            onBegin: ui.Begin,
            onCommit: ui.Commit,
            help: scrubbable
                ? $"Animation time / {control.Duration:0.00}"
                : "No active animation control",
            disabled: !scrubbable));

        if (loop is { } slotUi)
        {
            bool looped = _animation.OverridesFor(actor)
                .LoopedSlots.ContainsKey(slotUi.Slot);
            AddRow(Crystarium.FormSwitch(
                slotUi.LoopLabel,
                looped,
                slotUi.SetLoop,
                help: "Play this layer's animation again when it ends"));
        }
    }

    // ── row scratch ──────────────────────────────────────────────────────

    private void BeginRows() => _rowCount = 0;

    private void AddRow(UiNode node)
    {
        if (_rowCount == _rows.Length)
            Array.Resize(ref _rows, _rowCount * 2);
        _rows[_rowCount++] = node;
    }

    private UiChildren EndRows() =>
        UiChildren.Create(_rows.AsSpan(0, _rowCount));

    // ── retained holders ─────────────────────────────────────────────────

    private SlotUi SlotFor(AnimationSlot slot, string label)
    {
        if (_slotUi.TryGetValue(slot, out var existing))
            return existing;
        var created = new SlotUi(this, slot, label);
        _slotUi[slot] = created;
        return created;
    }

    private ScrubUi ScrubFor(ScrubControlId id)
    {
        if (_scrubUi.TryGetValue(id, out var existing))
            return existing;
        var created = new ScrubUi(this, id.ToString());
        _scrubUi[id] = created;
        return created;
    }

    /// <summary>
    /// One SLOT's retained UI: the two native islands the rows bind, the
    /// scrub holder, and every callback the rows name. The handlers dispatch
    /// against the mutable readings written during the build, so a slot's
    /// delegates are allocated once for the pane's life rather than per frame
    /// or per actor.
    /// </summary>
    private sealed class SlotUi
    {
        internal readonly PickerTriggerState Trigger = new();
        internal readonly NumericWellState SpeedWell = new();
        internal readonly ScrubUi Scrub;
        internal readonly AnimationSlot Slot;
        internal readonly string Label;
        internal readonly string SpeedLabel;
        internal readonly string LoopLabel;
        internal readonly string PickerHelp;
        internal readonly string SpeedHelp;

        // Written by the build, read at dispatch.
        internal ActorId Actor;
        internal ushort Timeline;
        internal ushort LoopTimeline;
        internal bool Paused;
        internal bool HasOwnedSpeed;

        internal readonly Action Open;
        internal readonly Action Replay;
        internal readonly Action PlayPause;
        internal readonly Action ResetSpeed;
        internal readonly Action<float> SetSpeed;
        internal readonly Action<bool> SetLoop;

        internal SlotUi(AnimationPane pane, AnimationSlot slot, string label)
        {
            Slot = slot;
            Label = label;
            SpeedLabel = $"{label} speed";
            LoopLabel = $"{label} loop";
            string lower = label.ToLowerInvariant();
            PickerHelp = $"Choose an animation for the {lower} layer";
            SpeedHelp = $"Playback speed for the {lower} layer";
            Scrub = new ScrubUi(pane, label);

            Open = () =>
            {
                pane._pickActor = Actor;
                pane._pickSlot = Slot;
                pane._picker.Open(
                    AnimationPickTarget.Slot,
                    Slot,
                    Slot,
                    $"{Label} layer");
            };
            Replay = () => pane.Report(
                pane._animation.Blend(Actor, Timeline), Label);
            PlayPause = () => pane.Report(
                Paused
                    ? pane._animation.ClearSlotSpeed(Actor, Slot)
                    : pane._animation.SetSlotSpeed(Actor, Slot, 0f),
                "Layer playback");
            ResetSpeed = () => pane.Report(
                pane._animation.ClearSlotSpeed(Actor, Slot), "Layer speed");
            SetSpeed = next => pane.Report(
                pane._animation.SetSlotSpeed(Actor, Slot, next),
                "Layer speed");
            SetLoop = next => pane.Report(
                pane._animation.SetSlotLoop(Actor, Slot, LoopTimeline, next),
                "Loop");
        }
    }

    /// <summary>One SCRUB row's retained well and gesture callbacks. The
    /// begin/commit pair folds into the session's single scrub lease exactly as
    /// the imperative row's local functions did.</summary>
    private sealed class ScrubUi
    {
        internal readonly NumericWellState Well = new();
        internal readonly string Label;

        // Written by the build, read at dispatch.
        internal ActorId Actor;
        internal ScrubControlId Id;
        internal float Duration = 0.0001f;
        internal IReadOnlyList<ScrubControlReading>? Controls;

        internal readonly Action<float> Changed;
        internal readonly Action Begin;
        internal readonly Action Commit;

        internal ScrubUi(AnimationPane pane, string label)
        {
            Label = label;
            Begin = () => pane.EnsureScrub(this);
            Commit = () =>
            {
                if (pane._scrub is { } held
                    && held.Actor.Equals(Actor)
                    && held.Control.Equals(Id))
                    pane.EndScrub();
            };
            Changed = next =>
            {
                pane.EnsureScrub(this);
                pane.Report(
                    pane._animation.UpdateScrub(
                        Math.Clamp(next, 0f, Duration)),
                    "Scrub");
            };
        }
    }

    private void EnsureScrub(ScrubUi ui)
    {
        if (_scrub is { } held
            && held.Actor.Equals(ui.Actor)
            && held.Control.Equals(ui.Id))
            return;
        Report(_animation.BeginScrub(ui.Actor, ui.Id), "Scrub");
        _scrub = (ui.Actor, ui.Id);
        _scrubFrozenControls = ui.Controls;
    }

    /// <summary>
    /// ONE actor's fixed callbacks, constructed once and reused for every frame
    /// that actor stays selected. Each handler closes over the actor, so
    /// building them inside the tree would allocate a dozen delegates per
    /// frame; the holder is therefore rebuilt only when <see cref="TargetActor"/>
    /// reports a different <see cref="ActorId"/>. The per-frame readings the
    /// handlers need are written onto the holder during the build.
    /// </summary>
    private sealed class ActorHandlers
    {
        internal readonly ActorId Actor;

        // Written by the build, read at dispatch.
        internal ushort BaseTimeline;
        internal bool BasePaused;
        internal AnimationStance Stance;
        internal AnimationStance PoseFamily;
        internal int Pose;
        internal ushort Facial;
        internal ushort Held;

        internal readonly Action OpenBase;
        internal readonly Action PlayPause;
        internal readonly Action ReplayBase;
        internal readonly Action RestoreActor;
        internal readonly Action<float> SetSpeed;
        internal readonly Action ResetSpeed;

        internal readonly Action<int> PickStance;
        internal readonly Action PreviousPose;
        internal readonly Action NextPose;
        internal readonly Action<bool> SetWeaponDrawn;
        internal readonly Action<bool> SetPositionLock;

        internal readonly Action OpenExpression;
        internal readonly Action PreviewExpression;
        internal readonly Action ReleaseExpression;
        internal readonly Action ApplyToFace;
        internal readonly Action OpenLips;
        internal readonly Action ClearLips;

        internal ActorHandlers(AnimationPane pane, ActorId actor)
        {
            Actor = actor;

            OpenBase = () =>
            {
                pane._pickActor = actor;
                pane._pickSlot = AnimationSlot.Base;
                pane._picker.Open(
                    AnimationPickTarget.Base,
                    AnimationSlot.Base,
                    restrictToSlot: AnimationSlot.Base,
                    caption: "Animation");
            };
            PlayPause = () => pane.Report(
                BasePaused
                    ? pane._animation.Resume(actor)
                    : pane._animation.Pause(actor),
                "Playback");
            ReplayBase = () => pane.Report(
                pane._animation.Blend(actor, BaseTimeline), "Replay");
            RestoreActor = () => pane.Report(
                pane._animation.ResetActor(actor), "Restore");
            SetSpeed = next => pane.Report(
                pane._animation.SetSpeed(actor, next), "Speed");
            ResetSpeed = () => pane.Report(
                pane._animation.ClearSpeed(actor), "Speed");

            PickStance = picked =>
            {
                int pose = StanceValues[picked] == Stance ? Pose : 0;
                pane.Report(
                    pane._animation.SetStance(
                        actor, StanceValues[picked], pose),
                    "Stance");
            };
            PreviousPose = () => pane.Report(
                pane._animation.SetStance(actor, PoseFamily, Pose - 1),
                "Pose");
            NextPose = () => pane.Report(
                pane._animation.SetStance(actor, PoseFamily, Pose + 1),
                "Pose");
            SetWeaponDrawn = next => pane.Report(
                pane._animation.SetWeaponDrawn(actor, next), "Weapon");
            SetPositionLock = next => pane.Report(
                pane._animation.SetPositionLock(actor, next),
                "Position lock");

            OpenExpression = () =>
            {
                pane._pickActor = actor;
                pane._pickSlot = AnimationSlot.Facial;
                pane._picker.Open(
                    AnimationPickTarget.Expression,
                    AnimationSlot.Facial,
                    AnimationSlot.Facial,
                    "Expression",
                    AnimationKind.Expression);
            };
            PreviewExpression = () => pane.Report(
                Held != 0
                    ? pane._animation.HoldExpression(actor, Held)
                    : pane._animation.Blend(actor, Facial),
                "Expression");
            ReleaseExpression = () => pane.Report(
                pane._animation.ReleaseExpression(actor), "Expression");
            ApplyToFace = () =>
            {
                var descriptor = pane.Describe(actor);
                pane._status = descriptor == null
                    ? "Apply to face: actor is no longer in the scene."
                    : pane._facialCapture.Begin(actor, descriptor)
                        is { Success: false } failed
                        ? $"Apply to face: {failed.Detail}"
                        : string.Empty;
            };

            OpenLips = () =>
            {
                pane._pickActor = actor;
                pane._pickSlot = AnimationSlot.Lips;
                pane._picker.Open(
                    AnimationPickTarget.Lips,
                    AnimationSlot.Lips,
                    AnimationSlot.Lips,
                    "Lips",
                    entries: pane.LipsEntries());
            };
            ClearLips = () => pane.Report(
                pane._animation.SetLips(actor, 0), "Lips");
        }
    }

    // ── unchanged helpers ────────────────────────────────────────────────

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

    private void DrawSceneMenu()
    {
        if (_sceneMenuRequested)
        {
            _sceneMenuRequested = false;
            LegacyCrystarium.FloatingMenu.Open(
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
        switch (LegacyCrystarium.FloatingMenu.Draw("##anim-scene-menu"))
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

    /// <summary>The lips range is fixed and the catalogue is static once
    /// loaded, so the list is built at most once — and kept only when the
    /// catalogue actually answered, so an early call cannot freeze the
    /// fallback names.</summary>
    private IReadOnlyList<TimelineEntry> LipsEntries()
    {
        if (_lipsEntries is { } cached)
            return cached;
        var entries = new List<TimelineEntry>();
        bool resolved = false;
        for (ushort id = AnimationTimelines.FirstLips;
             id <= AnimationTimelines.LastLips;
             id++)
        {
            if (_catalog.Find(id) is { } known)
            {
                resolved = true;
                entries.Add(known);
                continue;
            }
            entries.Add(new TimelineEntry(
                id,
                $"Speech {id - AnimationTimelines.FirstLips + 1}",
                AnimationKind.RawTimeline,
                AnimationSlot.Lips));
        }
        if (resolved)
            _lipsEntries = entries;
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
                // The row that opened the picker is the row that reads the
                // memory: the write key is the REQUESTED slot, not whichever
                // slot the chosen entry declares.
                _layerPicks[(actor, _pickSlot ?? pick.Entry.Slot)] = timeline;
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
