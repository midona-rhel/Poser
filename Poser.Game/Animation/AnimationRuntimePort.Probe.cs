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
        public readonly Dictionary<ScrubControlId, float> ControlDurations = new();
        // Prop controls (attached weapon skeletons): clip length + time, so
        // a clone's props can be seeked by clip once they attach.
        public readonly List<(float Duration, float Time, float Speed)> Props = new();
        // Weapon slots (packed WeaponModelId per slot index): the clone
        // attaches its prop itself on frame one instead of waiting for
        // the timeline event.
        public readonly ulong[] WeaponModels = new ulong[3];
    }

    private Hook<SetTimelineIdDelegate>? _probeTimelineHook;
    private bool _probeOurWrite;

    public bool ProbeLogging => _probeTimelineHook?.IsEnabled == true;

    /// <summary>A trace line from callers that have no log of their own.</summary>
    public void ProbeTrace(string message) => _log.Information($"[AnimProbe] {message}");

    /// <summary>Render flags for step tracing (0 = visible).</summary>
    public int ProbeRenderFlags(ActorId actor)
    {
        var character = Resolve(actor, out _);
        return character == null ? -1 : (int)character->GameObject.RenderFlags;
    }

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
        if (enabled && _probeLoadWeaponHook == null)
        {
            try
            {
                _probeLoadWeaponHook = _hooking.HookFromAddress<LoadWeaponDelegate>(
                    DrawDataContainer.Addresses.LoadWeapon.Value, ProbeLoadWeaponDetour);
            }
            catch (Exception ex)
            {
                _log.Error($"[AnimProbe] LoadWeapon hook failed: {ex.Message}");
            }
        }
        if (enabled)
        {
            _probeTimelineHook.Enable();
            _probeCancelHook?.Enable();
            _probeLoadWeaponHook?.Enable();
        }
        else
        {
            _probeTimelineHook.Disable();
            _probeCancelHook?.Disable();
            _probeLoadWeaponHook?.Disable();
        }
        _log.Information($"[AnimProbe] timeline write logging {(enabled ? "ON" : "off")}");
    }

    private Hook<CancelTimelineDelegate>? _probeCancelHook;

    // LoadWeapon logger: the game's prop attach goes through here; the
    // exact arguments are what a direct attach must reproduce.
    private delegate void LoadWeaponDelegate(
        DrawDataContainer* container, DrawDataContainer.WeaponSlot slot, ulong modelId,
        byte a4, byte a5, byte a6, byte a7, bool a8);
    private Hook<LoadWeaponDelegate>? _probeLoadWeaponHook;

    private void ProbeLoadWeaponDetour(
        DrawDataContainer* container, DrawDataContainer.WeaponSlot slot, ulong modelId,
        byte a4, byte a5, byte a6, byte a7, bool a8)
    {
        try
        {
            _log.Information(
                $"[AnimProbe] LoadWeapon on container 0x{(nint)container:X} slot={slot} model=0x{modelId:X} "
                + $"a4={a4} a5={a5} a6={a6} a7={a7} a8={a8} "
                + (_probeOurWrite ? "by POSER" : "by the game"));
        }
        catch
        {
        }
        _probeLoadWeaponHook!.Original(container, slot, modelId, a4, a5, a6, a7, a8);
    }

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
        {
            capture.Controls.Add(
                (control.Id, control.Time, control.PlaybackSpeed));
            capture.ControlDurations[control.Id] = control.Duration;
        }
        for (int slotIndex = 0; slotIndex < 3; slotIndex++)
        {
            ref var weapon = ref character->DrawData.Weapon(
                (DrawDataContainer.WeaponSlot)slotIndex);
            var model = weapon.ModelId;
            capture.WeaponModels[slotIndex] = *(ulong*)&model;
        }
        ForEachPropControl(character, prop =>
        {
            var binding = prop->hkaAnimationControl.Binding;
            if (binding.ptr == null || binding.ptr->Animation.ptr == null)
                return;
            capture.Props.Add((
                binding.ptr->Animation.ptr->Duration,
                prop->hkaAnimationControl.LocalTime,
                prop->PlaybackSpeed));
        });
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
                var weaponDraw = weapon.DrawData.DrawObject;
                weapons.Append(string.Create(CultureInfo.InvariantCulture,
                    $"[{slotIndex}] id {weapon.ModelId.Id}.{weapon.ModelId.Type}.{weapon.ModelId.Variant}"));
                weapons.Append(weaponDraw == null
                    ? " nodraw"
                    : weaponDraw->IsVisible ? " draw+visible" : " draw+hidden");
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
    /// <summary>Logs every scheduler clip per slot, recursing into child
    /// timelines: type, track/clip start and length in frames (Ktisis
    /// BaseClip +0x50/+0x54/+0x64/+0x68). The pap's embedded TMLB matches
    /// this tree one for one (C031 attach = type 23, C094 fade = 78).</summary>
    public void ProbeClips(ActorId actor)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
        {
            _log.Information($"[AnimProbe] clips: {detail}");
            return;
        }
        var sequencer = &character->Timeline.TimelineSequencer;
        var handles = (ulong*)((byte*)sequencer + SequencerSchedulerHandlesOffset);
        var sb = new StringBuilder(1024);
        sb.Append($"[AnimProbe] clips on {actor}:");
        for (int slot = 0; slot < 14; slot++)
        {
            var handle = (SchedulerTimelineHandle*)handles[slot];
            if (handle == null || handle->Flags == 0 || handle->Data == 0)
                continue;
            nint sched = (nint)handle->Data;
            sb.Append(string.Create(CultureInfo.InvariantCulture,
                $"\n  slot {slot} sched 0x{sched:X} t={*(float*)(sched + SchedulerTimestampOffset):0.0}f"));
            DumpClips(sb, *(nint*)(sched + 0x18), 1);
        }
        _log.Information(sb.ToString());
    }

    private static void DumpClips(StringBuilder sb, nint trackController, int depth)
    {
        if (trackController == 0 || depth > 4 || !RegionReadable(trackController, 0x40))
            return;
        nint trackPointers = *(nint*)(trackController + 0x28);
        int trackCount = *(ushort*)(trackController + 0x28 + 0xA);
        string pad = new(' ', depth * 2 + 2);
        for (int t = 0; t < trackCount && t < 16 && trackPointers != 0; t++)
        {
            if (!RegionReadable(trackPointers + t * 8, 8))
                return;
            nint track = *(nint*)(trackPointers + t * 8);
            if (track == 0 || !RegionReadable(track, 0x30))
                continue;
            nint clipPointers = *(nint*)(track + 0x18);
            int clipCount = *(ushort*)(track + 0x18 + 0xA);
            for (int c = 0; c < clipCount && c < 32 && clipPointers != 0; c++)
            {
                if (!RegionReadable(clipPointers + c * 8, 8))
                    return;
                nint clip = *(nint*)(clipPointers + c * 8);
                if (clip == 0 || !RegionReadable(clip, 0x90))
                    continue;
                int type = *(int*)(clip + 0x84);
                sb.Append(string.Create(CultureInfo.InvariantCulture,
                    $"\n{pad}track {t} clip {c} type {type} @0x{clip:X} track {*(float*)(clip + 0x50):0.#}+{*(float*)(clip + 0x54):0.#} clip {*(float*)(clip + 0x64):0.#}+{*(float*)(clip + 0x68):0.#}"));
                if (type != 7 || !RegionReadable(clip, 0x140))
                    continue;
                nint child = *(nint*)(clip + 0x138);
                if (child == 0 || !RegionReadable(child, 0x80))
                    child = *(nint*)(clip + 0x130);
                if (child == 0 || !RegionReadable(child, 0x80))
                    continue;
                sb.Append(string.Create(CultureInfo.InvariantCulture,
                    $" child 0x{child:X} t={*(float*)(child + SchedulerTimestampOffset):0.0}f"));
                DumpClips(sb, *(nint*)(child + 0x18), depth + 1);
            }
        }
    }

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
    }

}
