using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using Poser.Application.Animation;
using Poser.Application.Scene;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.UI;

/// <summary>Renders actor animation controls and catalog pickers.</summary>
public sealed class AnimationPane
{
    private readonly AnimationSession _animation;
    private readonly AnimationCatalog _catalog;

    // The expression workspace may open before another catalog row.
    private readonly Game.Animation.AnimationCatalogLoader _catalogLoader;
    private readonly AnimationSceneActions _sceneActions;
    private readonly Game.Animation.FacialPoseCapture _facialCapture;
    private readonly SceneSession _scene;

    // All picker rows share one open feed.
    private readonly Crystarium.SearchPicker<TimelineEntry> _picker =
        new("animation");

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
    // Commands report through the shared notification sink.
    private readonly UserNotices _notices;
    private int _pickerFrame = -1;
    private int _expressionReadingFrame = -1;
    private ActorId? _expressionReadingActor;
    private ActorAnimationReading _expressionReading =
        ActorAnimationReading.Empty;
    private bool _sceneMenuRequested;

    // The batch menu stays anchored to its trigger.
    private System.Numerics.Vector2 _sceneMenuAnchor;

    // A picker keeps its actor and feed until it closes.
    private ActorId? _pickActor;
    private TimelineFeed? _openFeed;

    // Each row keeps one memoized catalog feed.
    private readonly TimelineFeed _baseFeed;
    private readonly TimelineFeed _expressionFeed;
    private readonly TimelineFeed _lipsFeed;
    private readonly Dictionary<AnimationSlot, TimelineFeed> _slotFeeds = new();

    // 0 = all, 1 = sheathed, 2 = drawn.
    private int _weaponFilter;

    private readonly Action<int> _setWeaponFilter;

    private readonly GameIconResolver _icons;

    // Timeline labels are cached for catalog badges.
    private readonly Dictionary<uint, string> _idText = new();

    // The kind keeps duplicate timeline rows distinct.
    private readonly Dictionary<long, string> _rowKeys = new();

    private static readonly Func<TimelineEntry, string> TimelineName =
        static entry => entry.Name;

    // Raw timelines use a kind-specific fallback glyph.
    private static readonly Func<TimelineEntry, TablerIcon?> TimelineGlyph =
        static entry => entry.Kind switch
        {
            AnimationKind.Emote or AnimationKind.Expression =>
                TablerIcon.MoodSmile,
            AnimationKind.Action => TablerIcon.Bolt,
            _ => TablerIcon.Movie,
        };

    private readonly Func<TimelineEntry, string> _timelineKey;
    private readonly Func<TimelineEntry, nint> _timelineTexture;

    // The lips catalog is built once after loading.
    private IReadOnlyList<TimelineEntry>? _lipsEntries;

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

    private static readonly string[] KindLabels =
        ["All", "Emote", "Action", "Expr", "Raw"];

    private static readonly AnimationKind?[] KindValues =
    [
        null, AnimationKind.Emote, AnimationKind.Action,
        AnimationKind.Expression, AnimationKind.RawTimeline,
    ];

    private static readonly string[] WeaponLabels = ["All", "Sheathed", "Drawn"];

    public AnimationPane(
        AnimationSession animation,
        AnimationCatalog catalog,
        Game.Animation.AnimationCatalogLoader catalogLoader,
        AnimationSceneActions sceneActions,
        Game.Animation.FacialPoseCapture facialCapture,
        ITextureProvider textures,
        SceneSession scene,
        UserNotices notices)
    {
        _notices = notices;
        _animation = animation;
        _catalog = catalog;
        _catalogLoader = catalogLoader;
        _sceneActions = sceneActions;
        _facialCapture = facialCapture;
        _icons = new GameIconResolver(textures);
        _scene = scene;
        _timelineKey = RowKey;
        _timelineTexture = entry => _icons.Resolve(entry.Icon);
        _setWeaponFilter = chosen => _weaponFilter = chosen;
        _baseFeed = new TimelineFeed(
            this, "animation", AnimationPickTarget.Base, AnimationSlot.Base,
            AnimationSlot.Base, seed: null, weaponAware: true, entries: null);
        _expressionFeed = new TimelineFeed(
            this, "expression", AnimationPickTarget.Expression,
            AnimationSlot.Facial, AnimationSlot.Facial,
            AnimationKind.Expression, weaponAware: false, entries: null);
        _lipsFeed = new TimelineFeed(
            this, "lips", AnimationPickTarget.Lips, AnimationSlot.Lips,
            AnimationSlot.Lips, seed: null, weaponAware: false,
            entries: LipsEntries);
    }

    public void Draw(Vector2 origin, Vector2 size)
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
            page.Section(
                "GENERAL",
                _openGeneral,
                next => _openGeneral = next,
                form => DrawPlayback(form, actor, reading, owned),
                divider: false);
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
                "LIPS",
                _openFace,
                next => _openFace = next,
                form => DrawLips(form, actor, reading));
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

        DrawPicker();
        DrawSceneMenu();
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
            NameFor(current, "Choose"),
            () => OpenPicker(_baseFeed, actor, current),
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
                        ? "Resume this actor from the frame it stopped on"
                        : "Freeze this actor on the current frame");
                actions.Button(
                    "Replay",
                    () =>
                    {
                        var result = _animation.Replay(
                            actor, current, out bool resumed);
                        Report(result, "Replay");
                        if (result.Success && resumed)
                            _notices.Done("Replay resumed paused playback.");
                    },
                    disabled: current == 0,
                    help: "Play this actor's animation again from the "
                        + "start; a paused actor resumes playing");
                actions.Button(
                    "Restore",
                    () => Report(
                        _animation.ResetActor(actor), "Restore"),
                    help: "Undo every animation change Poser made to "
                        + "this actor");
            },
            help: "Choose the animation this actor plays");

        if (_catalog.Find(current) is { IsLoop: true })
            form.Status("Native loop.");

        float speed = owned.OverallSpeed ?? reading.OverallSpeed;
        form.NumericSlider(
            "Speed",
            speed,
            -5f,
            10f,
            next => Report(_animation.SetSpeed(actor, next), "Speed"),
            perPixel: 0.01f,
            marks: SpeedMarks,
            help: "Set how fast every animation on this actor plays; "
                + "0 freezes it");
        form.Actions("Playback", actions =>
        {
            actions.Button(
                "Reset speed",
                () => Report(_animation.ClearSpeed(actor), "Speed"),
                help: "Give this actor's playback speed back to the game");
            actions.Button(
                "All actors",
                () =>
                {
                    _sceneMenuAnchor = Crystarium.ButtonSeat;
                    _sceneMenuRequested = true;
                },
                help: "Freeze, resume, replay or restore every actor in "
                    + "the scene");
            actions.Button(
                "Start slot probe",
                () => StartSlotProbe(actor),
                help: "Record bounded slot observations in dalamud.log");
            actions.Button(
                "Stop slot probe",
                () => StopSlotProbe(actor),
                help: "End the active slot probe");
        });
    }

    private void StartSlotProbe(ActorId actor)
    {
        var result = _animation.StartSlotProbe(actor);
        if (!result.Success)
        {
            Report(result, "Slot probe");
            return;
        }
        _notices.Done($"Slot probe {result.Detail} started. See dalamud.log.");
    }

    private void StopSlotProbe(ActorId actor)
    {
        var result = _animation.StopSlotProbe(actor);
        if (result.Success)
            _notices.Done("Slot probe ended. See dalamud.log.");
        else
            Report(result, "Slot probe");
    }

    private void DrawStance(
        Crystarium.FormScope form,
        ActorId actor,
        ActorAnimationReading reading)
    {
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

        form.Pair(
            "Stance",
            cell =>
            {
                var theme = Crystarium.ActiveTheme;
                ImGui.SetCursorScreenPos(
                    cell.Center(theme.Controls.WorkspaceHeight));
                Crystarium.ActionDropdown(
                    "##anim-stance",
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
                    cell.Constrain(ControlStyle.Workspace),
                    disabled: !supported,
                    help: supported
                        ? "Put the actor into this stance; this clears the "
                            + "animation you chose"
                        : "Stance control is unavailable; Poser could not "
                            + "find the game function it needs");
            },
            $"Pose {reading.Pose}",
            cell => PoseStepper(
                cell,
                poseDisabled,
                () => Report(
                    _animation.SetStance(
                        actor, poseFamily, reading.Pose - 1),
                    "Pose"),
                () => Report(
                    _animation.SetStance(
                        actor, poseFamily, reading.Pose + 1),
                    "Pose")));

        form.Pair(
            "Weapon",
            cell =>
            {
                ImGui.SetCursorScreenPos(cell.Center(
                    Crystarium.ActiveTheme.Controls.SwitchHeight));
                Crystarium.Switch(
                    "##anim-weapon-drawn",
                    reading.WeaponDrawn,
                    next => Report(
                        _animation.SetWeaponDrawn(actor, next), "Weapon"),
                    cell.Constrain());
            },
            "Lock position",
            cell =>
            {
                ImGui.SetCursorScreenPos(cell.Center(
                    Crystarium.ActiveTheme.Controls.SwitchHeight));
                Crystarium.Switch(
                    "##anim-position-lock",
                    owned.PositionLock,
                    next => Report(
                        _animation.SetPositionLock(actor, next),
                        "Position lock"),
                    cell.Constrain());
            });
    }

    private static void PoseStepper(
        Crystarium.FormPairCell cell,
        bool disabled,
        Action onPrevious,
        Action onNext)
    {
        var theme = Crystarium.ActiveTheme;
        float gap = theme.Page.ActionGap * cell.Scale;
        float width = MathF.Max(
            1f, (cell.Width - gap) * 0.5f / cell.Scale);
        var style = ControlStyle.Workspace with
        {
            Width = UiWidth.Fixed(width),
        };
        var top = cell.Center(theme.Controls.WorkspaceHeight);
        ImGui.SetCursorScreenPos(top);
        Crystarium.Button(
            "Previous",
            onPrevious,
            style: style,
            disabled: disabled,
            help: "Step back one pose in this stance; the list wraps",
            id: "##anim-pose-previous");
        ImGui.SetCursorScreenPos(
            new Vector2(top.X + width * cell.Scale + gap, top.Y));
        Crystarium.Button(
            "Next",
            onNext,
            style: style,
            disabled: disabled,
            help: "Step forward one pose in this stance; the list wraps",
            id: "##anim-pose-next");
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
        string lower = label.ToLowerInvariant();

        form.Picker(
            label,
            active ? NameFor(timeline, "Choose") : "Add layer",
            () => OpenPicker(SlotFeed(captured), actor, timeline),
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
                            help: "Play this layer's animation again from "
                                + "the start");
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
                            help: "Freeze just this layer, or let it play "
                                + "again");
                    }
                    actions.Button(
                        "Reset",
                        () => Report(
                            _animation.ClearSlotSpeed(actor, captured),
                            "Layer speed"),
                        disabled: !hasOwnedSpeed,
                        help: "Give this layer's speed back to the game");
                },
            help: $"Choose an animation for the {lower} layer");

        if (slot == AnimationSlot.Base)
            form.Switch(
                "Full body repeat",
                _animation.LoopWantedFor(actor, AnimationSlot.Base),
                next => Report(
                    _animation.SetSlotLoop(actor, AnimationSlot.Base, 0, next),
                    "Full body repeat"),
                help: "Repeat the selected full-body animation");

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
                marks: UnitMarks,
                help: $"Set how fast the {lower} layer plays");
        }

        if (slot is AnimationSlot.UpperBody or AnimationSlot.Facial or AnimationSlot.Additive)
            form.Status("Repeat is unavailable until this layer has a verified replay route.");
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
        ScrubControlReading control)
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
                        actor,
                        Math.Clamp(next, 0f, duration)),
                    "Scrub");
            },
            perPixel: 0.01f,
            disabled: !scrubbable,
            help: scrubbable
                ? "Drag to move through the animation, "
                    + $"{control.Duration:0.00}s long; this pauses the actor"
                : "No animation is playing here",
            onBegin: EnsureScrub,
            onCommit: Commit);

    }

    /// <summary>Draws the expression controls for the face workspace.</summary>
    public void DrawExpressionRow(Crystarium.FormScope form, ActorId actor)
    {
        if (!_animation.IsSupported(actor))
        {
            form.Status("This actor does not support expressions.");
            return;
        }
        _catalogLoader.EnsureLoaded();
        DrawExpression(form, actor, ExpressionReading(actor));
    }

    // The workspace may redraw within one frame.
    private ActorAnimationReading ExpressionReading(ActorId actor)
    {
        int frame = ImGui.GetFrameCount();
        if (_expressionReadingFrame == frame && _expressionReadingActor == actor)
            return _expressionReading;
        _expressionReadingFrame = frame;
        _expressionReadingActor = actor;
        return _expressionReading =
            _animation.Read(actor) ?? ActorAnimationReading.Empty;
    }

    /// <summary>Draws this pane's open picker.</summary>
    public void DrawExpressionPicker() => DrawPicker();

    private void DrawLips(
        Crystarium.FormScope form,
        ActorId actor,
        ActorAnimationReading reading)
    {
        form.Picker(
            "Lips",
            NameFor(reading.LipsOverride, "Choose speech"),
            () => OpenPicker(_lipsFeed, actor, reading.LipsOverride),
            actions => actions.Button(
                "None",
                () => Report(_animation.SetLips(actor, 0), "Lips"),
                disabled: reading.LipsOverride == 0,
                help: "Put back the lip animation the actor had before"),
            help: "Choose the speech animation this actor's lips play");
    }

    private void DrawExpression(
        Crystarium.FormScope form,
        ActorId actor,
        ActorAnimationReading reading)
    {
        if (_facialCapture.ReceiptFor(actor) is { } receipt)
            form.Label($"Bake: {FacialReceiptText(receipt)}");
        ushort held = _animation.HeldExpressionFor(actor) ?? 0;
        ushort facial = held != 0
            ? held
            : reading.TimelineFor(AnimationSlot.Facial);
        form.Picker(
            "Expression",
            NameFor(facial, "Choose expression"),
            () => OpenPicker(_expressionFeed, actor, facial),
            actions =>
            {
                actions.Button(
                    "Preview",
                    () => ReportExpression(
                        held != 0
                            ? _animation.HoldExpression(actor, held)
                            : _animation.Blend(actor, facial),
                        "Expression"),
                    disabled: facial == 0,
                    help: "Play this expression on the face again — a look "
                        + "held by the animation, which Release takes back");
                actions.Button(
                    "Release",
                    () => ReportExpression(
                        _animation.ReleaseExpression(actor), "Expression"),
                    disabled: held == 0,
                    help: "Drop the previewed expression so the face follows "
                        + "the animation again");
                actions.Button(
                    "Bake expression",
                    () =>
                    {
                        var descriptor = Describe(actor);
                        if (descriptor == null)
                            _notices.Refused(
                                "Bake expression: actor is no longer in "
                                + "the scene.");
                        else if (_facialCapture.Begin(actor, descriptor)
                            is { Success: false } failed)
                            _notices.Failed(
                                $"Bake expression: {failed.Detail}");
                    },
                    disabled: _facialCapture.IsPending,
                    help: "Write the previewed face into the POSE as one "
                        + "undoable edit — it stays after the preview ends");
            },
            help: "Choose an expression to hold on this actor's face");
    }


    // The picker opens for the row that reserved the trigger.
    private void OpenPicker(TimelineFeed feed, ActorId actor, ushort current)
    {
        _pickActor = actor;
        _openFeed = feed;
        feed.Seed();
        _picker.Open(
            feed.Owner,
            Array.Empty<TimelineEntry>(),
            TimelineName,
            _timelineKey,
            feed.SelectedKey(current),
            feed.LoadError,
            PickerOptionsFor(feed));
    }

    private void DrawPicker()
    {
        int frame = ImGui.GetFrameCount();
        if (_pickerFrame == frame)
            return;
        _pickerFrame = frame;
        if (_openFeed is not { } feed)
            return;
        _picker.Update(PickerOptionsFor(feed));
        if (_picker.Draw() is { } chosen && _pickActor is { } actor)
            Apply(actor, new AnimationPick(
                chosen.Item, feed.Target, feed.Slot));
    }

    private PickerOptions<TimelineEntry> PickerOptionsFor(TimelineFeed feed) =>
        new()
        {
            Query = feed.Results,
            Texture = _timelineTexture,
            Glyph = TimelineGlyph,
            Badge = feed.Badge,
            Strip = feed.KindStrip,
            SecondStrip = feed.WeaponStrip,
            Width = Crystarium.ActiveTheme.Picker.WideWidth,
        };

    private TimelineFeed SlotFeed(AnimationSlot slot)
    {
        if (_slotFeeds.TryGetValue(slot, out var existing))
            return existing;
        var created = new TimelineFeed(
            this, $"layer-{slot}", AnimationPickTarget.Slot, slot, slot,
            seed: null, weaponAware: false, entries: null);
        _slotFeeds[slot] = created;
        return created;
    }

    private string IdText(uint id)
    {
        if (_idText.TryGetValue(id, out var text))
            return text;
        text = id.ToString(CultureInfo.InvariantCulture);
        _idText[id] = text;
        return text;
    }

    // The kind and slot keep catalog row IDs unique.
    private string RowKey(TimelineEntry entry)
    {
        long identity = ((long)entry.TimelineId << 16)
            | ((long)(int)entry.Kind << 8)
            | (long)(int)entry.Slot;
        if (_rowKeys.TryGetValue(identity, out var text))
            return text;
        text = identity.ToString(CultureInfo.InvariantCulture);
        _rowKeys[identity] = text;
        return text;
    }

    private sealed class TimelineFeed
    {
        private readonly AnimationPane _pane;
        private readonly AnimationSlot? _slotFilter;
        private readonly AnimationKind? _seed;
        private readonly bool _weaponAware;
        private readonly Func<IReadOnlyList<TimelineEntry>>? _entries;

        private readonly AnimationKind?[] _kinds;
        private readonly string[] _kindLabels;
        private int _kindIndex;

        // Cache keys cover every query input.
        private string? _memoQuery;
        private int _memoKind = -1;
        private int _memoWeapon = -1;
        private bool _memoLoaded;
        private IReadOnlyList<TimelineEntry> _memo = Array.Empty<TimelineEntry>();

        internal readonly string Owner;
        internal readonly AnimationPickTarget Target;
        internal readonly AnimationSlot Slot;

        internal readonly Func<string, IReadOnlyList<TimelineEntry>> Results;
        internal readonly Func<TimelineEntry, string?> Badge;
        private readonly Action<int> _setKind;

        internal TimelineFeed(
            AnimationPane pane,
            string owner,
            AnimationPickTarget target,
            AnimationSlot slot,
            AnimationSlot? slotFilter,
            AnimationKind? seed,
            bool weaponAware,
            Func<IReadOnlyList<TimelineEntry>>? entries)
        {
            _pane = pane;
            Owner = owner;
            Target = target;
            Slot = slot;
            _slotFilter = slotFilter;
            _seed = seed;
            _weaponAware = weaponAware;
            _entries = entries;

            var excluded = AnimationCatalog.ExcludedKinds(slotFilter);
            var kinds = new List<AnimationKind?>();
            var labels = new List<string>();
            for (int i = 0; i < KindValues.Length; i++)
            {
                bool blocked = false;
                if (KindValues[i] is { } concrete)
                    foreach (var kind in excluded)
                        if (kind == concrete)
                            blocked = true;
                if (blocked)
                    continue;
                kinds.Add(KindValues[i]);
                labels.Add(KindLabels[i]);
            }

            _kinds = kinds.ToArray();
            _kindLabels = labels.ToArray();
            Results = Compute;
            Badge = Metadata;
            _setKind = chosen => _kindIndex = chosen;
            Seed();
        }

        internal PickerStrip? KindStrip =>
            _entries != null || _kindLabels.Length <= 1
                ? null
                : new PickerStrip(_kindLabels, _kindIndex, _setKind);

        internal PickerStrip? WeaponStrip =>
            _weaponAware
                ? new PickerStrip(
                    WeaponLabels, _pane._weaponFilter, _pane._setWeaponFilter)
                : null;

        internal string? LoadError =>
            _entries == null && !_pane._catalog.IsLoaded
                ? "Building animation catalog"
                : null;

        internal void Seed()
        {
            AnimationKind? start = _seed ?? AnimationCatalog.BestKind(_slotFilter);
            _kindIndex = Array.IndexOf(_kinds, start);
            if (_kindIndex < 0)
                _kindIndex = 0;
        }

        internal string? SelectedKey(ushort timeline)
        {
            if (timeline == 0)
                return null;
            if (_entries is { } explicitEntries)
            {
                foreach (var entry in explicitEntries())
                    if (entry.TimelineId == timeline)
                        return _pane.RowKey(entry);
                return null;
            }
            return _pane._catalog.Find(timeline) is { } known
                ? _pane.RowKey(known)
                : null;
        }

        private IReadOnlyList<TimelineEntry> Compute(string search)
        {
            int weapon = _pane._weaponFilter;
            bool loaded = _pane._catalog.IsLoaded;
            if (_memoQuery == search
                && _memoKind == _kindIndex
                && _memoWeapon == weapon
                && _memoLoaded == loaded)
                return _memo;
            _memoQuery = search;
            _memoKind = _kindIndex;
            _memoWeapon = weapon;
            _memoLoaded = loaded;
            _memo = Query(search, weapon);
            return _memo;
        }

        private IReadOnlyList<TimelineEntry> Query(string search, int weapon)
        {
            if (_entries is { } explicitEntries)
            {
                var entries = explicitEntries();
                if (string.IsNullOrWhiteSpace(search))
                    return entries;
                var filtered = new List<TimelineEntry>();
                foreach (var entry in entries)
                    if (entry.Name.IndexOf(
                            search, StringComparison.OrdinalIgnoreCase) >= 0
                        || entry.TimelineId.ToString(CultureInfo.InvariantCulture)
                            == search.Trim())
                        filtered.Add(entry);
                return filtered;
            }

            var found = _pane._catalog.Search(
                search, _kinds[Math.Clamp(_kindIndex, 0, _kinds.Length - 1)],
                _slotFilter, limit: 400);
            if (!_weaponAware || weapon == 0)
                return found;

            bool drawn = weapon == 2;
            var narrowed = new List<TimelineEntry>(found.Count);
            foreach (var entry in found)
                if (entry.DrawsWeapon is not { } state || state == drawn)
                    narrowed.Add(entry);
            return narrowed;
        }

        private string? Metadata(TimelineEntry entry) =>
            _slotFilter != null
                ? _pane.IdText(entry.TimelineId)
                : $"{AnimationSlots.DisplayName(entry.Slot)} · {entry.TimelineId}";
    }

    private enum AnimationPickTarget
    {
        Base,
        Slot,
        Lips,
        Expression,
    }

    private readonly record struct AnimationPick(
        TimelineEntry Entry,
        AnimationPickTarget Target,
        AnimationSlot Slot);


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
            Crystarium.FloatingMenu.Open(
                "##anim-scene-menu",
                _sceneMenuAnchor,
                [
                    new ContextMenuItem(
                        "Freeze all",
                        TablerIcon.PlayerPause),
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
            {
                var replay = _sceneActions.ReplayAll(out int resumed);
                Report(replay, "Replay all");
                if (replay.Success && resumed > 0)
                    _notices.Done(
                        $"Replay resumed {resumed} paused "
                        + $"actor{(resumed == 1 ? "" : "s")}.");
                break;
            }
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
        { Kind: SceneEntityKind.GazeTarget, Actor: { } gazeActor } =>
            gazeActor,
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

    // Cache lips only after the catalog returns entries.
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
                _layerPicks[(actor, AnimationSlot.Base)] = timeline;
                break;
            }
            case AnimationPickTarget.Slot:
            {
                var played = pick.Slot == AnimationSlot.Base
                    ? _animation.PlayEntry(actor, pick.Entry, asBase: true, playFromStart: true)
                    : _animation.Blend(actor, timeline);
                if (!played.Success)
                {
                    Report(
                        played,
                        AnimationSlots.DisplayName(pick.Slot));
                    break;
                }
                // Apply to the slot requested by the opening row.
                _layerPicks[(actor, pick.Slot)] = timeline;
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

    private void EndScrub()
    {
        _animation.EndScrub();
        _scrub = null;
        _scrubFrozenControls = null;
    }

    private void ReportExpression(AnimationResult result, string what) =>
        Report(result, what);

    private void Report(AnimationResult result, string what)
    {
        if (!result.Success)
            _notices.Failed($"{what}: {result.Detail}");
    }

    private void Report(
        AnimationSceneActions.SceneActionReport report,
        string verb)
    {
        if (!report.Success || report.Skipped.Count > 0)
            _notices.Failed(report.Summary(verb));
    }

    private static string FacialReceiptText(
        global::Poser.Application.Operations.OperationReceipt receipt) =>
        receipt.State switch
        {
            global::Poser.Application.Operations.OperationReceiptState.Pending =>
                "Pending",
            _ when string.IsNullOrWhiteSpace(receipt.Detail) =>
                receipt.State.ToString(),
            _ => $"{receipt.State}: {receipt.Detail}",
        };
}
