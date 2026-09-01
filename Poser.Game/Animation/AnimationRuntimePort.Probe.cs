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
                            weapons.Append(CultureInfo.InvariantCulture,
                                $" {p}.{c}@{control->hkaAnimationControl.LocalTime:0.00}"
                                + $"x{control->PlaybackSpeed:0.##}");
                        }
                    }
                }
                weapons.Append("  ");
            }
            var gameGazeMode = *(int*)(controller + 0x38);
            var gameGazePoint = *(System.Numerics.Vector3*)(controller + 0x40);
            _log.Information(
                $"[AnimProbe] dump {actor}:\n{Describe(capture)}\n"
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

    /// <summary>Runs from the port's framework tick.</summary>
    private void ProbeTick()
    {
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
                    pending.Reapply?.Invoke(pending.Target);
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
        pending.Apply?.Invoke(target);
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
