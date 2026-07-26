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
/// The Animation tab: discover, play, blend, pause, loop, scrub, and
/// restore an actor's animation.
///
/// It renders from the session's own state plus one live native reading
/// per frame, so it never holds a second copy of anything. A bone
/// selection resolves to its owning actor without touching the sidebar
/// selection, which is what lets the tab stay useful mid-pose.
/// </summary>
public sealed class AnimationPane
{
    private readonly AnimationSession _animation;
    private readonly AnimationCatalog _catalog;
    private readonly AnimationSceneActions _sceneActions;
    private readonly SceneSession _scene;

    private string _search = string.Empty;
    private int _kindIndex;
    private int _slotFilterIndex;
    private bool _asBase = true;
    private bool _interrupt = true;
    private bool _playFromStart = true;
    private bool _forceLoop;
    private int _directTimeline;
    private string _status = string.Empty;
    private ScrubControlId? _activeScrub;
    private bool _showAdvanced;

    private static readonly string[] KindLabels =
        { "Emote", "Action", "Expression", "Raw" };
    private static readonly AnimationKind[] KindValues =
    {
        AnimationKind.Emote, AnimationKind.Action,
        AnimationKind.Expression, AnimationKind.RawTimeline,
    };
    private static readonly string[] StanceLabels =
        { "Idle", "Sit chair", "Sit ground", "Sleep" };
    private static readonly AnimationStance[] StanceValues =
    {
        AnimationStance.Idle, AnimationStance.SitChair,
        AnimationStance.SitGround, AnimationStance.Sleeping,
    };

    public AnimationPane(
        AnimationSession animation,
        AnimationCatalog catalog,
        AnimationSceneActions sceneActions,
        SceneSession scene)
    {
        _animation = animation;
        _catalog = catalog;
        _sceneActions = sceneActions;
        _scene = scene;
    }

    /// <summary>The actor the tab acts on: the selected actor, or the
    /// owning actor of a selected bone. Selection itself is untouched.</summary>
    private ActorId? TargetActor()
    {
        var primary = _scene.Selection.Primary;
        return primary switch
        {
            { Kind: SceneEntityKind.Actor, Actor: { } actor } => actor,
            { Kind: SceneEntityKind.Bone, Bone: { } bone } => bone.Skeleton.Actor,
            _ => null,
        };
    }

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
        var cursor = origin;
        float width = size.X;

        if (TargetActor() is not { } actor)
        {
            ViewText.Label(cursor + new Vector2(0f, 8f) * s,
                "Select an actor or bone to control its animation.", 12f,
                FontWeight.Regular, InspectorLayout.HintColor);
            DrawSceneActions(cursor + new Vector2(0f, 32f) * s, width, s);
            return;
        }

        if (!_animation.IsSupported(actor))
        {
            ViewText.Label(cursor + new Vector2(0f, 8f) * s,
                "This actor does not support animation control.", 12f,
                FontWeight.Regular, InspectorLayout.HintColor);
            return;
        }

        var reading = _animation.Read(actor) ?? ActorAnimationReading.Empty;
        var owned = _animation.OverridesFor(actor);

        cursor.Y += DrawHeader(actor, reading, owned, cursor, width, s);
        cursor.Y += 8f * s;
        cursor.Y += DrawSelector(actor, cursor, width, s);
        cursor.Y += 8f * s;
        cursor.Y += DrawStance(actor, reading, cursor, width, s);
        cursor.Y += 8f * s;
        cursor.Y += DrawSlots(actor, reading, owned, cursor, width, s);
        cursor.Y += 8f * s;
        cursor.Y += DrawScrub(actor, reading, cursor, width, s);
        cursor.Y += 8f * s;
        cursor.Y += DrawLips(actor, reading, cursor, width, s);

        if (_status.Length > 0)
        {
            cursor.Y += 6f * s;
            ViewText.Label(cursor, _status, 11f, FontWeight.Regular,
                InspectorLayout.HintColor);
        }
    }

    // ── Header ────────────────────────────────────────────────────────

    private float DrawHeader(
        ActorId actor, ActorAnimationReading reading,
        AnimationOverrides owned, Vector2 cursor, float width, float s)
    {
        var name = Describe(actor)?.Name ?? "Actor";
        ushort current = reading.BaseTimeline != 0
            ? reading.BaseTimeline
            : reading.TimelineFor(AnimationSlot.Base);
        var entry = current == 0 ? null : _catalog.Find(current);
        var caption = current == 0
            ? "No animation"
            : entry != null ? $"{entry.Name} ({current})" : $"Timeline {current}";

        ViewText.Label(cursor, name, 13f, FontWeight.Medium, new Vector4(1f, 1f, 1f, 1f));
        ViewText.Label(cursor + new Vector2(0f, 17f) * s, caption, 11f,
            FontWeight.Regular, new Vector4(1f, 1f, 1f, 0.55f), mono: true);

        var row = cursor + new Vector2(0f, 38f) * s;
        ImGui.SetCursorScreenPos(row);
        bool paused = _animation.IsPaused(actor);
        if (Crystarium.Button(paused ? "Resume" : "Pause", new ButtonProps
            {
                Id = "anim-pause",
                Classes = Cls.Compact,
                Tooltip = paused
                    ? "Continue from the current frame"
                    : "Hold the actor on the current frame",
            }))
            Report(paused ? _animation.Resume(actor) : _animation.Pause(actor), "Playback");

        ImGui.SameLine(0f, 6f * s);
        if (Crystarium.Button("Stop", new ButtonProps
            {
                Id = "anim-stop",
                Classes = Cls.Compact,
                Tooltip = "Restore this actor's incoming animation state",
            }))
            Report(_animation.ResetActor(actor), "Reset animation");

        ImGui.SameLine(0f, 12f * s);
        bool loop = owned.ForceLoop != null;
        if (Crystarium.Switch("##anim-loop", ref loop))
        {
            _forceLoop = loop;
            Report(
                _animation.SetForceLoop(actor, loop ? current : (ushort)0),
                "Loop");
        }
        ImGui.SameLine(0f, 6f * s);
        ViewText.Label(
            new Vector2(ImGui.GetCursorScreenPos().X, row.Y + 4f * s),
            "Loop", 12f, FontWeight.Regular, new Vector4(1f, 1f, 1f, 0.72f));

        // Speed: -5..10 with 1 as normal, per the PBI. Reset drops the
        // override rather than writing 1, so the game's own speed returns.
        var speedRow = cursor + new Vector2(0f, 66f) * s;
        ImGui.SetCursorScreenPos(speedRow);
        float speed = owned.OverallSpeed ?? reading.OverallSpeed;
        ImGui.SetNextItemWidth(width - 120f * s);
        if (Crystarium.Slider("##anim-speed", ref speed, -5f, 10f))
            Report(_animation.SetSpeed(actor, speed), "Speed");
        ImGui.SameLine(0f, 6f * s);
        if (Crystarium.Button("Normal", new ButtonProps
            {
                Id = "anim-speed-reset",
                Classes = Cls.Compact,
                Tooltip = "Hand playback speed back to the game",
            }))
            Report(_animation.ClearSpeed(actor), "Speed");

        DrawSceneActions(cursor + new Vector2(0f, 92f) * s, width, s);
        return 122f * s;
    }

    private void DrawSceneActions(Vector2 cursor, float width, float s)
    {
        ImGui.SetCursorScreenPos(cursor);
        if (Crystarium.Button("Freeze all", new ButtonProps
            { Id = "anim-freeze-all", Classes = Cls.Compact,
              Tooltip = "Pause every actor in the scene" }))
            Report(_sceneActions.FreezeAll(), "Freeze all");
        ImGui.SameLine(0f, 6f * s);
        if (Crystarium.Button("Resume all", new ButtonProps
            { Id = "anim-resume-all", Classes = Cls.Compact,
              Tooltip = "Resume every actor in the scene" }))
            Report(_sceneActions.ResumeAll(), "Resume all");
        ImGui.SameLine(0f, 6f * s);
        if (Crystarium.Button("Replay", new ButtonProps
            { Id = "anim-replay-all", Classes = Cls.Compact,
              Tooltip = "Restart what each actor is already playing" }))
            Report(_sceneActions.ReplayAll(), "Replay");
        ImGui.SameLine(0f, 6f * s);
        if (Crystarium.Button("Restore all", new ButtonProps
            { Id = "anim-stop-all", Classes = Cls.Compact,
              Tooltip = "Restore every actor's incoming animation state" }))
            Report(_sceneActions.StopAll(), "Restore all");
    }

    // ── Selector ──────────────────────────────────────────────────────

    private float DrawSelector(ActorId actor, Vector2 cursor, float width, float s)
    {
        ViewText.Label(cursor, "Animation", 12f, FontWeight.Medium,
            new Vector4(1f, 1f, 1f, 0.85f));
        var row = cursor + new Vector2(0f, 18f) * s;

        ImGui.SetCursorScreenPos(row);
        ImGui.SetNextItemWidth(width * 0.5f);
        Crystarium.TextInput("##anim-search", ref _search, "Search name or id");

        ImGui.SameLine(0f, 6f * s);
        ImGui.SetNextItemWidth(width * 0.22f);
        Crystarium.SegmentedControl("##anim-kind", KindLabels, ref _kindIndex);

        ImGui.SameLine(0f, 6f * s);
        ImGui.SetNextItemWidth(width * 0.22f);
        var slotLabels = SlotFilterLabels();
        Crystarium.Dropdown("##anim-slot", slotLabels, ref _slotFilterIndex);

        var modeRow = row + new Vector2(0f, 26f) * s;
        ImGui.SetCursorScreenPos(modeRow);
        int mode = _asBase ? 0 : 1;
        if (Crystarium.SegmentedControl("##anim-mode", new[] { "Base", "Blend" }, ref mode))
            _asBase = mode == 0;

        ImGui.SameLine(0f, 10f * s);
        Crystarium.Switch("##anim-interrupt", ref _interrupt);
        ImGui.SameLine(0f, 4f * s);
        ViewText.Label(
            new Vector2(ImGui.GetCursorScreenPos().X, modeRow.Y + 4f * s),
            "Interrupt", 12f, FontWeight.Regular, new Vector4(1f, 1f, 1f, 0.72f));

        ImGui.SameLine(0f, 14f * s);
        Crystarium.Switch("##anim-fromstart", ref _playFromStart);
        ImGui.SameLine(0f, 4f * s);
        ViewText.Label(
            new Vector2(ImGui.GetCursorScreenPos().X, modeRow.Y + 4f * s),
            "From start", 12f, FontWeight.Regular, new Vector4(1f, 1f, 1f, 0.72f));

        // Direct timeline id: the same commit path as a catalog pick, so
        // an id typed here behaves exactly like the row it names.
        var idRow = modeRow + new Vector2(0f, 26f) * s;
        ImGui.SetCursorScreenPos(idRow);
        ImGui.SetNextItemWidth(90f * s);
        ImGui.InputInt("##anim-id", ref _directTimeline, 0, 0);
        if (_directTimeline < 0)
            _directTimeline = 0;
        ImGui.SameLine(0f, 6f * s);
        if (Crystarium.Button("Play id", new ButtonProps
            { Id = "anim-play-id", Classes = Cls.Compact,
              Tooltip = "Play this timeline id directly" }) && _directTimeline > 0)
        {
            var entry = _catalog.Find((uint)_directTimeline) ?? new TimelineEntry(
                (uint)_directTimeline, $"Timeline {_directTimeline}",
                AnimationKind.RawTimeline, AnimationSlot.Base);
            PlayEntry(actor, entry);
        }

        var listTop = idRow + new Vector2(0f, 26f) * s;
        float listHeight = 150f * s;
        if (!_catalog.IsLoaded)
        {
            ViewText.Label(listTop, "Building animation catalog…", 11f,
                FontWeight.Regular, InspectorLayout.HintColor);
            return (listTop.Y - cursor.Y) + 20f * s;
        }

        var kind = KindValues[Math.Clamp(_kindIndex, 0, KindValues.Length - 1)];
        AnimationSlot? slotFilter = _slotFilterIndex <= 0
            ? null
            : AnimationSlots.All[_slotFilterIndex - 1];
        var results = _catalog.Search(_search, kind, slotFilter, limit: 400);

        ImGui.SetCursorScreenPos(listTop);
        if (ImGui.BeginChild("##anim-list", new Vector2(width, listHeight), false,
                ImGuiWindowFlags.NoSavedSettings))
        {
            foreach (var entry in results)
            {
                if (ImGui.Selectable($"{entry.Name}##{entry.TimelineId}-{entry.Slot}"))
                    PlayEntry(actor, entry);
                ImGui.SameLine();
                ViewText.Label(
                    new Vector2(
                        ImGui.GetWindowPos().X + width - 130f * s,
                        ImGui.GetItemRectMin().Y + 2f * s),
                    $"{AnimationSlots.DisplayName(entry.Slot)} · {entry.TimelineId}",
                    11f, FontWeight.Regular, new Vector4(1f, 1f, 1f, 0.45f), mono: true);
            }
            if (results.Count == 0)
                ViewText.Label(ImGui.GetCursorScreenPos(), "No matches.", 11f,
                    FontWeight.Regular, InspectorLayout.HintColor);
        }
        ImGui.EndChild();
        return (listTop.Y - cursor.Y) + listHeight;
    }

    private static string[] SlotFilterLabels()
    {
        var labels = new string[AnimationSlots.All.Count + 1];
        labels[0] = "All slots";
        for (int i = 0; i < AnimationSlots.All.Count; i++)
            labels[i + 1] = AnimationSlots.DisplayName(AnimationSlots.All[i]);
        return labels;
    }

    /// <summary>Start-on-select: picking a row plays it immediately with
    /// the current Base/Blend, interrupt, from-start, and loop choices.</summary>
    private void PlayEntry(ActorId actor, TimelineEntry entry)
    {
        Report(
            _animation.PlayEntry(
                actor, entry, _asBase, _interrupt, _playFromStart, _forceLoop),
            entry.Name);
    }

    // ── Stance ────────────────────────────────────────────────────────

    private float DrawStance(
        ActorId actor, ActorAnimationReading reading, Vector2 cursor, float width, float s)
    {
        ViewText.Label(cursor, "Stance", 12f, FontWeight.Medium,
            new Vector4(1f, 1f, 1f, 0.85f));
        var row = cursor + new Vector2(0f, 18f) * s;
        ImGui.SetCursorScreenPos(row);

        int stanceIndex = Array.IndexOf(StanceValues, reading.Stance);
        if (stanceIndex < 0)
            stanceIndex = 0;
        ImGui.SetNextItemWidth(width * 0.42f);
        if (Crystarium.SegmentedControl("##anim-stance", StanceLabels, ref stanceIndex))
            Report(_animation.SetStance(actor, StanceValues[stanceIndex], 0), "Stance");

        // Pose stepping wraps in both directions against the count the
        // game reports for this stance, so stepping past either end lands
        // on a real pose instead of nothing.
        ImGui.SameLine(0f, 10f * s);
        if (Crystarium.Button("Pose −", new ButtonProps
            { Id = "anim-pose-prev", Classes = Cls.Compact }))
            Report(
                _animation.SetStance(actor, StanceValues[stanceIndex], reading.Pose - 1),
                "Pose");
        ImGui.SameLine(0f, 4f * s);
        if (Crystarium.Button("Pose +", new ButtonProps
            { Id = "anim-pose-next", Classes = Cls.Compact }))
            Report(
                _animation.SetStance(actor, StanceValues[stanceIndex], reading.Pose + 1),
                "Pose");

        ImGui.SameLine(0f, 12f * s);
        bool drawn = reading.WeaponDrawn;
        if (Crystarium.Switch("##anim-weapon", ref drawn))
            Report(_animation.SetWeaponDrawn(actor, drawn), "Weapon");
        ImGui.SameLine(0f, 4f * s);
        ViewText.Label(
            new Vector2(ImGui.GetCursorScreenPos().X, row.Y + 4f * s),
            "Weapon drawn", 12f, FontWeight.Regular, new Vector4(1f, 1f, 1f, 0.72f));

        ImGui.SameLine(0f, 12f * s);
        bool locked = _animation.OverridesFor(actor).PositionLock;
        if (Crystarium.Switch("##anim-poslock", ref locked))
            Report(_animation.SetPositionLock(actor, locked), "Position lock");
        ImGui.SameLine(0f, 4f * s);
        ViewText.Label(
            new Vector2(ImGui.GetCursorScreenPos().X, row.Y + 4f * s),
            "Lock position", 12f, FontWeight.Regular, new Vector4(1f, 1f, 1f, 0.72f));

        return 44f * s;
    }

    // ── Slots ─────────────────────────────────────────────────────────

    private float DrawSlots(
        ActorId actor, ActorAnimationReading reading,
        AnimationOverrides owned, Vector2 cursor, float width, float s)
    {
        ViewText.Label(cursor, "Slots", 12f, FontWeight.Medium,
            new Vector4(1f, 1f, 1f, 0.85f));
        float y = cursor.Y + 18f * s;

        foreach (var slot in AnimationSlots.All)
        {
            ushort timeline = reading.TimelineFor(slot);
            float slotSpeed = owned.SlotSpeeds.TryGetValue(slot, out var over)
                ? over
                : reading.SpeedFor(slot);
            var entry = timeline == 0 ? null : _catalog.Find(timeline);

            ViewText.Label(new Vector2(cursor.X, y + 3f * s),
                AnimationSlots.DisplayName(slot), 11f, FontWeight.Regular,
                new Vector4(1f, 1f, 1f, 0.8f));
            ViewText.Label(new Vector2(cursor.X + 90f * s, y + 3f * s),
                timeline == 0 ? "—" : entry != null
                    ? $"{entry.Name} ({timeline})"
                    : timeline.ToString(),
                11f, FontWeight.Regular, new Vector4(1f, 1f, 1f, 0.55f), mono: true);

            ImGui.SetCursorScreenPos(new Vector2(cursor.X + width - 190f * s, y));
            ImGui.SetNextItemWidth(90f * s);
            if (Crystarium.Slider($"##anim-slot-speed-{(int)slot}", ref slotSpeed, 0f, 2f))
                Report(_animation.SetSlotSpeed(actor, slot, slotSpeed), "Slot speed");

            ImGui.SameLine(0f, 6f * s);
            if (Crystarium.Button("Reset", new ButtonProps
                {
                    Id = $"anim-slot-reset-{(int)slot}",
                    Classes = Cls.Compact,
                    Tooltip = "Hand this slot's speed back to the game",
                }))
                Report(_animation.ClearSlotSpeed(actor, slot), "Slot speed");

            y += 22f * s;
        }
        return (y - cursor.Y);
    }

    // ── Scrub ─────────────────────────────────────────────────────────

    private float DrawScrub(
        ActorId actor, ActorAnimationReading reading, Vector2 cursor, float width, float s)
    {
        ViewText.Label(cursor, "Scrub", 12f, FontWeight.Medium,
            new Vector4(1f, 1f, 1f, 0.85f));
        ImGui.SetCursorScreenPos(new Vector2(cursor.X + width - 90f * s, cursor.Y));
        if (Crystarium.Button(_showAdvanced ? "Simple" : "Advanced", new ButtonProps
            {
                Id = "anim-scrub-advanced",
                Classes = Cls.Compact,
                Tooltip = "Show every valid animation control this actor reports",
            }))
            _showAdvanced = !_showAdvanced;

        float y = cursor.Y + 20f * s;
        var controls = reading.Controls;
        if (controls.Count == 0)
        {
            ViewText.Label(new Vector2(cursor.X, y), "No animation controls.", 11f,
                FontWeight.Regular, InspectorLayout.HintColor);
            return (y - cursor.Y) + 18f * s;
        }

        // Friendly view shows the first control per scrubbable slot;
        // Advanced shows every control the actor currently reports.
        int shown = 0;
        foreach (var control in controls)
        {
            if (!_showAdvanced && shown >= AnimationSlots.Scrubbable.Count)
                break;
            float time = control.Time;
            ViewText.Label(new Vector2(cursor.X, y + 3f * s),
                _showAdvanced
                    ? control.Id.ToString()
                    : AnimationSlots.DisplayName(AnimationSlots.Scrubbable[shown]),
                11f, FontWeight.Regular, new Vector4(1f, 1f, 1f, 0.8f), mono: true);

            ImGui.SetCursorScreenPos(new Vector2(cursor.X + 90f * s, y));
            ImGui.SetNextItemWidth(width - 190f * s);
            float duration = MathF.Max(control.Duration, 0.0001f);
            bool changed = Crystarium.Slider(
                $"##anim-scrub-{control.Id.Partial}-{control.Id.Control}",
                ref time, 0f, duration);

            // The drag owns the freeze: it begins on the first change and
            // ends on release, leaving the actor paused where it landed.
            if (changed)
            {
                if (_activeScrub != control.Id)
                {
                    Report(_animation.BeginScrub(actor, control.Id), "Scrub");
                    _activeScrub = control.Id;
                }
                Report(_animation.UpdateScrub(time), "Scrub");
            }
            if (_activeScrub == control.Id && ImGui.IsItemDeactivated())
            {
                _animation.EndScrub();
                _activeScrub = null;
            }

            ViewText.Label(new Vector2(cursor.X + width - 90f * s, y + 3f * s),
                $"{control.Time:0.00}/{control.Duration:0.00}", 11f,
                FontWeight.Regular, new Vector4(1f, 1f, 1f, 0.45f), mono: true);

            y += 22f * s;
            shown++;
        }
        return (y - cursor.Y);
    }

    // ── Lips ──────────────────────────────────────────────────────────

    private float DrawLips(
        ActorId actor, ActorAnimationReading reading, Vector2 cursor, float width, float s)
    {
        ViewText.Label(cursor, "Lips", 12f, FontWeight.Medium,
            new Vector4(1f, 1f, 1f, 0.85f));
        var row = cursor + new Vector2(0f, 18f) * s;

        var labels = new List<string> { "None" };
        var ids = new List<ushort> { 0 };
        for (ushort id = AnimationTimelines.FirstLips; id <= AnimationTimelines.LastLips; id++)
        {
            labels.Add(_catalog.Find(id)?.Name ?? $"Speech {id}");
            ids.Add(id);
        }
        int selected = ids.IndexOf(reading.LipsOverride);
        if (selected < 0)
            selected = 0;

        ImGui.SetCursorScreenPos(row);
        ImGui.SetNextItemWidth(width * 0.5f);
        if (Crystarium.Dropdown("##anim-lips", labels.ToArray(), ref selected))
            Report(_animation.SetLips(actor, ids[selected]), "Lips");

        return 44f * s;
    }

    // ── Status ────────────────────────────────────────────────────────

    private void Report(AnimationResult result, string what)
    {
        _status = result.Success ? string.Empty : $"{what}: {result.Detail}";
    }

    private void Report(AnimationSceneActions.SceneActionReport report, string verb)
    {
        _status = report.Success ? string.Empty : report.Summary(verb);
    }
}
