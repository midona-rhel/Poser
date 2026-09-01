using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using Poser.Application.Animation;
using Poser.Application.Lifecycle;
using Poser.Application.Operations;
using Poser.Application.Scene;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Game.Bindings;

namespace Poser.UI;

/// <summary>Renders actor animation controls and catalog pickers.</summary>
public sealed class AnimationPane : IDisposable
{
    private const long ExpressionRetryDelayMs = 500;

    private readonly AnimationSession _animation;
    private readonly AnimationCatalog _catalog;

    // The expression workspace may open before another catalog row.
    private readonly Game.Animation.AnimationCatalogLoader _catalogLoader;
    private readonly Game.Animation.FacialPoseCapture _facialCapture;
    private readonly IFramework _framework;
    private readonly StableBindingRegistry _bindings;
    private readonly ISessionGenerationSource _sessionGeneration;
    private readonly SceneSession _scene;

    // All picker rows share one open feed.
    private readonly Crystarium.SearchPicker<TimelineEntry> _picker =
        new("animation");

    private bool _openGeneral = true;
    private bool _openAnimationLayers = true;
    // Advanced mode is actor-local; a transition restores outgoing ownership.
    private readonly HashSet<ActorId> _advancedActors = new();
    private bool _playEmoteStart = true;
    // Commands report through the shared notification sink.
    private readonly UserNotices _notices;
    private int _pickerFrame = -1;
    // A picker keeps its actor and feed until it closes.
    private ActorId? _pickActor;
    private TimelineFeed? _openFeed;
    // General stages one catalog command; native ownership begins on Apply.
    private readonly Dictionary<ActorId, GeneralSelection> _generalSelections = new();
    // Layer Apply retains the exact catalog row selected by its picker.
    private readonly Dictionary<(ActorId Actor, AnimationSlot Slot), TimelineEntry>
        _layerSelections = new();
    // Both expression surfaces show the friendly row chosen from the catalog.
    private readonly Dictionary<ActorId, TimelineEntry> _expressionSelections = new();
    private PendingExpressionRetry? _expressionRetry;
    // A Base scrub keeps one captured control identity until release.
    private ActorId? _scrubActor;
    private ScrubControlId? _scrubControl;

    // Each row keeps one memoized catalog feed.
    private readonly TimelineFeed _baseFeed;
    private readonly TimelineFeed _generalFeed;
    private readonly TimelineFeed _expressionFeed;
    private readonly TimelineFeed _lipsFeed;
    private readonly Dictionary<AnimationSlot, TimelineFeed> _slotFeeds = new();

    // 0 = all, 1 = sheathed, 2 = drawn.
    private int _weaponFilter;

    private readonly Action<int> _setWeaponFilter;

    private readonly GameIconResolver _icons;

    // Timeline labels are cached for catalog badges.
    private readonly Dictionary<uint, string> _idText = new();

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
        AnimationSlot.Base,
        AnimationSlot.UpperBody,
        AnimationSlot.Facial,
        AnimationSlot.Additive,
        AnimationSlot.Lips,
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

    private static readonly float[] UnitMarks = [1f];

    private static readonly string[] KindLabels =
        ["Compatible", "Emotes", "Actions", "Raw"];

    private static readonly AnimationKind?[] KindValues =
    [
        null, AnimationKind.Emote, AnimationKind.Action,
        AnimationKind.RawTimeline,
    ];

    private static readonly string[] WeaponLabels = ["All", "Sheathed", "Drawn"];

    public AnimationPane(
        AnimationSession animation,
        AnimationCatalog catalog,
        Game.Animation.AnimationCatalogLoader catalogLoader,
        Game.Animation.FacialPoseCapture facialCapture,
        IFramework framework,
        StableBindingRegistry bindings,
        ISessionGenerationSource sessionGeneration,
        ITextureProvider textures,
        SceneSession scene,
        UserNotices notices,
        Game.Animation.AnimationRuntimePort probePort,
        global::Poser.Services.IActorSpawnService spawner,
        Game.Scene.SceneLifecycleHistory lifecycle,
        global::Poser.Services.IGazeService gaze,
        global::Poser.Services.IActorManager actorManager,
        Application.Integration.IIntegrationRuntimePort integrationPort)
    {
        _probePort = probePort;
        _spawner = spawner;
        _lifecycle = lifecycle;
        _gaze = gaze;
        _actorManager = actorManager;
        _integrationPort = integrationPort;
        _notices = notices;
        _animation = animation;
        _catalog = catalog;
        _catalogLoader = catalogLoader;
        _facialCapture = facialCapture;
        _framework = framework;
        _bindings = bindings;
        _sessionGeneration = sessionGeneration;
        _icons = new GameIconResolver(textures);
        _scene = scene;
        _timelineKey = RowKey;
        _timelineTexture = entry => _icons.Resolve(entry.Icon);
        _setWeaponFilter = chosen => _weaponFilter = chosen;
        _baseFeed = new TimelineFeed(
            this, "animation", AnimationPickTarget.Base, AnimationSlot.Base,
            AnimationSlot.Base, kindFilter: null, weaponAware: true, entries: null);
        _generalFeed = new TimelineFeed(
            this, "general-animation", AnimationPickTarget.General,
            AnimationSlot.Base, AnimationSlot.Base, kindFilter: null,
            weaponAware: true, entries: null, showKindStrip: true);
        _expressionFeed = new TimelineFeed(
            this, "expression", AnimationPickTarget.Expression,
            AnimationSlot.Facial, AnimationSlot.Facial,
            kindFilter: AnimationKind.Expression, weaponAware: false, entries: null);
        _lipsFeed = new TimelineFeed(
            this, "lips", AnimationPickTarget.Lips, AnimationSlot.Lips,
            AnimationSlot.Lips, kindFilter: null, weaponAware: false,
            entries: LipsEntries);
        _framework.Update += OnFrameworkUpdate;
    }

    public void Draw(Vector2 origin, Vector2 size)
    {
        PrunePaneState();
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
            var reading =
                _animation.Read(actor) ?? ActorAnimationReading.Empty;
            var owned = _animation.OverridesFor(actor);
            bool advanced = _advancedActors.Contains(actor);
            page.Section(
                "General",
                _openGeneral,
                next => _openGeneral = next,
                form => DrawGeneral(form, actor, reading, advanced),
                divider: false);
            page.Section(
                "Animation layers",
                _openAnimationLayers,
                next => _openAnimationLayers = next,
                form => DrawAnimationLayers(
                    form, actor, reading, owned, advanced));
            page.Section(
                "Debug",
                _openDebug,
                next => _openDebug = next,
                form => DebugRows(form, actor));
        });

        DrawPicker();
    }


    // ── The animation ownership probe (debug harness) ─────────────────

    private readonly Game.Animation.AnimationRuntimePort _probePort;
    private readonly global::Poser.Services.IActorSpawnService _spawner;
    private readonly Game.Scene.SceneLifecycleHistory _lifecycle;
    private readonly global::Poser.Services.IGazeService _gaze;
    private readonly global::Poser.Services.IActorManager _actorManager;
    private readonly Application.Integration.IIntegrationRuntimePort _integrationPort;
    private bool _openDebug;

    /// <summary>The ownership hunt's controls: dump, write logging, and
    /// the three clone-with-animation strategies. Everything reports to
    /// the plugin log; findings drive the ownership design, then this
    /// section goes away.</summary>
    private void DebugRows(Crystarium.FormScope form, ActorId actor)
    {
        form.Switch(
            "Log timeline writes",
            _probePort.ProbeLogging,
            next => _probePort.ProbeSetTimelineLogging(next),
            "Log every native timeline write and who made it");
        form.Actions("Probe", actions =>
        {
            actions.Button(
                "Dump",
                () => _probePort.ProbeDump(actor),
                help: "Log this actor's animation state");
            actions.Button(
                "Find clocks",
                () => _probePort.ProbeFindClocks(actor),
                help: "Log which timeline offsets advance like clocks");
            actions.Button(
                "Watch reset",
                () => _probePort.ProbeWatchReset(actor),
                help: "Log every animation clock each frame for 6s");
            actions.Button(
                "Clone A",
                () => ProbeClone(actor, Game.Animation.ProbeMethod.Verbs),
                help: "Clone; apply animation via play verbs");
            actions.Button(
                "Clone B",
                () => ProbeClone(actor, Game.Animation.ProbeMethod.RawOnce),
                help: "Clone; apply animation via raw writes, once");
            actions.Button(
                "Clone C",
                () => ProbeClone(actor, Game.Animation.ProbeMethod.Seam),
                help: "Clone; hold animation at the update seam");
            actions.Button(
                "Clone D",
                () => ProbeClone(actor, Game.Animation.ProbeMethod.Owned),
                help: "Clone; replay Poser's owned record");
        });
    }

    private void ProbeClone(ActorId source, Game.Animation.ProbeMethod method)
    {
        if (_probePort.ProbeCapture(source) is not { } capture)
        {
            _notices.Failed("Probe: the source did not answer a read.");
            return;
        }
        var resolved = _bindings.Resolve(source);
        if (!resolved.Success || resolved.Value is not { } sourceActor)
        {
            _notices.Failed("Probe: the source actor did not resolve.");
            return;
        }
        var clone = _lifecycle.SpawnActor(
            $"Probe clone ({method})",
            () => _spawner.CloneActor(sourceActor));
        if (clone == null || _bindings.GetActorId(clone) is not { } cloneId)
        {
            _notices.Failed("Probe: the clone did not spawn.");
            return;
        }
        _probePort.ProbeSchedule(
            cloneId, capture, method,
            method == Game.Animation.ProbeMethod.Owned
                ? ProbeOwnedReplay(source)
                : ProbeSessionTransfer(source),
            ProbeSecondPass(source));
    }

    /// <summary>The session-owned state — the big Animation toggle, slot
    /// speeds, held expression, lips — plus the live gaze, re-issued on the
    /// target through the owning services, so the record matches the engine
    /// and the toggles tell the truth. Gaze POSITIONS travel RELATIVE to
    /// the actor (ruled 2026-09-01): the clone looks where the source
    /// looked as seen from its own feet, not at an absolute point.</summary>
    private Action<ActorId> ProbeSessionTransfer(ActorId source)
    {
        var owned = _animation.OverridesFor(source);
        var resolvedSource = _bindings.Resolve(source);
        var sourceActor = resolvedSource.Success ? resolvedSource.Value : null;
        var gaze = sourceActor != null && _gaze.IsAvailable
            ? _gaze.GetGazeState(sourceActor)
            : null;
        var sourceTransform = sourceActor?.Transform;
        nint gazeTargetAddress = sourceActor != null
            ? _gaze.GetGazeTargetAddress(sourceActor)
            : 0;
        // The GAME's window-owned stare (face-camera locks the point
        // the camera held when the toggle was flipped)
        // adopts as a Position gaze when Poser owns none of its own.
        var gameGaze = gaze is { Mode: global::Poser.Services.GazeTargetMode.None }
            ? _probePort.ProbeGameGaze(source)
            : null;
        // The clone shares the source's Customize+ body: the active SAVED
        // profile copies as a temporary one. A temporary profile on the
        // source cannot be read back through the IPC and is skipped.
        string? bodyProfileJson = null;
        var bodyProbe = _integrationPort.ProbeBodyProfile(source);
        if (bodyProbe.Success
            && bodyProbe.Value is { ActiveProfile: { } activeProfile, ActiveIsSaved: true })
        {
            var profileJson = _integrationPort.GetBodyProfileJson(activeProfile);
            if (profileJson.Success)
                bodyProfileJson = profileJson.Value;
        }
        var lockedParts = new List<global::Poser.Services.GazeTargetType>();
        if (sourceActor != null && gaze is { Mode: not global::Poser.Services.GazeTargetMode.None })
        {
            foreach (var part in PartOrder)
            {
                if (_gaze.IsPartLocked(sourceActor, part))
                    lockedParts.Add(part);
            }
        }
        return target =>
        {
            // Base identity and speeds land on the first pass. LAYERED
            // state (upper body, expression) waits for the second pass:
            // the base/emote engagement a few ticks in wipes whatever
            // layer was set in the same breath (slot[1] 0!=7356, run
            // nine) — the same second-evaluation-edge rule as #75.
            if (owned.BaseTimeline is { } baseTimeline)
                _animation.PlayBase(target, baseTimeline);
            foreach (var (slot, timeline) in owned.LoopedSlots)
                _animation.SetSlotLoop(target, slot, timeline, true);
            if (owned.OverallSpeed is { } overall)
                _animation.SetSpeed(target, overall);
            foreach (var (slot, speed) in owned.SlotSpeeds)
                _animation.SetSlotSpeed(target, slot, speed);
            if (owned.Lips is { } lips)
                _animation.SetLips(target, lips);
            if (bodyProfileJson != null)
                _integrationPort.ApplyTemporaryBodyProfile(target, bodyProfileJson);
            var resolvedTarget = _bindings.Resolve(target);
            if (!resolvedTarget.Success || resolvedTarget.Value is not { } clone)
                return;
            if (gameGaze is { } stare)
            {
                var cloneFrame = clone.Transform;
                var rebased = sourceTransform is { } fromFrame
                    ? cloneFrame.Position + Vector3.Transform(
                        Vector3.Transform(
                            stare - fromFrame.Position,
                            Quaternion.Inverse(fromFrame.Rotation)),
                        cloneFrame.Rotation)
                    : stare;
                if (_gaze.SetGazeMode(
                        clone, global::Poser.Services.GazeTargetMode.Position)
                    .Success)
                {
                    _gaze.SetGazeParts(clone,
                        global::Poser.Services.GazeTargetType.Head
                        | global::Poser.Services.GazeTargetType.Eyes);
                    _gaze.SetGazePosition(clone, rebased);
                }
                return;
            }
            if (gaze is not { Mode: not global::Poser.Services.GazeTargetMode.None })
                return;
            // The service's own transition order (ApplyActorGaze is the
            // normative sequence): target/mode, parts, positions, locks.
            if (gaze.Mode == global::Poser.Services.GazeTargetMode.Entity)
            {
                var followed = FindActorByAddress(gazeTargetAddress);
                if (followed == null)
                    return;
                if (!_gaze.SetGazeTarget(clone, followed).Success)
                    return;
            }
            else if (!_gaze.SetGazeMode(clone, gaze.Mode).Success)
            {
                return;
            }
            _gaze.SetGazeParts(clone, gaze.TargetType);
            if (gaze.Mode == global::Poser.Services.GazeTargetMode.Position)
            {
                var cloneTransform = clone.Transform;
                _gaze.SetGazePosition(clone, Rebase(gaze.Position));
                _gaze.SetPartPosition(clone,
                    global::Poser.Services.GazeTargetType.Eyes,
                    Rebase(gaze.EyesPosition));
                _gaze.SetPartPosition(clone,
                    global::Poser.Services.GazeTargetType.Head,
                    Rebase(gaze.HeadPosition));
                _gaze.SetPartPosition(clone,
                    global::Poser.Services.GazeTargetType.Body,
                    Rebase(gaze.BodyPosition));

                Vector3 Rebase(Vector3 point)
                {
                    if (sourceTransform is not { } from)
                        return point;
                    var to = cloneTransform;
                    var relative = Vector3.Transform(
                        point - from.Position,
                        Quaternion.Inverse(from.Rotation));
                    return to.Position
                        + Vector3.Transform(relative, to.Rotation);
                }
            }
            foreach (var part in lockedParts)
                _gaze.SetPartLock(clone, part, true);
        };
    }

    /// <summary>The transfer's SECOND PASS, ~half a second after the
    /// first: layered slot selections and the held expression, applied
    /// once the base or emote has settled — then the speeds re-asserted,
    /// because Replay on a paused actor resumes it first.</summary>
    private Action<ActorId>? ProbeSecondPass(ActorId source)
    {
        var owned = _animation.OverridesFor(source);
        // Weapon state is layered too: setting it in the first pass, the
        // base/emote engagement wiped it (report: clone wore the weapon
        // on its back while the source held it drawn).
        bool? weaponDrawn = _animation.Read(source)?.WeaponDrawn;
        var selections =
            new Dictionary<AnimationSlot, ushort>(owned.SelectedSlots);
        foreach (var (slot, timeline) in owned.AppliedSlots)
        {
            if (!selections.ContainsKey(slot))
                selections[slot] = timeline;
        }
        foreach (var slot in owned.LoopedSlots.Keys)
            selections.Remove(slot);
        if (selections.Count == 0
            && owned.HeldExpression == null
            && weaponDrawn is null or false)
            return null;
        return target =>
        {
            foreach (var (slot, timeline) in selections)
            {
                _animation.ChooseSlot(target, slot, timeline);
                _animation.Replay(target, timeline, out _);
            }
            if (owned.OverallSpeed is { } overall)
                _animation.SetSpeed(target, overall);
            foreach (var (slot, speed) in owned.SlotSpeeds)
                _animation.SetSlotSpeed(target, slot, speed);
            if (owned.HeldExpression is { } expression)
                _animation.HoldExpression(target, expression);
            if (weaponDrawn is { } drawn)
                _animation.SetWeaponDrawn(target, drawn);
        };
    }

    private static readonly global::Poser.Services.GazeTargetType[] PartOrder =
    [
        global::Poser.Services.GazeTargetType.Body,
        global::Poser.Services.GazeTargetType.Head,
        global::Poser.Services.GazeTargetType.Eyes,
    ];

    private global::Poser.Entities.IActor? FindActorByAddress(nint address)
    {
        if (address == nint.Zero)
            return null;
        foreach (var actor in _actorManager.Actors)
        {
            if (actor.Address == address)
                return actor;
        }
        return null;
    }

    /// <summary>The ownership-transfer replay: everything the session's
    /// owned record says about the SOURCE, re-issued through the session's
    /// own verbs on the target. What the user did, done again.</summary>
    private Action<ActorId> ProbeOwnedReplay(ActorId source)
    {
        var owned = _animation.OverridesFor(source);
        return target =>
        {
            if (owned.BaseTimeline is { } baseTimeline)
                _animation.PlayBase(target, baseTimeline);
            foreach (var (slot, timeline) in owned.LoopedSlots)
                _animation.SetSlotLoop(target, slot, timeline, true);
            foreach (var (slot, timeline) in owned.SelectedSlots)
            {
                if (owned.LoopedSlots.ContainsKey(slot))
                    continue;
                _animation.ChooseSlot(target, slot, timeline);
                _animation.Replay(target, timeline, out _);
            }
            if (owned.OverallSpeed is { } overall)
                _animation.SetSpeed(target, overall);
            foreach (var (slot, speed) in owned.SlotSpeeds)
                _animation.SetSlotSpeed(target, slot, speed);
            if (owned.Lips is { } lips)
                _animation.SetLips(target, lips);
        };
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
        bool disabled)
    {
        ushort live = reading.TimelineFor(slot);
        _layerSelections.TryGetValue((actor, slot), out var choice);
        ushort selected = choice is { } exact ? (ushort)exact.TimelineId : (ushort)0;
        if (choice != null && _animation.SelectedFor(actor, slot) != selected)
        {
            _layerSelections.Remove((actor, slot));
            choice = null;
            selected = 0;
        }
        // The whole-actor pause pauses EVERY layer, so every row offers
        // Play while it holds — not only Base (the old special case left
        // other rows saying Pause over a frozen layer).
        bool paused = (owned.SlotSpeeds.TryGetValue(slot, out var ownedSpeed) &&
            ownedSpeed == 0f) ||
            _animation.IsPaused(actor);
        bool needsReplay = selected != 0 && live != selected;
        // "None" has no clock: with nothing live, staged, or selected,
        // the speed and play controls have nothing to drive.
        bool hasAnimation = live != 0 || selected != 0
            || _animation.SelectedFor(actor, slot) != null;
        var feed = slot switch
        {
            AnimationSlot.Base => _baseFeed,
            AnimationSlot.Lips => _lipsFeed,
            _ => SlotFeed(slot),
        };

        var actionStyle = FixedActionStyle();
        var selectionStyle = FixedSelectionStyle();
        // The row pairs live native state with the shared staged slot selection.
        form.ReadOnlyWithActions(
            "Animation",
            DisplayName(live, slot, choice, "None"),
            actions =>
            {
                actions.Button(
                    choice?.Name ?? "Choose animation",
                    () => OpenPicker(feed, actor, choice),
                    style: selectionStyle,
                    disabled: disabled);
                actions.Button(
                    "Apply",
                    () => Report(
                        _animation.PlaySelectedSlot(
                            actor,
                            slot,
                            choice,
                            _playEmoteStart,
                            resume: false),
                        $"{label} playback"),
                    style: actionStyle,
                    disabled: disabled || selected == 0);
                actions.Button(
                    "Reset",
                    () => ResetLayer(actor, slot, label),
                    style: actionStyle,
                    disabled: disabled || !_animation.OwnsSlot(actor, slot));
            },
            id: $"anim-{slot}-current");

        float speed = owned.SlotSpeeds.TryGetValue(slot, out var overrideSpeed)
            ? overrideSpeed
            : reading.SpeedFor(slot);
        form.Slider(
            "Speed",
            speed,
            0f,
            2f,
            next => Report(
                _animation.SetSlotSpeed(actor, slot, next),
                $"{label} speed"),
            format: "0.00",
            marks: UnitMarks,
            disabled: disabled || !hasAnimation,
            actions: actions =>
            {
                bool play = paused || needsReplay;
                actions.Button(
                    play ? "Play" : "Pause",
                    () => Report(
                        play
                            ? _animation.PlaySelectedSlot(
                                actor,
                                slot,
                                choice,
                                _playEmoteStart)
                            : _animation.PauseSlot(actor, slot),
                        $"{label} playback"),
                    style: actionStyle,
                    disabled: disabled || !hasAnimation);
            },
            id: $"anim-{slot}-speed");

        if (slot is AnimationSlot.Base or AnimationSlot.UpperBody)
            DrawScrub(form, actor, slot, actionStyle, disabled);
    }

    private void DrawAnimationLayers(
        Crystarium.FormScope form,
        ActorId actor,
        ActorAnimationReading reading,
        AnimationOverrides owned,
        bool advanced)
    {
        form.Switch(
            "Advanced animation",
            advanced,
            next => SetAdvanced(actor, next));

        // Keep every layer visible while the shared scope makes it inert.
        ImGui.BeginDisabled(!advanced);
        for (int index = 0; index < PrimaryLayers.Length; index++)
        {
            var slot = PrimaryLayers[index];
            string label = AnimationSlots.DisplayName(slot);
            form.Subgroup(label, disabled: !advanced);
            if (slot == AnimationSlot.Facial)
            {
                DrawHeldExpression(
                    form, actor, poseSurface: false, disabled: !advanced);
                continue;
            }
            DrawLayer(form, actor, reading, owned, slot, label, !advanced);
            if (slot is AnimationSlot.Base or AnimationSlot.UpperBody)
            {
                // The visible label is shared, so the slot scopes its ImGui identity.
                ImGui.PushID($"anim-{slot}-loop");
                form.Switch(
                    "Loop",
                    _animation.LoopWantedFor(actor, slot),
                    next => Report(
                        _animation.SetSlotLoop(
                            actor, slot, 0, next),
                        $"{label} loop"),
                    disabled: !advanced);
                ImGui.PopID();
            }
        }
        ImGui.EndDisabled();
    }

    private void SetAdvanced(ActorId actor, bool enabled)
    {
        bool current = _advancedActors.Contains(actor);
        if (enabled == current)
            return;

        if (enabled)
        {
            // ENTERING advanced is a pure view change (ruled 2026-09-01):
            // it must not reset, replay, or release ANYTHING — the old
            // restore-Base-first handoff restarted the base (losing scrub
            // points and layered animations, worst on a fresh clone). The
            // layer rows simply adopt whatever state the actor holds.
            _generalSelections.Remove(actor);
            _layerSelections.Remove((actor, AnimationSlot.Base));
            _advancedActors.Add(actor);
            return;
        }

        CancelExpressionRetry(actor);
        var expression = _animation.ReleaseExpression(actor);
        if (!expression.Success)
        {
            Report(expression, "Animation mode");
            return;
        }
        _expressionSelections.Remove(actor);
        _layerSelections.Remove((actor, AnimationSlot.Facial));

        // Advanced releases every layer before Basic can issue Base commands.
        foreach (var slot in PrimaryLayers)
        {
            var reset = _animation.ResetSlot(actor, slot);
            if (!reset.Success)
            {
                Report(reset, "Basic animation");
                return;
            }
        }
        RemoveLayerSelections(actor);
        _advancedActors.Remove(actor);
    }

    private void DrawGeneral(
        Crystarium.FormScope form,
        ActorId actor,
        ActorAnimationReading reading,
        bool advanced)
    {
        form.Pair(
            "Play emote start",
            cell => cell.Switch(
                "##anim-general-emote-start",
                _playEmoteStart,
                next => _playEmoteStart = next),
            "Loop",
            cell => cell.Switch(
                "##anim-general-loop",
                _animation.LoopWantedFor(actor, AnimationSlot.Base),
                next => Report(
                    _animation.SetSlotLoop(actor, AnimationSlot.Base, 0, next),
                    "Loop"),
                disabled: advanced));

        _generalSelections.TryGetValue(actor, out var command);
        if (command is { Applied: true } &&
            _animation.SelectedFor(actor, command.Entry.Slot) == null)
        {
            _generalSelections.Remove(actor);
            command = null;
        }
        // No staged pick still DESCRIBES what the actor is doing: the live
        // base slot names a game emote (/hum) the same way the Full body
        // layer row does. "None" means idle, never "Poser picked nothing" —
        // so the two idle stands render as the empty value, not as names.
        ushort liveBase = reading.TimelineFor(AnimationSlot.Base);
        if (liveBase is AnimationTimelines.Idle or AnimationTimelines.BattleIdle)
            liveBase = 0;
        ushort live = command is { } staged
            ? reading.TimelineFor(staged.Entry.Slot)
            : liveBase;
        var selectionStyle = FixedSelectionStyle();
        form.ReadOnlyWithActions(
            "Animation",
            DisplayName(live, AnimationSlot.Base, command?.Entry, "None"),
            actions =>
            {
                actions.Button(
                    command?.Entry.Name ?? "Choose animation",
                    () => OpenPicker(
                        _generalFeed,
                        actor,
                        command?.Entry),
                    style: selectionStyle,
                    disabled: advanced);
                actions.Button(
                    "Apply",
                    () => ApplyGeneral(actor),
                    disabled: advanced || command == null);
                actions.Button(
                    "Reset",
                    () => ResetGeneral(actor),
                    disabled: advanced || (command == null &&
                        !_animation.OwnsSlot(actor, AnimationSlot.Base) &&
                        !_animation.LoopWantedFor(actor, AnimationSlot.Base)));
            },
            id: "anim-general-command");

        DrawStance(form, actor, reading);
    }

    private static ControlStyle FixedActionStyle() =>
        ControlStyle.Workspace with
        {
            Width = UiWidth.Fixed(Crystarium.ActiveTheme.Form.ValueColumnWidth),
        };

    // Selection text clips inside the natural Choose animation button seat.
    private static ControlStyle FixedSelectionStyle()
    {
        var style = ControlStyle.Workspace;
        float width = Crystarium.MeasureButton("Choose animation", style).X
            / ImGuiHelpers.GlobalScale;
        return style with { Width = UiWidth.Fixed(width) };
    }

    private void DrawScrub(
        Crystarium.FormScope form,
        ActorId actor,
        AnimationSlot slot,
        ControlStyle actionStyle,
        bool disabled)
    {
        var control = _animation.FindSlotControl(actor, slot);
        bool available = control is { Duration: > 0f };
        float time = available ? control!.Time : 0f;
        float duration = available ? control!.Duration : 1f;
        form.Slider(
            "Scrub",
            time,
            0f,
            duration,
            next => ScrubTo(actor, control, next),
            format: "0.00",
            disabled: disabled || !available,
            actions: actions => actions.Button(
                "Reset",
                () => ScrubTo(actor, control, 0f, finish: true),
                style: actionStyle,
                disabled: disabled || !available),
            id: $"anim-{slot}-scrub");

        if (_scrubActor is { } scrubActor && scrubActor.Equals(actor) &&
            !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            _animation.EndScrub();
            _scrubActor = null;
            _scrubControl = null;
        }
        // A gesture the session refused stays refused until the mouse is
        // released; without this every remaining drag frame re-reported.
        if (_scrubBlocked && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
            _scrubBlocked = false;
    }

    private bool _scrubBlocked;

    private void ScrubTo(
        ActorId actor, ScrubControlReading? control, float time, bool finish = false)
    {
        if (control == null || _scrubBlocked)
            return;
        if (_scrubActor is not { } scrubActor || !scrubActor.Equals(actor) ||
            _scrubControl != control.Id)
        {
            var begun = _animation.BeginScrub(actor, control.Id);
            if (!begun.Success)
            {
                Report(begun, "Animation scrub");
                _scrubBlocked = true;
                return;
            }
            _scrubActor = actor;
            _scrubControl = control.Id;
        }
        var updated = _animation.UpdateScrub(actor, time);
        if (!updated.Success)
        {
            // The session dropped the gesture (the control died mid-drag,
            // the timeline ended): report ONCE, end the pane's gesture, and
            // stay quiet until the next press.
            Report(updated, "Animation scrub");
            _animation.EndScrub();
            _scrubActor = null;
            _scrubControl = null;
            _scrubBlocked = true;
            return;
        }
        if (finish)
        {
            _animation.EndScrub();
            _scrubActor = null;
            _scrubControl = null;
        }
    }

    /// <summary>Draws the expression controls for the face workspace.</summary>
    public void DrawExpressionRow(Crystarium.FormScope form, ActorId actor)
    {
        PrunePaneState();
        if (!_animation.IsSupported(actor))
        {
            form.Status("This actor does not support expressions.");
            return;
        }
        _catalogLoader.EnsureLoaded();
        DrawExpression(form, actor);
    }

    /// <summary>Draws this pane's open picker.</summary>
    public void DrawExpressionPicker() => DrawPicker();

    private void DrawExpression(
        Crystarium.FormScope form,
        ActorId actor)
    {
        DrawHeldExpression(form, actor, poseSurface: true, disabled: false);
    }

    private void DrawHeldExpression(
        Crystarium.FormScope form,
        ActorId actor,
        bool poseSurface,
        bool disabled)
    {
        ushort held = _animation.HeldExpressionFor(actor) ?? 0;
        ushort selected = _animation.SelectedFor(actor, AnimationSlot.Facial) ?? 0;
        _expressionSelections.TryGetValue(actor, out var choice);
        var actionStyle = FixedActionStyle();
        bool pending = _expressionRetry?.Actor == actor;
        form.Picker(
            "Expression",
            ExpressionNameFor(actor, selected, "Choose expression"),
            () => OpenPicker(_expressionFeed, actor, choice),
            actions =>
            {
                actions.Button(
                    poseSurface ? "Preview" : "Apply",
                    () => ReportExpression(
                        ApplyExpression(actor, selected),
                        "Expression"),
                    style: poseSurface ? default : actionStyle,
                    disabled: disabled || selected == 0 || pending,
                    help: "Preview the expression");
                actions.Button(
                    "Reset",
                    () => ResetExpression(actor),
                    style: poseSurface ? default : actionStyle,
                    disabled: disabled || (held == 0 && selected == 0),
                    help: "Reset the face bones");
                if (poseSurface)
                {
                    actions.Button(
                        "Bake expression",
                        () =>
                        {
                            var descriptor = Describe(actor);
                            if (descriptor == null)
                                _notices.Refused(
                                    "Bake expression: actor is no longer in "
                                    + "the scene.");
                            else if (_animation.HoldExpression(actor, selected)
                                is { Success: false } previewFailed)
                                _notices.Failed(
                                    $"Bake expression: {previewFailed.Detail}");
                            else if (_facialCapture.Begin(actor, descriptor)
                                is { Success: false } failed)
                                _notices.Failed(
                                    $"Bake expression: {failed.Detail}");
                        },
                        disabled: disabled || selected == 0 || pending ||
                            _facialCapture.IsPending,
                        help: "Bake the face into the pose");
                }
            },
            disabled: disabled,
            help: poseSurface
                ? "Choose a facial expression, then Preview to hold it"
                : "Choose a facial expression, then Apply to hold it");
    }


    // The picker opens for the row that reserved the trigger.
    private void OpenPicker(
        TimelineFeed feed, ActorId actor, TimelineEntry? current)
    {
        _pickActor = actor;
        _openFeed = feed;
        feed.ResetFilter();
        _picker.Open(
            feed.Owner,
            Array.Empty<TimelineEntry>(),
            TimelineName,
            _timelineKey,
            current == null ? null : RowKey(current),
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
            kindFilter: null, weaponAware: false, entries: null);
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

    // Route metadata keeps friendly and raw aliases distinct in the picker.
    private static string RowKey(TimelineEntry entry) =>
        $"{(int)entry.Kind}:{(int)entry.Slot}:{entry.TimelineId}:" +
        $"{entry.EmoteId}:{entry.EmoteIndex}:{entry.Icon}:" +
        $"{entry.Name}:{entry.Key}";

    private sealed class TimelineFeed
    {
        private readonly AnimationPane _pane;
        private readonly AnimationSlot? _slotFilter;
        private readonly AnimationKind? _kindFilter;
        private readonly bool _weaponAware;
        private readonly Func<IReadOnlyList<TimelineEntry>>? _entries;
        private readonly bool _showKindStrip;
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
            AnimationKind? kindFilter,
            bool weaponAware,
            Func<IReadOnlyList<TimelineEntry>>? entries,
            bool showKindStrip = false)
        {
            _pane = pane;
            Owner = owner;
            Target = target;
            Slot = slot;
            _slotFilter = slotFilter;
            _kindFilter = kindFilter;
            _weaponAware = weaponAware;
            _entries = entries;
            _showKindStrip = showKindStrip;

            Results = Compute;
            Badge = Metadata;
            _setKind = chosen => _kindIndex = chosen;
        }

        internal PickerStrip? KindStrip =>
            _showKindStrip
                ? new PickerStrip(KindLabels, _kindIndex, _setKind)
                : null;

        internal PickerStrip? WeaponStrip =>
            _weaponAware
                ? new PickerStrip(
                    WeaponLabels, _pane._weaponFilter, _pane._setWeaponFilter)
                : null;

        internal string? LoadError =>
            _entries == null && !_pane._catalog.IsLoaded
                ? "Building animation catalog"
                : null;

        internal void ResetFilter() => _kindIndex = 0;

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

            var kind = _showKindStrip
                ? KindValues[Math.Clamp(_kindIndex, 0, KindValues.Length - 1)]
                : _kindFilter;
            bool namedKindsFirst = kind == null &&
                _slotFilter is AnimationSlot.Base or AnimationSlot.UpperBody;
            var found = _pane._catalog.Search(
                search, kind, _slotFilter,
                limit: namedKindsFirst ? int.MaxValue : 400);
            IReadOnlyList<TimelineEntry> eligible = found;

            if (_weaponAware && weapon != 0)
            {
                bool drawn = weapon == 2;
                var narrowed = new List<TimelineEntry>(found.Count);
                foreach (var entry in found)
                    if (entry.DrawsWeapon is not { } state || state == drawn)
                        narrowed.Add(entry);
                eligible = narrowed;
            }
            return namedKindsFirst
                ? NamedKindsFirst(eligible, 400)
                : eligible;
        }

        // Compatible Base/Upper feeds lead with named emotes, then actions.
        private static IReadOnlyList<TimelineEntry> NamedKindsFirst(
            IReadOnlyList<TimelineEntry> entries, int limit)
        {
            var ordered = new List<TimelineEntry>(Math.Min(entries.Count, limit));
            Append(static kind => kind is
                AnimationKind.Emote or AnimationKind.Expression);
            Append(static kind => kind == AnimationKind.Action);
            Append(static kind => kind == AnimationKind.RawTimeline);
            return ordered;

            void Append(Func<AnimationKind, bool> accepts)
            {
                if (ordered.Count >= limit)
                    return;
                foreach (var entry in entries)
                {
                    if (!accepts(entry.Kind))
                        continue;
                    ordered.Add(entry);
                    if (ordered.Count >= limit)
                        return;
                }
            }
        }

        private string? Metadata(TimelineEntry entry)
            => $"Applies to {AnimationSlots.DisplayName(entry.Slot)} · "
                + $"ID {_pane.IdText(entry.TimelineId)}";
    }

    private enum AnimationPickTarget
    {
        General,
        Base,
        Slot,
        Lips,
        Expression,
    }

    private readonly record struct AnimationPick(
        TimelineEntry Entry,
        AnimationPickTarget Target,
        AnimationSlot Slot);

    private sealed record GeneralSelection(TimelineEntry Entry, bool Applied);

    private sealed record PendingExpressionRetry(
        ActorId Actor,
        TimelineEntry Entry,
        SessionGeneration Session,
        object Binding,
        long DueAt);


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
            if (_catalog.FindDisplay(id, AnimationSlot.Lips) is { } known)
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

    private string DisplayName(
        ushort timeline,
        AnimationSlot slot,
        TimelineEntry? chosen,
        string empty) =>
        timeline == 0
            ? empty
            : chosen is { } exact && exact.TimelineId == timeline
                ? exact.Name
            : _catalog.FindDisplay(timeline, slot) is { } entry
                ? entry.Name
                : $"Timeline {timeline}";

    private void Apply(ActorId actor, AnimationPick pick)
    {
        var timeline = (ushort)pick.Entry.TimelineId;
        switch (pick.Target)
        {
            case AnimationPickTarget.General:
                _generalSelections[actor] = new GeneralSelection(pick.Entry, false);
                break;
            case AnimationPickTarget.Base:
                ChooseLayer(actor, AnimationSlot.Base, pick.Entry);
                break;
            case AnimationPickTarget.Slot:
                ChooseLayer(actor, pick.Slot, pick.Entry);
                break;
            case AnimationPickTarget.Expression:
                CancelExpressionRetry(actor);
                var expression = _animation.ChooseSlot(
                    actor, AnimationSlot.Facial, timeline);
                if (expression.Success)
                    _expressionSelections[actor] = pick.Entry;
                Report(expression, "Expression");
                break;
            case AnimationPickTarget.Lips:
                ChooseLayer(actor, AnimationSlot.Lips, pick.Entry);
                break;
        }
    }

    private void ChooseLayer(
        ActorId actor, AnimationSlot slot, TimelineEntry entry)
    {
        var chosen = _animation.ChooseSlot(
            actor, slot, (ushort)entry.TimelineId);
        if (chosen.Success)
            _layerSelections[(actor, slot)] = entry;
        Report(chosen, AnimationSlots.DisplayName(slot));
    }

    private void ResetLayer(ActorId actor, AnimationSlot slot, string label)
    {
        if (slot == AnimationSlot.Facial)
            CancelExpressionRetry(actor);
        var reset = _animation.ResetSlot(actor, slot);
        if (reset.Success)
        {
            _layerSelections.Remove((actor, slot));
            if (slot == AnimationSlot.Facial)
                _expressionSelections.Remove(actor);
        }
        Report(reset, $"{label} reset");
    }

    private void ApplyGeneral(ActorId actor)
    {
        if (!_generalSelections.TryGetValue(actor, out var command))
            return;
        var entry = command.Entry;
        var chosen = _animation.ChooseSlot(
            actor,
            entry.Slot,
            (ushort)entry.TimelineId);
        if (!chosen.Success)
        {
            Report(chosen, "Animation");
            return;
        }
        _generalSelections[actor] = command with { Applied = true };
        Report(
            _animation.PlaySelectedSlot(
                actor, entry.Slot, entry, _playEmoteStart),
            "Animation");
    }

    private void ResetGeneral(ActorId actor)
    {
        var reset = _animation.ResetSlot(actor, AnimationSlot.Base);
        if (!reset.Success)
        {
            Report(reset, "Animation reset");
            return;
        }
        if (_animation.LoopWantedFor(actor, AnimationSlot.Base))
        {
            var loop = _animation.SetSlotLoop(
                actor, AnimationSlot.Base, 0, false);
            if (!loop.Success)
            {
                Report(loop, "Animation reset");
                return;
            }
        }
        _generalSelections.Remove(actor);
        _layerSelections.Remove((actor, AnimationSlot.Base));
    }

    private void ReportExpression(AnimationResult result, string what) =>
        Report(result, what);

    private string ExpressionNameFor(ActorId actor, ushort timeline, string empty)
    {
        if (timeline == 0)
            return empty;
        if (_expressionSelections.TryGetValue(actor, out var chosen) &&
            chosen.TimelineId == timeline)
            return chosen.Name;
        foreach (var entry in _catalog.Entries)
            if (entry.TimelineId == timeline &&
                entry.Kind == AnimationKind.Expression &&
                entry.Slot == AnimationSlot.Facial)
                return entry.Name;
        return $"Timeline {timeline}";
    }

    private void ResetExpression(ActorId actor)
    {
        CancelExpressionRetry(actor);
        var result = _animation.ReleaseExpression(actor);
        if (result.Success)
        {
            _expressionSelections.Remove(actor);
            _layerSelections.Remove((actor, AnimationSlot.Facial));
        }
        ReportExpression(result, "Expression");
    }

    private AnimationResult ApplyExpression(ActorId actor, ushort timeline)
    {
        var applied = _animation.HoldExpression(actor, timeline);
        if (!applied.Success)
            return applied;
        if (_expressionSelections.TryGetValue(actor, out var entry) &&
            entry.TimelineId == timeline &&
            _sessionGeneration.ActiveSessionGeneration is { } session &&
            _bindings.Resolve(actor) is { Success: true, Value: { } binding })
        {
            _expressionRetry = new PendingExpressionRetry(
                actor, entry, session, binding,
                Environment.TickCount64 + ExpressionRetryDelayMs);
        }
        return AnimationResult.Ok();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (_expressionRetry is not { } pending)
            return;
        if (!ActorPresent(pending.Actor))
        {
            _expressionRetry = null;
            var released = _animation.ReleaseExpression(pending.Actor);
            if (!released.Success)
                _notices.Failed($"Expression reset: {released.Detail}");
            _expressionSelections.Remove(pending.Actor);
            _layerSelections.Remove((pending.Actor, AnimationSlot.Facial));
            return;
        }
        var binding = _bindings.Resolve(pending.Actor);
        if (_sessionGeneration.ActiveSessionGeneration != pending.Session ||
            binding is not { Success: true, Value: { } currentBinding } ||
            !ReferenceEquals(currentBinding, pending.Binding) ||
            _animation.SelectedFor(pending.Actor, AnimationSlot.Facial) !=
                pending.Entry.TimelineId ||
            !_expressionSelections.TryGetValue(pending.Actor, out var selected) ||
            selected != pending.Entry)
        {
            _expressionRetry = null;
            return;
        }
        if (Environment.TickCount64 < pending.DueAt)
            return;

        // One delayed replay gives a paused client a second evaluation edge.
        _expressionRetry = null;
        var replayed = _animation.HoldExpression(
            pending.Actor, (ushort)pending.Entry.TimelineId);
        if (!replayed.Success)
            _notices.Failed($"Expression retry: {replayed.Detail}");
    }

    private void CancelExpressionRetry(ActorId actor)
    {
        if (_expressionRetry?.Actor == actor)
            _expressionRetry = null;
    }

    private bool ActorPresent(ActorId actor)
    {
        foreach (var candidate in _scene.Snapshot.Actors)
            if (candidate.Id == actor)
                return true;
        return false;
    }

    private void PrunePaneState()
    {
        if (_pickActor is { } pickerActor && !ActorPresent(pickerActor))
        {
            _pickActor = null;
            _openFeed = null;
        }
        if (_scrubActor is { } scrubActor && !ActorPresent(scrubActor))
        {
            _animation.EndScrub();
            _scrubActor = null;
            _scrubControl = null;
        }
        foreach (var actor in _generalSelections.Keys.ToArray())
            if (!ActorPresent(actor))
                _generalSelections.Remove(actor);
        foreach (var actor in _expressionSelections.Keys.ToArray())
            if (!ActorPresent(actor))
                _expressionSelections.Remove(actor);
        foreach (var key in _layerSelections.Keys.ToArray())
            if (!ActorPresent(key.Actor))
                _layerSelections.Remove(key);
        _advancedActors.RemoveWhere(actor => !ActorPresent(actor));
    }

    private void RemoveLayerSelections(ActorId actor)
    {
        foreach (var key in _layerSelections.Keys.ToArray())
            if (key.Actor == actor)
                _layerSelections.Remove(key);
    }

    private void Report(AnimationResult result, string what)
    {
        if (!result.Success)
            _notices.Failed($"{what}: {result.Detail}");
    }

    public void Dispose()
    {
        _expressionRetry = null;
        _framework.Update -= OnFrameworkUpdate;
    }

}
