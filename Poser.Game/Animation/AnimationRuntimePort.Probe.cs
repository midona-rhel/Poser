using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Poser.Application.Animation;
using Poser.Domain.Animation;
using Poser.Domain.Identity;

namespace Poser.Game.Animation;

/// <summary>How a probe copy lands animation state on the target.</summary>
public enum ProbeMethod
{
    /// <summary>The trial-branch baseline: play verbs, then one staged
    /// seek. Known to corrupt the sequencer's mode machine.</summary>
    Verbs,

    /// <summary>The native set-timeline call per slot with NO mode
    /// walking, then every raw field once, then one staged seek.</summary>
    RawOnce,

    /// <summary>Pure field ownership imposed inside the game's own
    /// per-frame timeline update, held until a read-back says the state
    /// stuck. The scenery-anchor pattern on an actor.</summary>
    Seam,

    /// <summary>Replays Poser's OWNED record (the session overrides the
    /// user authored on the source) through the session's own verbs —
    /// the ownership-transfer thesis.</summary>
    Owned,
}

/// <summary>
/// THE ANIMATION OWNERSHIP PROBE — a debug harness, not a feature. The
/// hunt: which write strategy makes an actor adopt a captured animation
/// mid-flight (the clone-identical test), and which seam tells game-authored
/// timeline writes apart from rebuild noise (the yield rule). Everything
/// here logs loudly and ships behind the Animation pane's Debug section;
/// it is torn out once the ownership design lands.
/// </summary>
public sealed unsafe partial class AnimationRuntimePort
{
    /// <summary>Everything the sequencer and its havok controls say about
    /// one actor at one instant, raw enough to write back verbatim.</summary>
    public sealed class RawTimelineCapture
    {
        public byte Mode;
        public uint ModeParam;
        // Run two: a looping game emote (/hum) lives in the
        // EmoteController; the sequencer slots only flicker derived ids.
        // Cloning the slots clones the flicker — the emote id is the
        // identity that must carry.
        public uint EmoteId;
        public ushort BaseOverride;
        public ushort Forced;
        public ushort LipsOverride;
        public float OverallSpeed;
        public readonly Dictionary<int, ushort> SlotIds = new();
        public readonly Dictionary<int, float> SlotSpeeds = new();
        public readonly List<(ScrubControlId Id, float Time, float Speed)>
            Controls = new();
    }

    private sealed class ProbePending
    {
        public ActorId Target;
        public RawTimelineCapture Capture = null!;
        public ProbeMethod Method;
        public Action<ActorId>? Apply;
        // A second, narrower application ~half a second after the first:
        // holding an expression on a PAUSED actor needs a later
        // evaluation edge (issue #75's shape), and the pane's own apply
        // schedules the same delayed replay.
        public Action<ActorId>? Reapply;
        public int ReapplyIn;
        public int WaitTicks;
        public readonly List<int> VerifyIn = new();
        // The slot-0/mode watch: iteration one showed the base slot
        // reverting to idle WITHOUT a SetTimelineId call — a second
        // writer. The watch logs the exact tick every flip happens.
        public ushort WatchSlot0;
        public byte WatchMode;
        public int WatchTick;
        public bool WatchArmed;
    }

    private sealed class SeamArm
    {
        public RawTimelineCapture Capture = null!;
        public int FramesLeft;
        public int FramesWritten;
        // Controls-only arms replace the old one-shot seek: iteration one
        // wrote 0/1 because the clone's havok control did not exist yet
        // at +2 ticks. This writes each stored control's time and speed
        // at the seam until every one has taken a write once.
        public bool ControlsOnly;
        public int ControlsLanded;
        // Full-ownership arms disarm only after the state holds this many
        // CONSECUTIVE ticks — iteration one disarmed on a one-tick
        // lingering match and the second writer reverted it right after.
        public int HeldTicks;
    }

    private const int SeamHoldTicks = 30;

    private readonly List<ProbePending> _probePending = new();
    private readonly Dictionary<ActorId, SeamArm> _probeSeams = new();
    private readonly Dictionary<nint, SeamArm> _probeSeamsByAddress = new();
    private Hook<SetTimelineIdDelegate>? _probeTimelineHook;
    private bool _probeOurWrite;

    /// <summary>How long a probe waits for a fresh clone to answer reads
    /// before giving up, in ticks.</summary>
    private const int ProbeWaitTicks = 240;

    /// <summary>How many frames the seam method keeps imposing state.</summary>
    private const int SeamFrames = 600;

    public bool ProbeLogging => _probeTimelineHook?.IsEnabled == true;

    /// <summary>A trace line from callers that have no log of their own.</summary>
    public void ProbeTrace(string message) => _log.Information($"[AnimProbe] {message}");

    /// <summary>Render flags for step tracing (0 = visible).</summary>
    public int ProbeRenderFlags(ActorId actor)
    {
        var character = Resolve(actor, out _);
        return character == null ? -1 : (int)character->GameObject.RenderFlags;
    }

    /// <summary>Trace helper: "step -> flags".</summary>
    public void ProbeStep(ActorId actor, string step) =>
        _log.Information($"[AnimProbe] step {step} -> render {ProbeRenderFlags(actor)}");

    /// <summary>Experiment: CancelTimeline with chosen a2/a3.</summary>
    public AnimationPortResult ProbeCancel(ActorId actor, nint a2, nint a3)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);
        if (_cancelTimeline == null)
            return AnimationPortResult.Fail("CancelTimeline unavailable.");
        _cancelTimeline(&character->Timeline, a2, a3);
        return AnimationPortResult.Ok();
    }

    /// <summary>The native facts the debug bridge reports beside the
    /// session's view: emote, mode, the scheduler clocks (frames) and the
    /// child timeline cursors for the base and upper slots.</summary>
    public sealed class ProbeState
    {
        public uint EmoteId { get; set; }
        public byte Mode { get; set; }
        public float?[] SchedulerFrames { get; set; } = new float?[4];
        public float?[] ChildFrames { get; set; } = new float?[2];
        public bool WeaponHidden { get; set; }
        public bool HatHidden { get; set; }
        public bool VisorToggled { get; set; }
        public int RenderFlags { get; set; }
        public bool HasDrawObject { get; set; }
        public bool DrawObjectVisible { get; set; }
        public bool WeaponDrawn { get; set; }
    }

    public ProbeState? ProbeSnapshot(ActorId actor)
    {
        var character = Resolve(actor, out _);
        if (character == null)
            return null;
        var state = new ProbeState
        {
            EmoteId = character->EmoteController.EmoteId,
            Mode = (byte)character->Mode,
            WeaponHidden = character->DrawData.IsWeaponHidden,
            HatHidden = character->DrawData.IsHatHidden,
            VisorToggled = character->DrawData.IsVisorToggled,
            RenderFlags = (int)character->GameObject.RenderFlags,
            HasDrawObject = character->GameObject.DrawObject != null,
            DrawObjectVisible = character->GameObject.DrawObject != null
                && character->GameObject.DrawObject->IsVisible,
            WeaponDrawn = character->Timeline.IsWeaponDrawn,
        };
        for (int slot = 0; slot < 4; slot++)
        {
            var stamp = SchedulerTimestamp(&character->Timeline.TimelineSequencer, slot);
            state.SchedulerFrames[slot] = stamp != null ? *stamp : null;
            if (slot >= 2 || stamp == null)
                continue;
            // First track, first clip; ChildFrame when it is a child clip.
            nint sched = (nint)stamp - SchedulerTimestampOffset;
            nint tctl = *(nint*)(sched + 0x18);
            if (tctl == 0) continue;
            nint tracks = *(nint*)(tctl + 0x28);
            nint track = tracks != 0 ? *(nint*)tracks : 0;
            nint clips = track != 0 ? *(nint*)(track + 0x18) : 0;
            nint clip = clips != 0 ? *(nint*)clips : 0;
            if (clip != 0 && TryReadClockRegion(clip) && *(int*)(clip + 0x84) == 7)
                state.ChildFrames[slot] = *(float*)(clip + 0xCC);
        }
        return state;
    }

    /// <summary>The GAME's window-owned gaze, read from the look-at
    /// controller: GPose's face-camera writes LookMode.Position (3) at
    /// controller+0x38 and, at +0x40, the world point the camera held
    /// at the moment the toggle was flipped (a locked stare, not tracking)
    /// (proven by the three-dump diff, 2026-09-01). Null when the game
    /// holds no stare.</summary>
    public System.Numerics.Vector3? ProbeGameGaze(ActorId actor)
    {
        var character = Resolve(actor, out _);
        if (character == null)
            return null;
        var controller = (byte*)&character->LookAt.Controller;
        if (*(int*)(controller + 0x38) != 3)
            return null;
        return *(System.Numerics.Vector3*)(controller + 0x40);
    }

    // ── The write logger ──────────────────────────────────────────────

    /// <summary>Hooks the native set-timeline route and logs EVERY write
    /// with provenance — ours or the game's — so a race change with the
    /// switch on shows exactly what the rebuild writes and from where.</summary>
    public void ProbeSetTimelineLogging(bool enabled)
    {
        if (enabled && _probeTimelineHook == null)
        {
            try
            {
                var address = _sigScanner.ScanText(
                    "E8 ?? ?? ?? ?? 4C 8B BC 24 ?? ?? ?? ?? 4C 8D 9C 24 ?? ?? ?? ?? 49 8B 5B 40");
                _probeTimelineHook = _hooking.HookFromAddress<SetTimelineIdDelegate>(
                    address, ProbeTimelineDetour);
            }
            catch (Exception ex)
            {
                _log.Error($"[AnimProbe] timeline write hook failed: {ex.Message}");
                return;
            }
        }
        if (_probeTimelineHook == null)
            return;
        if (enabled && _probeCancelHook == null)
        {
            try
            {
                var cancelAddress = _sigScanner.ScanText("E8 ?? ?? ?? ?? 80 7B 17 01");
                _probeCancelHook = _hooking.HookFromAddress<CancelTimelineDelegate>(
                    cancelAddress, ProbeCancelDetour);
            }
            catch (Exception ex)
            {
                _log.Error($"[AnimProbe] cancel hook failed: {ex.Message}");
            }
        }
        if (enabled)
        {
            _probeTimelineHook.Enable();
            _probeCancelHook?.Enable();
        }
        else
        {
            _probeTimelineHook.Disable();
            _probeCancelHook?.Disable();
        }
        _log.Information($"[AnimProbe] timeline write logging {(enabled ? "ON" : "off")}");
    }

    private Hook<CancelTimelineDelegate>? _probeCancelHook;

    /// <summary>Log-only: does the slot death route through the game's
    /// CancelTimeline, or is it a silent natural completion?</summary>
    private nint ProbeCancelDetour(TimelineContainer* container, nint a2, nint a3)
    {
        try
        {
            var owner = container != null ? container->OwnerObject : null;
            string name = owner != null ? owner->GameObject.NameString : "<no owner>";
            _log.Information(
                $"[AnimProbe] CancelTimeline on {name} a2=0x{a2:X} a3=0x{a3:X} "
                + (_probeOurWrite ? "by POSER" : "by the game"));
        }
        catch
        {
        }
        return _probeCancelHook!.Original(container, a2, a3);
    }

    private bool ProbeTimelineDetour(
        ActionTimelineSequencer* sequencer, ushort id, nint context)
    {
        try
        {
            var container = (TimelineContainer*)((byte*)sequencer - TimelineSequencerOffset);
            var owner = container->OwnerObject;
            string name = owner != null
                ? owner->GameObject.NameString
                : "<no owner>";
            _log.Information(
                $"[AnimProbe] SetTimelineId {id} on {name} "
                + $"(owner 0x{(nint)owner:X}) "
                + (_probeOurWrite ? "by POSER" : "by the game"));
        }
        catch
        {
            // The log must never break the write.
        }
        return _probeTimelineHook!.Original(sequencer, id, context);
    }

    // ── Capture and dump ──────────────────────────────────────────────

    public RawTimelineCapture? ProbeCapture(ActorId actor)
    {
        var character = Resolve(actor, out _);
        if (character == null)
            return null;
        var capture = new RawTimelineCapture
        {
            Mode = (byte)character->Mode,
            ModeParam = ReadModeParam(character),
            EmoteId = character->EmoteController.EmoteId,
            BaseOverride = character->Timeline.BaseOverride,
            Forced = TryReadForcedTimeline(&character->Timeline, out var forced)
                ? forced : (ushort)0,
            LipsOverride = character->Timeline.LipsOverride,
            OverallSpeed = character->Timeline.OverallSpeed,
        };
        foreach (var slot in AnimationSlots.All)
        {
            int index = (int)slot;
            capture.SlotIds[index] =
                character->Timeline.TimelineSequencer.TimelineIds[index];
            capture.SlotSpeeds[index] =
                character->Timeline.TimelineSequencer.TimelineSpeeds[index];
        }
        foreach (var control in CollectControls(character, out _))
            capture.Controls.Add(
                (control.Id, control.Time, control.PlaybackSpeed));
        // The FIELD lies about paused speed: Poser's pause is the speed
        // hook rewriting the value after the game's calculation, so a
        // read here catches the game's x1. The enforcement is the truth.
        if (_enforcement.TryGetValue(actor, out var enforced))
        {
            if (enforced.OverallSpeed is { } overall)
                capture.OverallSpeed = overall;
            foreach (var (slot, speed) in enforced.SlotSpeeds)
                capture.SlotSpeeds[slot] = speed;
        }
        return capture;
    }

    /// <summary>One loud structured dump of everything the probe knows how
    /// to read, for eyeballing and twin-diffing in the log.</summary>
    public void ProbeDump(ActorId actor)
    {
        var capture = ProbeCapture(actor);
        if (capture == null)
        {
            _log.Information($"[AnimProbe] dump: {actor} did not answer a read.");
            return;
        }
        var character = Resolve(actor, out _);
        if (character != null)
        {
            // The game's own gaze lives in the look-at controller; the
            // hex block is for twin-diffing (game-gazing vs idle) until
            // the channel offsets are proven.
            var controller = (byte*)&character->LookAt.Controller;
            var hex = new StringBuilder(0x160 * 3 + 64);
            for (int offset = 0; offset < 0x160; offset++)
            {
                if (offset % 16 == 0)
                    hex.Append(CultureInfo.InvariantCulture,
                        $"\n  +{offset:x3} ");
                hex.Append(controller[offset].ToString(
                    "x2", CultureInfo.InvariantCulture));
                if (offset % 4 == 3)
                    hex.Append(' ');
            }
            // The prop hunt: emote props (held bread) ride the weapon
            // slots; each attached draw object carries its OWN skeleton
            // and animation clocks, which the actor's scrub never touches.
            var weapons = new StringBuilder(128);
            for (int slotIndex = 0; slotIndex < 3; slotIndex++)
            {
                ref var weapon = ref character->DrawData.Weapon(
                    (DrawDataContainer.WeaponSlot)slotIndex);
                weapons.Append(CultureInfo.InvariantCulture,
                    $"[{slotIndex}] id {weapon.ModelId.Id}"
                    + $".{weapon.ModelId.Type}.{weapon.ModelId.Variant}");
                var weaponDraw = weapon.DrawData.DrawObject;
                if (weaponDraw != null
                    && weaponDraw->Object.GetObjectType()
                        == ObjectType.CharacterBase
                    && ((CharacterBase*)weaponDraw)->Skeleton != null)
                {
                    var weaponSkeleton = ((CharacterBase*)weaponDraw)->Skeleton;
                    for (int p = 0; p < weaponSkeleton->PartialSkeletonCount; p++)
                    {
                        var animated = weaponSkeleton->PartialSkeletons[p]
                            .GetHavokAnimatedSkeleton(0);
                        if (animated == null)
                            continue;
                        for (int c = 0; c < animated->AnimationControls.Length; c++)
                        {
                            var control = animated->AnimationControls[c].Value;
                            if (control == null)
                                continue;
                            var wb = control->hkaAnimationControl.Binding;
                            float wd = wb.ptr != null && wb.ptr->Animation.ptr != null
                                ? wb.ptr->Animation.ptr->Duration : -1f;
                            weapons.Append(CultureInfo.InvariantCulture,
                                $" {p}.{c}@{control->hkaAnimationControl.LocalTime:0.00}"
                                + $"/{wd:0.00}x{control->PlaybackSpeed:0.##}");
                        }
                    }
                }
                weapons.Append("  ");
            }
            var clocks = new StringBuilder(48);
            for (int slot = 0; slot < 4; slot++)
            {
                var stamp = SchedulerTimestamp(
                    &character->Timeline.TimelineSequencer, slot);
                clocks.Append('[').Append(slot).Append(']')
                    .Append(stamp != null
                        ? (*stamp).ToString("0.00", CultureInfo.InvariantCulture)
                        : "-")
                    .Append(' ');
            }
            var gameGazeMode = *(int*)(controller + 0x38);
            var gameGazePoint = *(System.Numerics.Vector3*)(controller + 0x40);
            _log.Information(
                $"[AnimProbe] dump {actor}:\n{Describe(capture)}\n"
                + $"  scheduler clocks: {clocks}\n"
                + $"  game gaze: mode {gameGazeMode} at "
                + $"({gameGazePoint.X:0.##}, {gameGazePoint.Y:0.##}, "
                + $"{gameGazePoint.Z:0.##})\n"
                + $"  weapons: {weapons}\n"
                + $"  look-at controller:{hex}");
            return;
        }
        _log.Information($"[AnimProbe] dump {actor}:\n{Describe(capture)}");
    }

    private static string Describe(RawTimelineCapture c)
    {
        var text = new StringBuilder(256);
        text.Append(CultureInfo.InvariantCulture,
            $"  mode {c.Mode} param {c.ModeParam} emote {c.EmoteId} "
            + $"baseOverride {c.BaseOverride} ");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"forced {c.Forced} lips {c.LipsOverride} overall x{c.OverallSpeed:0.###}");
        text.Append("  slots ");
        foreach (var (index, id) in c.SlotIds)
        {
            if (id == 0 && Math.Abs(c.SlotSpeeds[index] - 1f) < 0.001f)
                continue;
            text.Append(CultureInfo.InvariantCulture,
                $"[{index}]={id}x{c.SlotSpeeds[index]:0.##} ");
        }
        text.AppendLine();
        text.Append("  controls ");
        foreach (var (id, time, speed) in c.Controls)
            text.Append(CultureInfo.InvariantCulture,
                $"{id}@{time:0.00}x{speed:0.##} ");
        return text.ToString();
    }

    // ── The three apply strategies ────────────────────────────────────

    /// <summary>Schedules a captured state onto a target actor (a fresh
    /// clone). The probe waits for the target to answer reads, applies by
    /// the chosen method, then logs verification compares.</summary>
    public void ProbeSchedule(
        ActorId target, RawTimelineCapture capture, ProbeMethod method,
        Action<ActorId>? apply = null, Action<ActorId>? reapply = null)
    {
        _probePending.Add(new ProbePending
        {
            Target = target,
            Capture = capture,
            Method = method,
            Apply = apply,
            Reapply = reapply,
            WaitTicks = ProbeWaitTicks,
        });
        _log.Information(
            $"[AnimProbe] scheduled {method} onto {target}:\n{Describe(capture)}");
    }

    private int _speedSampleTick;

    /// <summary>While any actor carries a slot-speed HOLD, one line every
    /// second states what the engine actually does with it: the overall
    /// field, the slot-speed fields, and the base/upper havok control
    /// clocks. The instrument for "the slider says 0 but it plays".</summary>
    private void ProbeSampleSpeedHolds()
    {
        if (++_speedSampleTick % 60 != 0)
            return;
        foreach (var (actor, enforcement) in _enforcement)
        {
            bool holds = false;
            foreach (var (_, speed) in enforcement.SlotSpeeds)
            {
                if (speed == 0f)
                {
                    holds = true;
                    break;
                }
            }
            if (!holds)
                continue;
            var character = Resolve(actor, out _);
            if (character == null)
                continue;
            var text = new StringBuilder(128);
            text.Append(CultureInfo.InvariantCulture,
                $"[AnimSpeed] {actor}: overall "
                + $"{character->Timeline.OverallSpeed:0.##} fields ");
            for (int slot = 0; slot < 4; slot++)
                text.Append(CultureInfo.InvariantCulture,
                    $"[{slot}]{character->Timeline.TimelineSequencer.TimelineSpeeds[slot]:0.##} ");
            text.Append("controls ");
            foreach (var control in CollectControls(character, out _))
            {
                if (control.Id.Partial == 0)
                    text.Append(CultureInfo.InvariantCulture,
                        $"{control.Id}@{control.Time:0.00}x{control.PlaybackSpeed:0.##} ");
            }
            _log.Information(text.ToString());
        }
    }

    private ActorId? _clockHuntActor;
    private float[]? _clockPrevious;
    private int[]? _clockScores;
    private int _clockTicks;
    // One-level pointer chase: heap objects the container references,
    // snapshotted at arm time. The container itself held no steady
    // clocks (hunt one), so the scheduler's clock lives behind one of
    // these or counts in frames/ms — hunt two watches for all three.
    private readonly List<nint> _clockPointers = new();
    private readonly List<string> _clockLabels = new();
    private float[]? _clockPtrPrevious;
    private int[]? _clockPtrScores;
    private const int ClockPtrBytes = 0x280;
    private const int ClockPtrMax = 64;
    private readonly float[] _clockPtrBuffer = new float[ClockPtrBytes / 4];

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = false)]
    private static extern bool ReadProcessMemory(
        nint process, nint address, void* buffer, nint size, out nint read);

    /// <summary>Reads a chased pointer's region through ReadProcessMemory
    /// so a freed or unmapped object fails the read instead of faulting
    /// the game — the 20:11 crash was a raw dereference here.</summary>
    private bool TryReadClockRegion(nint address)
    {
        fixed (float* buffer = _clockPtrBuffer)
        {
            return ReadProcessMemory(
                (nint)(-1), address, buffer, ClockPtrBytes, out var read)
                && read == ClockPtrBytes;
        }
    }

    /// <summary>The clock hunt: watches every float in the actor's
    /// TimelineContainer for ~3 seconds and reports the offsets that
    /// advance like a clock each tick. The scheduler's elapsed time — the
    /// second clock a real scrub must move — lives at one of them.</summary>
    public void ProbeFindClocks(ActorId actor)
    {
        _clockHuntActor = actor;
        _clockLabels.Clear();
        _clockPrevious = null;
        _clockScores = null;
        _clockTicks = 0;
        _log.Information($"[AnimProbe] clock hunt armed on {actor}.");
    }

    /// <summary>A per-tick advance that reads as a clock in any unit:
    /// seconds (~dt), frames (~1), or milliseconds (~16).</summary>
    private static bool ClockLikeDelta(float delta)
    {
        // A COUNTDOWN decrements clock-like — the completion timer that
        // survived three hunts scored only positive deltas (hunt four).
        float size = Math.Abs(delta);
        return size is (> 0.004f and < 0.12f) or (> 0.4f and < 2.5f)
            or (> 6f and < 40f);
    }

    /// <summary>An INTEGER counter advancing 1-3 per tick — invisible to
    /// the float scan (hunt three).</summary>
    private static bool ClockLikeIntDelta(int delta) => delta is >= 1 and <= 3;

    private void ProbeClockHuntTick()
    {
        if (_clockHuntActor is not { } actor)
            return;
        var character = Resolve(actor, out _);
        if (character == null)
            return;
        int size = TimelineContainerSize / 4;
        var basePtr = (float*)&character->Timeline;
        int ptrFloats = ClockPtrBytes / 4;
        if (_clockPrevious == null || _clockScores == null)
        {
            _clockPrevious = new float[size];
            _clockScores = new int[size];
            for (int i = 0; i < size; i++)
                _clockPrevious[i] = basePtr[i];
            // Arm the pointer chase: plausible heap pointers inside the
            // container, deduplicated, capped.
            _clockPointers.Clear();
            var qwords = (nint*)basePtr;
            for (int q = 0; q < size / 2 && _clockPointers.Count < ClockPtrMax; q++)
            {
                nint value = qwords[q];
                if (value > 0x10000 && value < 0x7FFF_FFFF_FFFF
                    && (value & 0x7) == 0 && !_clockPointers.Contains(value))
                {
                    _clockPointers.Add(value);
                    _clockLabels.Add($"qword{q * 8:x}");
                }
            }
            // The per-slot SchedulerTimeline objects come FIRST, then
            // their track controllers, tracks, and CLIPS (Ktisis
            // Structs/Animation layout): the completion cursor lives in
            // the clip layer if nowhere shallower.
            foreach (int huntSlot in new[] { 1, 0, 2, 3 })
            {
                var stamp = SchedulerTimestamp(
                    &character->Timeline.TimelineSequencer, huntSlot);
                if (stamp == null)
                    continue;
                nint scheduler = (nint)stamp - SchedulerTimestampOffset;
                _clockPointers.Add(scheduler);
                _clockLabels.Add($"sched{huntSlot}");
                // TimelineController.TrackController at +0x18.
                nint trackController = *(nint*)(scheduler + 0x18);
                if (trackController == 0)
                    continue;
                _clockPointers.Add(trackController);
                _clockLabels.Add($"trackCtl{huntSlot}");
                // PtrList<TimelineTrack> at +0x28: { T** Pointers,
                // ushort Capacity, ushort Length }.
                nint trackPointers = *(nint*)(trackController + 0x28);
                int trackCount = *(ushort*)(trackController + 0x28 + 0xA);
                for (int trackIndex = 0;
                    trackIndex < trackCount && trackIndex < 8
                        && trackPointers != 0
                        && _clockPointers.Count < ClockPtrMax;
                    trackIndex++)
                {
                    nint track = *(nint*)(trackPointers + trackIndex * 8);
                    if (track == 0)
                        continue;
                    _clockPointers.Add(track);
                    _clockLabels.Add($"track{huntSlot}.{trackIndex}");
                    // PtrList<BaseClip> at track+0x18.
                    nint clipPointers = *(nint*)(track + 0x18);
                    int clipCount = *(ushort*)(track + 0x18 + 0xA);
                    for (int clipIndex = 0;
                        clipIndex < clipCount && clipIndex < 8
                            && clipPointers != 0
                            && _clockPointers.Count < ClockPtrMax;
                        clipIndex++)
                    {
                        nint clip = *(nint*)(clipPointers + clipIndex * 8);
                        if (clip == 0)
                            continue;
                        _clockPointers.Add(clip);
                        // ClipType at clip+0x84 (Ktisis BaseClip).
                        int clipType = *(int*)(clip + 0x84);
                        _clockLabels.Add(
                            $"clip{huntSlot}.{trackIndex}.{clipIndex}"
                            + $"t{clipType}");
                        // A child clip (type 7) references its child
                        // controller somewhere in 0x98..0x160: chase every
                        // pointer-looking qword there, guarded. The hidden
                        // child clock that gates completion lives behind
                        // one of them (forward-scrub stall, 22:18).
                        if (clipType == 7 && TryReadClockRegion(clip))
                        {
                            for (int off = 0x98; off + 8 <= 0x160
                                && _clockPointers.Count < ClockPtrMax; off += 8)
                            {
                                nint target;
                                fixed (float* raw = _clockPtrBuffer)
                                    target = *(nint*)((byte*)raw + off);
                                if (target > 0x10000 && target < 0x7FFF_FFFF_FFFF
                                    && (target & 0x7) == 0
                                    && !_clockPointers.Contains(target))
                                {
                                    _clockPointers.Add(target);
                                    _clockLabels.Add(
                                        $"clip{huntSlot}.{trackIndex}.{clipIndex}"
                                        + $"+{off:x}->");
                                }
                            }
                        }
                    }
                }
            }
            // The base havok control's own block joins the chase: the
            // loop decision may live beside LocalTime (hunt three).
            var huntDraw = character->GameObject.DrawObject;
            if (huntDraw != null
                && huntDraw->Object.GetObjectType() == ObjectType.CharacterBase
                && ((CharacterBase*)huntDraw)->Skeleton != null)
            {
                var huntSkeleton = ((CharacterBase*)huntDraw)->Skeleton;
                for (int hp = 0; hp < huntSkeleton->PartialSkeletonCount
                    && _clockPointers.Count < ClockPtrMax; hp++)
                {
                    var huntAnimated = huntSkeleton->PartialSkeletons[hp]
                        .GetHavokAnimatedSkeleton(0);
                    if (huntAnimated == null)
                        continue;
                    for (int hc = 0; hc < huntAnimated->AnimationControls.Length
                        && _clockPointers.Count < ClockPtrMax; hc++)
                    {
                        var huntControl = huntAnimated->AnimationControls[hc].Value;
                        if (huntControl == null)
                            continue;
                        _clockPointers.Add((nint)huntControl);
                        _clockLabels.Add($"hka{hp}.{hc}");
                    }
                }
            }
            // A pointer that does not answer a guarded read is dropped
            // before the hunt starts (label kept in step).
            for (int drop = _clockPointers.Count - 1; drop >= 0; drop--)
            {
                if (!TryReadClockRegion(_clockPointers[drop]))
                {
                    _clockPointers.RemoveAt(drop);
                    _clockLabels.RemoveAt(drop);
                }
            }
            _log.Information(
                "[AnimProbe] clock hunt chasing: "
                + string.Join(" ", _clockLabels));
            _clockPtrPrevious = new float[_clockPointers.Count * ptrFloats];
            _clockPtrScores = new int[_clockPointers.Count * ptrFloats];
            for (int t = 0; t < _clockPointers.Count; t++)
            {
                if (!TryReadClockRegion(_clockPointers[t]))
                    continue;
                for (int i = 0; i < ptrFloats; i++)
                    _clockPtrPrevious[t * ptrFloats + i] = _clockPtrBuffer[i];
            }
            return;
        }
        for (int i = 0; i < size; i++)
        {
            float current = basePtr[i];
            if (ClockLikeDelta(current - _clockPrevious[i]) && float.IsFinite(current))
                _clockScores[i]++;
            else if (ClockLikeIntDelta(
                ((int*)basePtr)[i]
                - System.BitConverter.SingleToInt32Bits(_clockPrevious[i])))
                _clockScores[i]++;
            _clockPrevious[i] = current;
        }
        for (int t = 0; t < _clockPointers.Count; t++)
        {
            if (!TryReadClockRegion(_clockPointers[t]))
                continue;
            for (int i = 0; i < ptrFloats; i++)
            {
                float current = _clockPtrBuffer[i];
                int index = t * ptrFloats + i;
                if (ClockLikeDelta(current - _clockPtrPrevious![index]) &&
                    float.IsFinite(current))
                    _clockPtrScores![index]++;
                else if (ClockLikeIntDelta(
                    System.BitConverter.SingleToInt32Bits(current)
                    - System.BitConverter.SingleToInt32Bits(_clockPtrPrevious[index])))
                    _clockPtrScores![index]++;
                _clockPtrPrevious[index] = current;
            }
        }
        if (++_clockTicks < 180)
            return;
        var found = new StringBuilder(192);
        for (int i = 0; i < size; i++)
        {
            if (_clockScores[i] >= 150)
                found.Append(CultureInfo.InvariantCulture,
                    $"base+{i * 4:x3}={basePtr[i]:0.00} ");
        }
        for (int t = 0; t < _clockPointers.Count; t++)
        {
            for (int i = 0; i < ptrFloats; i++)
            {
                if (_clockPtrScores![t * ptrFloats + i] >= 150)
                    found.Append(CultureInfo.InvariantCulture,
                        $"{_clockLabels[t]}(0x{_clockPointers[t]:X})+{i * 4:x3}"
                        + $"={_clockPtrPrevious![t * ptrFloats + i]:0.00} ");
            }
        }
        _log.Information(
            "[AnimProbe] clock hunt done: "
            + (found.Length > 0 ? found.ToString() : "no steady clocks found."));
        _clockHuntActor = null;
        _clockPrevious = null;
        _clockScores = null;
        _clockPtrPrevious = null;
        _clockPtrScores = null;
        _clockPointers.Clear();
    }

    /// <summary>Runs from the port's framework tick.</summary>
    private void ProbeTick()
    {
        ProbeClockHuntTick();
        ProbeResetWatchTick();
        ProbeSampleSpeedHolds();
        if (_probePending.Count > 0)
            ProbePendingPass();
        if (_probeSeams.Count > 0)
            ProbeSeamTick();
    }

    private void ProbePendingPass()
    {
        for (int i = _probePending.Count - 1; i >= 0; i--)
        {
            var pending = _probePending[i];

            // Verification compares and the slot watch, then retirement.
            if (pending.VerifyIn.Count > 0)
            {
                ProbeWatch(pending);
                if (pending.ReapplyIn > 0 && --pending.ReapplyIn == 0)
                {
                    ProbeStep(pending.Target, "before second pass");
                    pending.Reapply?.Invoke(pending.Target);
                    ProbeStep(pending.Target, "after second pass");
                }
                for (int v = pending.VerifyIn.Count - 1; v >= 0; v--)
                {
                    if (--pending.VerifyIn[v] > 0)
                        continue;
                    pending.VerifyIn.RemoveAt(v);
                    ProbeVerify(pending.Target, pending.Capture, pending.Method);
                }
                if (pending.VerifyIn.Count == 0)
                    _probePending.RemoveAt(i);
                continue;
            }

            // Still waiting for the target to answer AND to render: a fresh
            // clone answers reads while still loading (render flags 0x900),
            // and acting on it then leaves the model-hidden bit stuck (the
            // invisible clones, 2026-09-01 22:46). Ready = flags 0.
            if (Read(pending.Target) == null || ProbeRenderFlags(pending.Target) != 0)
            {
                if (--pending.WaitTicks <= 0)
                {
                    _log.Information(
                        $"[AnimProbe] {pending.Method}: {pending.Target} "
                        + "never answered a read; giving up.");
                    _probePending.RemoveAt(i);
                }
                continue;
            }

            ProbeApply(pending);
        }
    }

    private void ProbeApply(ProbePending pending)
    {
        var capture = pending.Capture;
        var target = pending.Target;
        switch (pending.Method)
        {
            case ProbeMethod.Verbs:
            {
                ProbeStep(target, "verbs:start");
                if (capture.EmoteId != 0)
                {
                    var emote = PlayEmote(target, capture.EmoteId);
                    // Run three: PlayEmote landed hum's timeline but the
                    // clone stayed mode 1 and played it once — a typed
                    // emote's LOOP is the mode (3) plus its param (46 for
                    // hum), which the game's own emote flow sets. The
                    // native SetMode is that flow's mode half.
                    if (emote.Success
                        && (CharacterModes)capture.Mode == CharacterModes.EmoteLoop)
                    {
                        var looper = Resolve(target, out _);
                        if (looper != null)
                            looper->SetMode(
                                (CharacterModes)capture.Mode,
                                (byte)capture.ModeParam);
                    }
                    var after = Resolve(target, out _);
                    uint retained = after != null
                        ? after->EmoteController.EmoteId : 0u;
                    _log.Information(
                        $"[AnimProbe] Verbs: replayed EMOTE {capture.EmoteId} "
                        + $"mode {capture.Mode}/{capture.ModeParam} "
                        + $"on {target}: "
                        + (emote.Success ? "ok" : emote.Detail)
                        + $" (controller now reads emote {retained}).");
                    ProbeArmControlHold(target, capture);
                    break;
                }
                int played = 0;
                var baseTimeline = capture.BaseOverride != 0
                    ? capture.BaseOverride
                    : capture.SlotIds.GetValueOrDefault(0);
                if (baseTimeline != 0 &&
                    PlayBase(target, baseTimeline, null, out _).Success)
                    played++;
                foreach (var (index, id) in capture.SlotIds)
                {
                    if (index == 0 || id == 0)
                        continue;
                    if (Blend(target, id, null, out _).Success)
                        played++;
                }
                _log.Information(
                    $"[AnimProbe] Verbs: played {played} timeline(s) on {target}.");
                ProbeStep(target, "verbs:played");
                ProbeArmControlHold(target, capture);
                break;
            }
            case ProbeMethod.RawOnce:
            {
                var character = Resolve(target, out var detail);
                if (character == null)
                {
                    _log.Information($"[AnimProbe] RawOnce: {detail}");
                    return;
                }
                // The native set per slot, NO mode walking — then every raw
                // field verbatim, exactly once.
                int set = 0;
                if (_setTimelineId != null)
                {
                    foreach (var (index, id) in capture.SlotIds)
                    {
                        if (id == 0)
                            continue;
                        _probeOurWrite = true;
                        try
                        {
                            if (_setTimelineId(
                                &character->Timeline.TimelineSequencer, id, nint.Zero))
                                set++;
                        }
                        finally
                        {
                            _probeOurWrite = false;
                        }
                    }
                }
                ProbeWriteRawFields(character, capture);
                _log.Information(
                    $"[AnimProbe] RawOnce: native-set {set} slot(s), wrote raw "
                    + $"fields on {target}.");
                ProbeArmControlHold(target, capture);
                break;
            }
            case ProbeMethod.Seam:
            {
                _probeSeams[target] = new SeamArm
                {
                    Capture = capture,
                    FramesLeft = SeamFrames,
                };
                _log.Information(
                    $"[AnimProbe] Seam: armed {target} for {SeamFrames} frames.");
                break;
            }
            case ProbeMethod.Owned:
            {
                _log.Information($"[AnimProbe] Owned: replayed the session "
                    + $"record onto {target}.");
                ProbeArmControlHold(target, capture);
                break;
            }
        }
        // The caller's transfer runs for EVERY method: speeds and pause
        // travel through the SESSION so the owned record — and every
        // toggle describing it — is right. Port-level enforcement alone
        // paused the engine while the session said "playing".
        ProbeStep(target, "before session transfer");
        pending.Apply?.Invoke(target);
        ProbeStep(target, "after session transfer");
        if (pending.Reapply != null)
            pending.ReapplyIn = 30;
        pending.WaitTicks = 0;
        pending.VerifyIn.Add(2);
        pending.VerifyIn.Add(15);
        pending.VerifyIn.Add(60);
        pending.VerifyIn.Add(240);
    }

    /// <summary>Arms a controls-only seam hold: stored scrub times and
    /// speeds written at the seam until every control exists and took a
    /// write, then disarms and logs how long it took.</summary>
    private void ProbeArmControlHold(ActorId target, RawTimelineCapture capture)
    {
        if (capture.Controls.Count == 0)
            return;
        _probeSeams[target] = new SeamArm
        {
            Capture = capture,
            FramesLeft = SeamFrames,
            ControlsOnly = true,
        };
    }

    /// <summary>Whether every PAUSED stored control's live time already
    /// holds its stored value. Playing controls advance by design and are
    /// exempt; no paused controls means trivially held.</summary>
    private bool ProbePausedControlsHold(ActorId target, RawTimelineCapture capture)
    {
        var character = Resolve(target, out _);
        if (character == null)
            return false;
        List<ScrubControlReading>? live = null;
        foreach (var (id, time, speed) in capture.Controls)
        {
            if (Math.Abs(speed) >= 0.001f)
                continue;
            live ??= CollectControls(character, out _);
            bool held = false;
            foreach (var control in live)
            {
                if (control.Id == id)
                {
                    held = Math.Abs(control.Time - time) <= 0.05f;
                    break;
                }
            }
            if (!held)
                return false;
        }
        return true;
    }

    /// <summary>Logs every base-slot or mode flip on a watched target with
    /// its tick offset — the second-writer instrument.</summary>
    private void ProbeWatch(ProbePending pending)
    {
        var character = Resolve(pending.Target, out _);
        if (character == null)
            return;
        var slot0 = character->Timeline.TimelineSequencer.TimelineIds[0];
        var mode = (byte)character->Mode;
        pending.WatchTick++;
        if (!pending.WatchArmed)
        {
            pending.WatchArmed = true;
            pending.WatchSlot0 = slot0;
            pending.WatchMode = mode;
            return;
        }
        if (slot0 == pending.WatchSlot0 && mode == pending.WatchMode)
            return;
        _log.Information(
            $"[AnimProbe] watch {pending.Target}: slot0 "
            + $"{pending.WatchSlot0}->{slot0} mode "
            + $"{pending.WatchMode}->{mode} at +{pending.WatchTick} ticks");
        pending.WatchSlot0 = slot0;
        pending.WatchMode = mode;
    }

    /// <summary>Every raw field the capture holds, written verbatim. The
    /// slot ID FIELDS too — separate from the native set call so the seam
    /// method can test pure field ownership.</summary>
    private void ProbeWriteRawFields(Character* character, RawTimelineCapture capture)
    {
        // Run two: raw-writing EmoteLoop mode onto a mid-init clone left
        // it INVISIBLE (the game recovered the mode; the render never
        // did). Emote modes go through the emote machinery or not at all.
        if ((CharacterModes)capture.Mode != CharacterModes.EmoteLoop
            || character->Mode == CharacterModes.EmoteLoop)
        {
            character->Mode = (CharacterModes)capture.Mode;
            WriteModeParam(character, capture.ModeParam);
        }
        // An EmoteLoop mode raw-written onto a non-emoting actor is
        // skipped silently: proven render-killer (run two).
        character->Timeline.BaseOverride = capture.BaseOverride;
        character->Timeline.LipsOverride = capture.LipsOverride;
        character->Timeline.OverallSpeed = capture.OverallSpeed;
        TrySetForcedTimeline(&character->Timeline, capture.Forced);
        foreach (var (index, id) in capture.SlotIds)
            character->Timeline.TimelineSequencer.TimelineIds[index] = id;
        foreach (var (index, speed) in capture.SlotSpeeds)
            character->Timeline.TimelineSequencer.TimelineSpeeds[index] = speed;
    }

    private static int ProbeWriteControls(
        Character* character, RawTimelineCapture capture)
    {
        var drawObject = character->GameObject.DrawObject;
        if (drawObject == null ||
            drawObject->Object.GetObjectType() != ObjectType.CharacterBase)
            return 0;
        var charaBase = (CharacterBase*)drawObject;
        if (charaBase->Skeleton == null)
            return 0;
        var skeleton = charaBase->Skeleton;
        int wrote = 0;
        foreach (var (id, time, speed) in capture.Controls)
        {
            if (id.Partial >= skeleton->PartialSkeletonCount)
                continue;
            var animated = skeleton->PartialSkeletons[id.Partial]
                .GetHavokAnimatedSkeleton(0);
            if (animated == null || id.Control >= animated->AnimationControls.Length)
                continue;
            var control = animated->AnimationControls[id.Control].Value;
            if (control == null)
                continue;
            control->hkaAnimationControl.LocalTime = time;
            control->PlaybackSpeed = speed;
            wrote++;
        }
        return wrote;
    }

    // ── The seam method ───────────────────────────────────────────────

    /// <summary>Refreshes the address map the detour reads, checks arms
    /// for having HELD (the read matches the capture), retires them.</summary>
    private void ProbeSeamTick()
    {
        _probeSeamsByAddress.Clear();
        List<ActorId>? done = null;
        foreach (var (target, arm) in _probeSeams)
        {
            string kind = arm.ControlsOnly ? "ControlHold" : "Seam";
            if (arm.FramesLeft <= 0)
            {
                _log.Information(
                    $"[AnimProbe] {kind}: {target} ran out of frames after "
                    + $"{arm.FramesWritten} written; never completed alone.");
                (done ??= new()).Add(target);
                continue;
            }
            if (arm.ControlsOnly)
            {
                // "Wrote once" is not landed: an emote transitions
                // intro->loop a few ticks in and the loop RECREATES the
                // control at the same index — the SAME ScrubControlId —
                // restarting at 0, so a single good read-back can be the
                // dying intro control. Landed = every control written AND
                // every paused control's read-back holds its time for
                // CONSECUTIVE ticks, riding out the handoff.
                if (arm.ControlsLanded >= arm.Capture.Controls.Count
                    && ProbePausedControlsHold(target, arm.Capture))
                {
                    if (++arm.HeldTicks >= SeamHoldTicks)
                    {
                        _log.Information(
                            $"[AnimProbe] ControlHold: {target} held all "
                            + $"{arm.ControlsLanded} control(s) for "
                            + $"{SeamHoldTicks} tick(s) after "
                            + $"{arm.FramesWritten} frame(s); disarming.");
                        (done ??= new()).Add(target);
                        continue;
                    }
                }
                else
                {
                    arm.HeldTicks = 0;
                }
            }
            else if (ProbeMatches(target, arm.Capture, log: false))
            {
                // One matching tick is a lingering write, not a hold —
                // iteration one proved it. Demand consecutive ticks.
                if (++arm.HeldTicks >= SeamHoldTicks)
                {
                    _log.Information(
                        $"[AnimProbe] Seam: {target} HELD {SeamHoldTicks} "
                        + $"consecutive tick(s) after {arm.FramesWritten} "
                        + "written frame(s); disarming.");
                    (done ??= new()).Add(target);
                    continue;
                }
            }
            else
            {
                arm.HeldTicks = 0;
                if (arm.FramesWritten > 0 && arm.FramesWritten % 120 == 0)
                    ProbeMatches(target, arm.Capture, log: true);
            }
            var character = Resolve(target, out _);
            if (character != null)
                _probeSeamsByAddress[(nint)character] = arm;
        }
        if (done != null)
        {
            foreach (var target in done)
                _probeSeams.Remove(target);
        }
    }

    /// <summary>Called from the overall-speed detour, per container per
    /// frame, AFTER the game's own update — the write that wins.</summary>
    private void ProbeSeamPass(TimelineContainer* container)
    {
        if (_probeSeamsByAddress.Count == 0 || container == null)
            return;
        var owner = (nint)container->OwnerObject;
        if (owner == nint.Zero ||
            !_probeSeamsByAddress.TryGetValue(owner, out var arm))
            return;
        var character = (Character*)owner;
        if (!arm.ControlsOnly)
            ProbeWriteRawFields(character, arm.Capture);
        arm.ControlsLanded = ProbeWriteControls(character, arm.Capture);
        arm.FramesLeft--;
        arm.FramesWritten++;
    }

    // ── Verification ──────────────────────────────────────────────────

    private void ProbeVerify(
        ActorId target, RawTimelineCapture capture, ProbeMethod method)
    {
        // The settle seek: the scheduler and clip clocks re-derive a havok
        // control's time, so a bare LocalTime hold cannot land a scrub on a
        // clone (upper stuck at 2.0 vs the source's 10, 22:48). Seek every
        // stored slot control through the full-family scrub write instead,
        // on each verify tick, while the clone settles.
        if (method != ProbeMethod.Seam)
        {
            foreach (var (id, time, _) in capture.Controls)
            {
                if (id.Partial == 0)
                    SetControlTime(target, id, time, 0);
            }
        }
        bool match = ProbeMatches(target, capture, log: true, method);
        _log.Information(
            $"[AnimProbe] verify {method} on {target}: "
            + (match ? "MATCHES the capture." : "diverges (see compare)."));
    }

    /// <summary>Slot ids equal, paused control times within tolerance.
    /// Playing controls advance by design and only compare identity.</summary>
    private bool ProbeMatches(
        ActorId target, RawTimelineCapture capture, bool log,
        ProbeMethod method = ProbeMethod.Seam)
    {
        var live = ProbeCapture(target);
        if (live == null)
            return false;
        var diffs = log ? new StringBuilder(128) : null;
        bool match = true;
        foreach (var (index, id) in capture.SlotIds)
        {
            if (live.SlotIds.GetValueOrDefault(index) == id)
                continue;
            match = false;
            diffs?.Append(CultureInfo.InvariantCulture,
                $"slot[{index}] {live.SlotIds.GetValueOrDefault(index)}!={id} ");
        }
        if (live.BaseOverride != capture.BaseOverride)
        {
            match = false;
            diffs?.Append(CultureInfo.InvariantCulture,
                $"baseOverride {live.BaseOverride}!={capture.BaseOverride} ");
        }
        if (live.Mode != capture.Mode)
        {
            match = false;
            diffs?.Append(CultureInfo.InvariantCulture,
                $"mode {live.Mode}!={capture.Mode} ");
        }
        foreach (var (id, time, speed) in capture.Controls)
        {
            if (Math.Abs(speed) >= 0.001f)
                continue;
            float? liveTime = null;
            foreach (var (liveId, t, _) in live.Controls)
            {
                if (liveId == id)
                {
                    liveTime = t;
                    break;
                }
            }
            if (liveTime is { } held && Math.Abs(held - time) <= 0.05f)
                continue;
            match = false;
            diffs?.Append(CultureInfo.InvariantCulture,
                $"control {id} {liveTime?.ToString("0.00", CultureInfo.InvariantCulture) ?? "gone"}!={time:0.00} ");
        }
        if (diffs is { Length: > 0 })
            _log.Information($"[AnimProbe] compare {method}: {diffs}");
        return match;
    }
}
