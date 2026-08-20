using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
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
    private readonly Game.Animation.FacialPoseCapture _facialCapture;
    // Preview stays in-place while its validated facial settle is pending.
    private readonly Game.Animation.ExpressionHoldCoordinator _expressionHold;
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
        Game.Animation.ExpressionHoldCoordinator expressionHold,
        ITextureProvider textures,
        SceneSession scene,
        UserNotices notices)
    {
        _notices = notices;
        _animation = animation;
        _catalog = catalog;
        _catalogLoader = catalogLoader;
        _facialCapture = facialCapture;
        _expressionHold = expressionHold;
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
            var reading =
                _animation.Read(actor) ?? ActorAnimationReading.Empty;
            var owned = _animation.OverridesFor(actor);
            bool advanced = _advancedActors.Contains(actor);
            page.Section(
                "GENERAL",
                _openGeneral,
                next => _openGeneral = next,
                form => DrawGeneral(form, actor, reading, advanced),
                divider: false);
            page.Section(
                "ANIMATION LAYERS",
                _openAnimationLayers,
                next => _openAnimationLayers = next,
                form => DrawAnimationLayers(
                    form, actor, reading, owned, advanced));
        });

        DrawPicker();
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
        ushort selected = _animation.SelectedFor(actor, slot) ?? 0;
        bool paused = (owned.SlotSpeeds.TryGetValue(slot, out var ownedSpeed) &&
            ownedSpeed == 0f) ||
            (slot == AnimationSlot.Base && _animation.IsPaused(actor));
        bool needsReplay = selected != 0 && live != selected;
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
            NameFor(live, "None"),
            actions =>
            {
                actions.Button(
                    NameFor(selected, "Choose animation"),
                    () => OpenPicker(feed, actor, selected),
                    style: selectionStyle,
                    disabled: disabled);
                actions.Button(
                    "Apply",
                    () => Report(
                        _animation.PlaySelectedSlot(
                            actor,
                            slot,
                            _playEmoteStart ? _catalog.Find(selected) : null),
                        $"{label} playback"),
                    style: actionStyle,
                    disabled: disabled || selected == 0);
                actions.Button(
                    "Reset",
                    () => Report(
                        _animation.ResetSlot(actor, slot),
                        $"{label} reset"),
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
            disabled: disabled,
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
                                _playEmoteStart ? _catalog.Find(selected) : null)
                            : _animation.PauseSlot(actor, slot),
                        $"{label} playback"),
                    style: actionStyle,
                    disabled: disabled || (play && !paused && selected == 0));
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
                form.Switch(
                    "Loop",
                    _animation.LoopWantedFor(actor, slot),
                    next => Report(
                        _animation.SetSlotLoop(
                            actor, slot, 0, next),
                        $"{label} loop"),
                    disabled: !advanced);
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
            // Basic owns only Base. Restore it before exposing layer writes.
            var reset = _animation.ResetSlot(actor, AnimationSlot.Base);
            if (!reset.Success)
            {
                Report(reset, "Advanced animation");
                return;
            }
            if (_animation.LoopWantedFor(actor, AnimationSlot.Base))
            {
                var loop = _animation.SetSlotLoop(
                    actor, AnimationSlot.Base, 0, false);
                if (!loop.Success)
                {
                    Report(loop, "Advanced animation");
                    return;
                }
            }
            _generalSelections.Remove(actor);
            _advancedActors.Add(actor);
            return;
        }

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
        ushort live = command is { } staged
            ? reading.TimelineFor(staged.Entry.Slot)
            : (ushort)0;
        var selectionStyle = FixedSelectionStyle();
        form.ReadOnlyWithActions(
            "Animation",
            NameFor(live, "None"),
            actions =>
            {
                actions.Button(
                    command?.Entry.Name ?? "Choose animation",
                    () => OpenPicker(
                        _generalFeed,
                        actor,
                        command is { } selected
                            ? (ushort)selected.Entry.TimelineId
                            : (ushort)0),
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
    }

    private void ScrubTo(
        ActorId actor, ScrubControlReading? control, float time, bool finish = false)
    {
        if (control == null)
            return;
        if (_scrubActor is not { } scrubActor || !scrubActor.Equals(actor) ||
            _scrubControl != control.Id)
        {
            var begun = _animation.BeginScrub(actor, control.Id);
            if (!begun.Success)
            {
                Report(begun, "Animation scrub");
                return;
            }
            _scrubActor = actor;
            _scrubControl = control.Id;
        }
        Report(_animation.UpdateScrub(actor, time), "Animation scrub");
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
        var actionStyle = FixedActionStyle();
        bool pending = _expressionHold.IsPendingFor(actor);
        form.Picker(
            "Expression",
            NameFor(selected, "Choose expression"),
            () => OpenPicker(_expressionFeed, actor, selected),
            actions =>
            {
                actions.Button(
                    poseSurface ? "Preview" : "Apply",
                    () => ReportExpression(
                        _expressionHold.Begin(actor, selected),
                        "Expression"),
                    style: poseSurface ? default : actionStyle,
                    disabled: disabled || selected == 0 || pending,
                    help: "Preview the selected expression and hold its facial frame");
                actions.Button(
                    "Reset",
                    () => ReportExpression(
                        _expressionHold.Release(actor), "Expression"),
                    style: poseSurface ? default : actionStyle,
                    disabled: disabled || (held == 0 && selected == 0),
                    help: "Restore the facial state captured before Poser's first choice");
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
                        help: "Write the held face into the pose as one undoable edit");
                }
            },
            disabled: disabled,
            help: poseSurface
                ? "Choose a facial expression, then Preview to hold it"
                : "Choose a facial expression, then Apply to hold it");
    }


    // The picker opens for the row that reserved the trigger.
    private void OpenPicker(TimelineFeed feed, ActorId actor, ushort current)
    {
        _pickActor = actor;
        _openFeed = feed;
        feed.ResetFilter();
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
            case AnimationPickTarget.General:
                _generalSelections[actor] = new GeneralSelection(pick.Entry, false);
                break;
            case AnimationPickTarget.Base:
                Report(
                    _animation.ChooseSlot(
                        actor, AnimationSlot.Base, timeline, pick.Entry.IsLoop),
                    pick.Entry.Name);
                break;
            case AnimationPickTarget.Slot:
                Report(
                    _animation.ChooseSlot(actor, pick.Slot, timeline),
                    AnimationSlots.DisplayName(pick.Slot));
                break;
            case AnimationPickTarget.Expression:
                Report(
                    _animation.ChooseSlot(
                        actor, AnimationSlot.Facial, timeline),
                    "Expression");
                break;
            case AnimationPickTarget.Lips:
                Report(
                    _animation.ChooseSlot(actor, AnimationSlot.Lips, timeline),
                    "Lips");
                break;
        }
    }

    private void ApplyGeneral(ActorId actor)
    {
        if (!_generalSelections.TryGetValue(actor, out var command))
            return;
        var entry = command.Entry;
        var chosen = _animation.ChooseSlot(
            actor,
            entry.Slot,
            (ushort)entry.TimelineId,
            entry.Slot == AnimationSlot.Base && entry.IsLoop);
        if (!chosen.Success)
        {
            Report(chosen, "Animation");
            return;
        }
        _generalSelections[actor] = command with { Applied = true };
        Report(
            _animation.PlaySelectedSlot(
                actor, entry.Slot, _playEmoteStart ? entry : null),
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
    }

    private void ReportExpression(AnimationResult result, string what) =>
        Report(result, what);

    private void Report(AnimationResult result, string what)
    {
        if (!result.Success)
            _notices.Failed($"{what}: {result.Detail}");
    }

}
