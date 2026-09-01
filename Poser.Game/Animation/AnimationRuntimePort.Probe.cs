using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
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
        public int WaitTicks;
        public int SeekIn = -1;
        public readonly List<int> VerifyIn = new();
    }

    private sealed class SeamArm
    {
        public RawTimelineCapture Capture = null!;
        public int FramesLeft;
        public int FramesWritten;
    }

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
        if (enabled)
            _probeTimelineHook.Enable();
        else
            _probeTimelineHook.Disable();
        _log.Information($"[AnimProbe] timeline write logging {(enabled ? "ON" : "off")}");
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
        _log.Information($"[AnimProbe] dump {actor}:\n{Describe(capture)}");
    }

    private static string Describe(RawTimelineCapture c)
    {
        var text = new StringBuilder(256);
        text.Append(CultureInfo.InvariantCulture,
            $"  mode {c.Mode} param {c.ModeParam} baseOverride {c.BaseOverride} ");
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
        ActorId target, RawTimelineCapture capture, ProbeMethod method)
    {
        _probePending.Add(new ProbePending
        {
            Target = target,
            Capture = capture,
            Method = method,
            WaitTicks = ProbeWaitTicks,
        });
        _log.Information(
            $"[AnimProbe] scheduled {method} onto {target}:\n{Describe(capture)}");
    }

    /// <summary>Runs from the port's framework tick.</summary>
    private void ProbeTick()
    {
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

            // Staged one-shot seek for Verbs/RawOnce.
            if (pending.SeekIn > 0 && --pending.SeekIn == 0)
                ProbeSeekOnce(pending.Target, pending.Capture);

            // Verification compares, then retirement.
            if (pending.VerifyIn.Count > 0)
            {
                for (int v = pending.VerifyIn.Count - 1; v >= 0; v--)
                {
                    if (--pending.VerifyIn[v] > 0)
                        continue;
                    pending.VerifyIn.RemoveAt(v);
                    ProbeVerify(pending.Target, pending.Capture, pending.Method);
                }
                if (pending.VerifyIn.Count == 0 && pending.SeekIn <= 0)
                    _probePending.RemoveAt(i);
                continue;
            }

            // Still waiting for the target to answer.
            if (Read(pending.Target) == null)
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
                pending.SeekIn = 2;
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
                pending.SeekIn = 2;
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
        }
        pending.WaitTicks = 0;
        pending.VerifyIn.Add(2);
        pending.VerifyIn.Add(15);
        pending.VerifyIn.Add(60);
    }

    /// <summary>Every raw field the capture holds, written verbatim. The
    /// slot ID FIELDS too — separate from the native set call so the seam
    /// method can test pure field ownership.</summary>
    private void ProbeWriteRawFields(Character* character, RawTimelineCapture capture)
    {
        character->Mode = (CharacterModes)capture.Mode;
        WriteModeParam(character, capture.ModeParam);
        character->Timeline.BaseOverride = capture.BaseOverride;
        character->Timeline.LipsOverride = capture.LipsOverride;
        character->Timeline.OverallSpeed = capture.OverallSpeed;
        TrySetForcedTimeline(&character->Timeline, capture.Forced);
        foreach (var (index, id) in capture.SlotIds)
            character->Timeline.TimelineSequencer.TimelineIds[index] = id;
        foreach (var (index, speed) in capture.SlotSpeeds)
            character->Timeline.TimelineSequencer.TimelineSpeeds[index] = speed;
    }

    /// <summary>One seek round: every stored havok control time and speed,
    /// written once, results logged. No retries — retries are the smell
    /// this probe exists to kill.</summary>
    private void ProbeSeekOnce(ActorId target, RawTimelineCapture capture)
    {
        var character = Resolve(target, out var detail);
        if (character == null)
        {
            _log.Information($"[AnimProbe] seek: {detail}");
            return;
        }
        int wrote = ProbeWriteControls(character, capture);
        _log.Information(
            $"[AnimProbe] seek: wrote {wrote}/{capture.Controls.Count} "
            + $"control(s) on {target}.");
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
            if (arm.FramesLeft <= 0)
            {
                _log.Information(
                    $"[AnimProbe] Seam: {target} ran out of frames after "
                    + $"{arm.FramesWritten} written; state never held alone.");
                (done ??= new()).Add(target);
                continue;
            }
            if (ProbeMatches(target, arm.Capture, log: false))
            {
                _log.Information(
                    $"[AnimProbe] Seam: {target} HELD after "
                    + $"{arm.FramesWritten} written frame(s); disarming.");
                (done ??= new()).Add(target);
                continue;
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
        ProbeWriteRawFields(character, arm.Capture);
        ProbeWriteControls(character, arm.Capture);
        arm.FramesLeft--;
        arm.FramesWritten++;
    }

    // ── Verification ──────────────────────────────────────────────────

    private void ProbeVerify(
        ActorId target, RawTimelineCapture capture, ProbeMethod method)
    {
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
