using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Poser.Application.Animation;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using Poser.Entities;
using Poser.Game.Bindings;

namespace Poser.Game.Animation;

/// <summary>
/// The native side of animation. Resolves every stable id through
/// <see cref="StableBindingRegistry"/> immediately before touching memory,
/// so a redraw or removal fails explicitly instead of writing through a
/// stale pointer.
///
/// Addresses exist ONLY inside this class. The speed detours fire on the
/// game's thread with a raw pointer and must answer without allocating or
/// re-scanning, so an address index is kept as a DERIVED cache: the
/// ActorId-keyed enforcement table is authoritative, the index is
/// rebuilt from it whenever an override changes or the scene refreshes.
///
/// Speed is enforced, not written once. The game recalculates its own
/// speeds every frame; the overall-speed detour therefore lets the
/// original run and then stomps the result (Brio's model), and the
/// slot-speed detour substitutes the argument before the original runs.
/// </summary>
public sealed unsafe class AnimationRuntimePort : IAnimationRuntimePort, IDisposable
{
    private readonly IFramework _framework;
    private readonly IClientState _clientState;
    private readonly IPluginLog _log;
    private readonly StableBindingRegistry _bindings;
    private readonly PosingService _posing;
    private readonly AnimationSlotProbe _slotProbe;
    private readonly byte[] _slotProbeSalt = RandomNumberGenerator.GetBytes(16);

    // Authoritative, stable-id keyed.
    private readonly Dictionary<ActorId, Enforcement> _enforcement = new();
    // Derived index for the detours only; never a source of truth.
    private readonly Dictionary<nint, Enforcement> _byAddress = new();
    // Actors whose position lock THIS session created, so releasing it
    // cannot wipe a placement the user made with the gizmo.
    private readonly HashSet<ActorId> _positionLocks = new();

    private sealed class Enforcement
    {
        public float? OverallSpeed;
        public readonly Dictionary<int, float> SlotSpeeds = new();
        public bool IsEmpty => OverallSpeed == null && SlotSpeeds.Count == 0;
    }

    private delegate bool CalculateAndApplyOverallSpeedDelegate(TimelineContainer* container);
    private readonly Hook<CalculateAndApplyOverallSpeedDelegate>? _speedHook;

    private delegate void SetSlotSpeedDelegate(ActionTimelineSequencer* sequencer, uint slot, float speed);
    private readonly Hook<SetSlotSpeedDelegate>? _slotSpeedHook;

    // Non-null is NOT enabled: a hook object can exist while its Enable
    // (or the other hook's construction) failed. Commands gate on these,
    // set only after the matching Enable returned.
    private readonly bool _overallSpeedHookEnabled;
    private readonly bool _slotSpeedHookEnabled;

    // Stance transition natives (Ktisis AnimationModule). ClientStructs maps
    // the structs but not these three entry points, so they are sig-scanned.
    // Every struct member they touch is a verified ClientStructs symbol.
    private delegate bool SetEmoteModeDelegate(EmoteController* controller, uint mode);
    private readonly SetEmoteModeDelegate? _setEmoteMode;
    private delegate nint CancelTimelineDelegate(TimelineContainer* container, nint a2, nint a3);
    private readonly CancelTimelineDelegate? _cancelTimeline;
    // ClientStructs binds PlayEmote as PlayEmote(uint, PlayEmoteOption*),
    // and passing a null option pointer faults inside the game. The
    // reference calls a four-argument form with zeros for option and
    // chair, which is the shape that is actually known to work, so that
    // is the one used here.
    private delegate bool PlayEmoteDelegate(
        EmoteController* controller, nint emoteId, nint option, nint chair);
    private readonly PlayEmoteDelegate? _playEmote;

    /// <summary>Ktisis' EmoteModeEnum. These are argument VALUES for
    /// SetEmoteMode, not struct offsets.</summary>
    private const uint EmoteModeNormal = 0;
    private const uint EmoteModeSitGround = 1;
    private const uint EmoteModeSitChair = 2;
    private const uint EmoteModeSleeping = 3;

    private readonly Lumina.Excel.ExcelSheet<Lumina.Excel.Sheets.ActionTimeline>? _timelineSheet;

    // The physics freeze is a process-global code patch, not a per-actor
    // enforcement; the patcher owns its site, capability state and restore.
    private readonly PhysicsFreezePatcher _physics;

    public AnimationRuntimePort(
        IFramework framework,
        IClientState clientState,
        ISigScanner sigScanner,
        IGameInteropProvider hooking,
        IPluginLog log,
        StableBindingRegistry bindings,
        PosingService posing,
        IDataManager data)
    {
        _framework = framework;
        _clientState = clientState;
        _timelineSheet = data.GetExcelSheet<Lumina.Excel.Sheets.ActionTimeline>();
        _log = log;
        _slotProbe = new AnimationSlotProbe(message => _log.Information(message));
        _bindings = bindings;
        _posing = posing;
        _framework.Update += OnFrameworkUpdate;

        // A missing stance native degrades that one operation to an explicit
        // failure; it never silently half-applies a transition.
        _setEmoteMode = ScanDelegate<SetEmoteModeDelegate>(
            sigScanner, "E8 ?? ?? ?? ?? F6 46 10 01", "SetEmoteMode");
        _cancelTimeline = ScanDelegate<CancelTimelineDelegate>(
            sigScanner, "E8 ?? ?? ?? ?? 80 7B 17 01", "CancelTimeline");
        _playEmote = ScanDelegate<PlayEmoteDelegate>(
            sigScanner, "E8 ?? ?? ?? ?? 88 45 68", "PlayEmote");

        // Each hook is constructed AND enabled independently, and only a
        // completed Enable sets its flag: a failure in either step, or in
        // the other hook, can never leave a command believing a non-null
        // but inactive hook is enforcing anything.
        try
        {
            var speedAddress = sigScanner.ScanText(
                "E8 ?? ?? ?? ?? 48 8D 8B ?? ?? ?? ?? 48 8B 01 FF 50 ?? 48 8D 8B ?? ?? ?? ?? 48 8B 01 FF 50 ?? F6 83");
            _speedHook = hooking.HookFromAddress<CalculateAndApplyOverallSpeedDelegate>(
                speedAddress, OverallSpeedDetour);
            _speedHook.Enable();
            _overallSpeedHookEnabled = true;
        }
        catch (Exception ex)
        {
            _log.Error($"Overall-speed hook unavailable; overall speed overrides will fail explicitly: {ex.Message}");
        }

        try
        {
            _slotSpeedHook = hooking.HookFromAddress<SetSlotSpeedDelegate>(
                ActionTimelineSequencer.Addresses.SetSlotSpeed.Value, SlotSpeedDetour);
            _slotSpeedHook.Enable();
            _slotSpeedHookEnabled = true;
        }
        catch (Exception ex)
        {
            _log.Error($"Slot-speed hook unavailable; layer speed overrides will fail explicitly: {ex.Message}");
        }

        // Scan, byte validation, capability state and restore all live in
        // the patcher; an unavailable site degrades SetPhysicsFrozen to an
        // explicit failure with the patcher's own detail.
        _physics = new PhysicsFreezePatcher(sigScanner, log);
    }

    private T? ScanDelegate<T>(ISigScanner scanner, string signature, string name)
        where T : Delegate
    {
        try
        {
            if (scanner.TryScanText(signature, out var address) && address != nint.Zero)
                return Marshal.GetDelegateForFunctionPointer<T>(address);
            _log.Warning($"Animation: {name} signature not found; stance changes will fail explicitly.");
        }
        catch (Exception ex)
        {
            _log.Warning($"Animation: {name} scan failed ({ex.Message}); stance changes will fail explicitly.");
        }
        return null;
    }

    // ── Resolution ────────────────────────────────────────────────────

    private Character* Resolve(ActorId actor, out string? detail)
    {
        detail = null;
        if (!_framework.IsInFrameworkUpdateThread)
        {
            detail = "Animation writes must run on the framework thread.";
            return null;
        }
        var resolved = _bindings.Resolve(actor);
        if (!resolved.Success || resolved.Value is not { } legacy || legacy.Address == nint.Zero)
        {
            detail = resolved.Detail ?? $"Actor {actor} is no longer available.";
            return null;
        }
        var character = (Character*)legacy.Address;
        return character == null ? null : character;
    }

    private IActor? ResolveActor(ActorId actor)
    {
        if (!_framework.IsInFrameworkUpdateThread)
            return null;
        var resolved = _bindings.Resolve(actor);
        return resolved.Success ? resolved.Value : null;
    }

    public bool IsSupported(ActorId actor)
    {
        if (!_framework.IsInFrameworkUpdateThread)
            return false;
        var resolved = _bindings.Resolve(actor);
        return resolved.Success && resolved.Value is { CanControlAnimation: true };
    }

    // ── Enforcement index ─────────────────────────────────────────────

    private Enforcement EnforcementFor(ActorId actor)
    {
        if (!_enforcement.TryGetValue(actor, out var value))
            _enforcement[actor] = value = new Enforcement();
        return value;
    }

    private void PruneEnforcement(ActorId actor)
    {
        if (_enforcement.TryGetValue(actor, out var value) && value.IsEmpty)
            _enforcement.Remove(actor);
        SyncEnforcementIndex();
    }

    /// <summary>
    /// Rebuilds the detour-facing address index from the stable-id table.
    /// Must run on the framework thread; called after every override
    /// change and once per structural scene change, which is what keeps a
    /// redrawn actor from inheriting the previous body's enforcement.
    /// </summary>
    public void SyncEnforcementIndex()
    {
        if (!_framework.IsInFrameworkUpdateThread)
            return;
        _byAddress.Clear();
        foreach (var (id, enforcement) in _enforcement)
        {
            var resolved = _bindings.Resolve(id);
            if (resolved.Success && resolved.Value is { } legacy && legacy.Address != nint.Zero)
                _byAddress[legacy.Address] = enforcement;
        }
    }

    private bool OverallSpeedDetour(TimelineContainer* container)
    {
        bool result = _speedHook!.Original(container);
        if (container == null)
            return result;
        var owner = (nint)container->OwnerObject;
        if (owner != nint.Zero &&
            _byAddress.TryGetValue(owner, out var enforcement) &&
            enforcement.OverallSpeed is { } speed)
        {
            // Run AFTER the game's own calculation so the override wins
            // whatever the game just decided.
            container->OverallSpeed = speed;
            return true;
        }
        return result;
    }

    private void SlotSpeedDetour(ActionTimelineSequencer* sequencer, uint slot, float speed)
    {
        float finalSpeed = speed;
        var owner = (nint)sequencer->Parent;
        if (owner != nint.Zero &&
            _byAddress.TryGetValue(owner, out var enforcement) &&
            enforcement.SlotSpeeds.TryGetValue((int)slot, out var overrideSpeed))
        {
            finalSpeed = overrideSpeed;
        }
        _slotSpeedHook!.Original(sequencer, slot, finalSpeed);
    }

    // ── Reads ─────────────────────────────────────────────────────────

    public ActorAnimationReading? Read(ActorId actor)
    {
        var character = Resolve(actor, out _);
        if (character == null)
            return null;

        var slots = new List<AnimationSlotReading>(AnimationSlots.All.Count);
        foreach (var slot in AnimationSlots.All)
        {
            int index = (int)slot;
            slots.Add(new AnimationSlotReading(
                slot,
                character->Timeline.TimelineSequencer.TimelineIds[index],
                character->Timeline.TimelineSequencer.TimelineSpeeds[index]));
        }

        var controls = CollectControls(character, out var token);
        // The RAW pose family: collapsing WeaponDrawn/Umbrella/Accessory to
        // Idle made the UI lie about the current state, which in turn made
        // Idle unreachable (re-selecting what the control already showed).
        var poseType = character->EmoteController.CurrentPoseType;
        var stance = poseType switch
        {
            EmoteController.PoseType.WeaponDrawn => AnimationStance.WeaponDrawn,
            EmoteController.PoseType.Sit => AnimationStance.SitChair,
            EmoteController.PoseType.GroundSit => AnimationStance.SitGround,
            EmoteController.PoseType.Doze => AnimationStance.Sleeping,
            EmoteController.PoseType.Umbrella => AnimationStance.Umbrella,
            EmoteController.PoseType.Accessory => AnimationStance.Accessory,
            _ => AnimationStance.Idle,
        };

        return new ActorAnimationReading(
            character->Timeline.BaseOverride,
            character->Timeline.OverallSpeed,
            character->Timeline.LipsOverride,
            character->Timeline.IsWeaponDrawn,
            stance,
            character->EmoteController.CPoseState,
            slots,
            controls,
            token);
    }

    public AnimationPortResult StartSlotProbe(ActorId actor)
    {
        if (_slotProbe.HasActive)
            return AnimationPortResult.Fail("A slot probe is already active.");
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);
        return _slotProbe.Start(
            actor,
            SlotProbeFingerprint((nint)character),
            CaptureSlotProbeSnapshot(character));
    }

    public AnimationPortResult StopSlotProbe(ActorId actor)
    {
        if (!_slotProbe.IsActiveFor(actor))
            return AnimationPortResult.Fail("No slot probe is active for this actor.");
        var character = Resolve(actor, out _);
        return character == null
            ? _slotProbe.Stop(actor, string.Empty, null)
            : _slotProbe.Stop(
                actor,
                SlotProbeFingerprint((nint)character),
                CaptureSlotProbeSnapshot(character));
    }

    public void BeginSlotProbeCommand(ActorId actor, AnimationProbeCommand command)
    {
        if (!_slotProbe.IsActiveFor(actor))
            return;
        var character = Resolve(actor, out _);
        if (character == null)
        {
            _slotProbe.Tick(string.Empty, null, _clientState.IsGPosing);
            return;
        }
        _slotProbe.Begin(
            actor,
            SlotProbeFingerprint((nint)character),
            command,
            CaptureSlotProbeSnapshot(character));
    }

    public void CompleteSlotProbeCommand(
        ActorId actor, AnimationProbeCommand command, bool success)
    {
        if (!_slotProbe.IsActiveFor(actor))
            return;
        var character = Resolve(actor, out _);
        if (character == null)
        {
            _slotProbe.Tick(string.Empty, null, _clientState.IsGPosing);
            return;
        }
        _slotProbe.Complete(
            actor,
            SlotProbeFingerprint((nint)character),
            command,
            success,
            CaptureSlotProbeSnapshot(character));
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        EnforceLoops(framework);
        ObserveSlotProbe();
    }

    private void ObserveSlotProbe()
    {
        if (!_slotProbe.HasActive)
            return;
        if (!_clientState.IsGPosing)
        {
            _slotProbe.Tick(string.Empty, null, false);
            return;
        }
        var actor = _slotProbe.ActiveActor!.Value;
        var character = Resolve(actor, out _);
        if (character == null)
        {
            _slotProbe.Tick(string.Empty, null, true);
            return;
        }
        _slotProbe.Tick(
            SlotProbeFingerprint((nint)character),
            CaptureSlotProbeSnapshot(character),
            true);
    }

    private SlotProbeSnapshot CaptureSlotProbeSnapshot(Character* character)
    {
        var sequencer = &character->Timeline.TimelineSequencer;
        var primary = new string[14];
        var secondary = new string[14];
        var tertiary = new string[14];
        var quaternary = new string[14];
        var speeds = new string[14];
        for (int index = 0; index < primary.Length; index++)
        {
            primary[index] = sequencer->TimelineIds[index].ToString(CultureInfo.InvariantCulture);
            secondary[index] = sequencer->TimelineIds2[index].ToString(CultureInfo.InvariantCulture);
            tertiary[index] = sequencer->TimelineIds3[index].ToString(CultureInfo.InvariantCulture);
            quaternary[index] = sequencer->TimelineIds4[index].ToString(CultureInfo.InvariantCulture);
            speeds[index] = sequencer->TimelineSpeeds[index].ToString("0.###", CultureInfo.InvariantCulture);
        }
        var controls = CaptureSlotProbeControls(character);
        return new SlotProbeSnapshot(
            $"mode={(byte)character->Mode} modeParam={(uint)character->ModeParam} " +
            $"base={character->Timeline.BaseOverride} lips={character->Timeline.LipsOverride} " +
            $"timeline=[{string.Join(',', primary)}] current=[{string.Join(',', secondary)}] " +
            $"previous=[{string.Join(',', tertiary)}] aux=[{string.Join(',', quaternary)}] " +
            $"speed=[{string.Join(',', speeds)}] controls=[{string.Join(',', controls)}]",
            controls);
    }

    private List<SlotProbeControl> CaptureSlotProbeControls(Character* character)
    {
        var result = new List<SlotProbeControl>();
        var drawObject = character->GameObject.DrawObject;
        if (drawObject == null || drawObject->Object.GetObjectType() != ObjectType.CharacterBase)
            return result;
        var charaBase = (CharacterBase*)drawObject;
        if (charaBase->Skeleton == null)
            return result;
        var skeleton = charaBase->Skeleton;
        for (int partialIndex = 0; partialIndex < skeleton->PartialSkeletonCount; partialIndex++)
        {
            var partial = &skeleton->PartialSkeletons[partialIndex];
            var animated = partial->GetHavokAnimatedSkeleton(0);
            if (animated == null)
                continue;
            for (int controlIndex = 0; controlIndex < animated->AnimationControls.Length; controlIndex++)
            {
                var control = animated->AnimationControls[controlIndex].Value;
                if (control == null)
                    continue;
                var binding = control->hkaAnimationControl.Binding;
                if (binding.ptr == null || binding.ptr->Animation.ptr == null)
                    continue;
                result.Add(new SlotProbeControl(
                    $"{partialIndex}.{controlIndex}",
                    SlotProbeFingerprint((nint)control),
                    $"time={control->hkaAnimationControl.LocalTime.ToString("0.###", CultureInfo.InvariantCulture)} " +
                    $"duration={binding.ptr->Animation.ptr->Duration.ToString("0.###", CultureInfo.InvariantCulture)} " +
                    $"speed={control->PlaybackSpeed.ToString("0.###", CultureInfo.InvariantCulture)}"));
            }
        }
        return result;
    }

    private string SlotProbeFingerprint(nint value)
    {
        Span<byte> input = stackalloc byte[_slotProbeSalt.Length + sizeof(long)];
        _slotProbeSalt.CopyTo(input);
        BitConverter.TryWriteBytes(input[_slotProbeSalt.Length..], (long)value);
        return Convert.ToHexString(SHA256.HashData(input)[..6]);
    }

    /// <summary>
    /// Walks the live skeleton for every valid Havok control. Nothing is
    /// cached: Brio re-walks from the draw object every time for exactly
    /// this reason, so a replaced skeleton simply yields a different set
    /// rather than a dangling pointer.
    /// </summary>
    private static List<ScrubControlReading> CollectControls(Character* character, out ulong token)
    {
        token = 0;
        var result = new List<ScrubControlReading>();
        var drawObject = character->GameObject.DrawObject;
        if (drawObject == null || drawObject->Object.GetObjectType() != ObjectType.CharacterBase)
            return result;
        var charaBase = (CharacterBase*)drawObject;
        if (charaBase->Skeleton == null)
            return result;
        var skeleton = charaBase->Skeleton;

        for (int p = 0; p < skeleton->PartialSkeletonCount; p++)
        {
            var partial = &skeleton->PartialSkeletons[p];
            var animated = partial->GetHavokAnimatedSkeleton(0);
            if (animated == null)
                continue;
            for (int c = 0; c < animated->AnimationControls.Length; c++)
            {
                var control = animated->AnimationControls[c].Value;
                if (control == null)
                    continue;
                var binding = control->hkaAnimationControl.Binding;
                if (binding.ptr == null || binding.ptr->Animation.ptr == null)
                    continue;
                result.Add(new ScrubControlReading(
                    new ScrubControlId(p, c),
                    control->hkaAnimationControl.LocalTime,
                    binding.ptr->Animation.ptr->Duration,
                    control->PlaybackSpeed));
            }
        }

        // The token identifies THIS skeleton and control layout. A redraw
        // moves the skeleton and changes the count, so a scrub captured
        // under the old token is refused rather than written blind.
        token = unchecked(((ulong)(nint)skeleton * 397) ^ (ulong)result.Count);
        return result;
    }

    public IReadOnlyList<ScrubControlReading> EnumerateControls(ActorId actor, out ulong token)
    {
        token = 0;
        var character = Resolve(actor, out _);
        return character == null
            ? Array.Empty<ScrubControlReading>()
            : CollectControls(character, out token);
    }

    /// <summary>
    /// The control that actually drives a slot, using Ktisis' lookup: the
    /// control INDEX equals the slot index, and the partials are searched
    /// for the first one holding a valid control at that index. Labelling
    /// the first two flattened controls "Full body" and "Upper body" is
    /// not the same thing and is wrong whenever a partial contributes a
    /// different number of controls.
    ///
    /// Gated on the slot actually playing something, because an empty slot
    /// has no meaningful time to scrub. Only Base and UpperBody are
    /// offered: Ktisis notes the index-equals-slot correspondence does not
    /// hold for the facial, additive, and lip slots, which live on other
    /// partials.
    /// </summary>
    public ScrubControlReading? FindSlotControl(
        ActorId actor, AnimationSlot slot, out ulong token)
    {
        token = 0;
        var character = Resolve(actor, out _);
        if (character == null)
            return null;
        if (slot is not (AnimationSlot.Base or AnimationSlot.UpperBody))
            return null;
        int index = (int)slot;
        if (character->Timeline.TimelineSequencer.TimelineIds[index] == 0)
            return null;

        var drawObject = character->GameObject.DrawObject;
        if (drawObject == null || drawObject->Object.GetObjectType() != ObjectType.CharacterBase)
            return null;
        var charaBase = (CharacterBase*)drawObject;
        if (charaBase->Skeleton == null)
            return null;
        var skeleton = charaBase->Skeleton;
        token = CurrentToken(skeleton);

        for (int p = 0; p < skeleton->PartialSkeletonCount; p++)
        {
            var partial = &skeleton->PartialSkeletons[p];
            var animated = partial->GetHavokAnimatedSkeleton(0);
            if (animated == null || index >= animated->AnimationControls.Length)
                continue;
            var control = animated->AnimationControls[index].Value;
            if (control == null)
                continue;
            var binding = control->hkaAnimationControl.Binding;
            if (binding.ptr == null || binding.ptr->Animation.ptr == null)
                continue;
            return new ScrubControlReading(
                new ScrubControlId(p, index),
                control->hkaAnimationControl.LocalTime,
                binding.ptr->Animation.ptr->Duration,
                control->PlaybackSpeed);
        }
        return null;
    }

    // ── Base, blend, loop ─────────────────────────────────────────────

    /// <summary>
    /// The reference's play, verbatim (Ktisis AnimationManager.PlayTimeline
    /// minus its forced-timeline write): a sheet-Pause timeline holds the
    /// actor by entering EmoteLoop with parameter 0; playing a normal
    /// timeline while still in that held state first returns the mode to
    /// Normal, because the held mode otherwise eats the play. A stale
    /// AnimLock latch (an older Poser build's base model, or Brio) is
    /// dismantled the same way — it re-drives its own timeline over
    /// anything played here, which is what made layering impossible.
    /// </summary>
    public AnimationPortResult Blend(ActorId actor, ushort timeline,
        BaseAnimationCapture? existing, out BaseAnimationCapture? captured)
    {
        captured = null;
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);

        if (existing == null)
        {
            captured = new BaseAnimationCapture(
                (byte)character->Mode,
                character->ModeParam,
                character->Timeline.BaseOverride,
                // The timeline actually PLAYING on the base slot, so a
                // restore can put back what the actor was doing rather
                // than a blanket idle.
                character->Timeline.TimelineSequencer.TimelineIds[0]);
        }

        PlayWithMode(character, timeline);
        return AnimationPortResult.Ok();
    }

    /// <summary>The slot the sheet's Stance column routes a timeline
    /// onto, or null when the row is missing or unmapped.</summary>
    public AnimationSlot? TimelineSlot(ushort timeline)
    {
        var stance = _timelineSheet?.GetRowOrDefault(timeline)?.Stance;
        return stance is { } value && AnimationSlots.IsKnown(value)
            ? (AnimationSlot)value
            : null;
    }

    /// <summary>
    /// The game's OWN timeline cancellation — the sig-scanned function the
    /// stance transition already uses (Ktisis' SetPose flow). It stops
    /// what the container is currently driving; there is no proven
    /// per-slot stop in either reference, so this is container-wide and
    /// callers rebuild the base afterwards.
    /// </summary>
    public AnimationPortResult CancelActiveTimeline(ActorId actor)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);
        if (_cancelTimeline == null)
            return AnimationPortResult.Fail(
                "Timeline cancellation is unavailable: the game function was not found.");
        _cancelTimeline(&character->Timeline, nint.Zero, nint.Zero);
        return AnimationPortResult.Ok();
    }

    /// <summary>The base restore point as it stands right now, for plays
    /// that go through the emote entry point rather than Blend.</summary>
    public BaseAnimationCapture? CaptureBase(ActorId actor)
    {
        var character = Resolve(actor, out _);
        if (character == null)
            return null;
        return new BaseAnimationCapture(
            (byte)character->Mode,
            character->ModeParam,
            character->Timeline.BaseOverride,
            character->Timeline.TimelineSequencer.TimelineIds[0]);
    }

    /// <summary>Ktisis' mode dance around a play. Raw field writes, as the
    /// reference does them; every member is a named ClientStructs symbol.</summary>
    private void PlayWithMode(Character* character, ushort timeline)
    {
        bool pause = _timelineSheet?.GetRowOrDefault(timeline)?.Pause ?? false;
        if (pause)
        {
            character->Mode = CharacterModes.EmoteLoop;
            character->ModeParam = 0;
        }
        else if (character->Mode == CharacterModes.EmoteLoop && character->ModeParam == 0)
        {
            character->Mode = CharacterModes.Normal;
        }
        else if (character->Mode == CharacterModes.AnimLock)
        {
            // Not in Ktisis: our older builds latched AnimLock+BaseOverride
            // (Brio's base model). The latch re-drives its timeline forever,
            // so it is dismantled on the way into any new play.
            character->Mode = CharacterModes.Normal;
            character->ModeParam = 0;
            character->Timeline.BaseOverride = 0;
        }
        character->Timeline.TimelineSequencer.PlayTimeline(timeline, null);
    }

    public AnimationPortResult RestoreBase(ActorId actor, BaseAnimationCapture capture)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);

        character->Timeline.BaseOverride = capture.BaseTimeline;
        character->Mode = (CharacterModes)capture.Mode;
        character->ModeParam = capture.ModeParam;
        // Play what the base slot HELD when Poser first touched the actor,
        // so a pre-existing ordinary animation comes back instead of being
        // flattened to idle. Idle is only the fallback for an empty slot.
        character->Timeline.TimelineSequencer.PlayTimeline(
            capture.BaseSlotTimeline != 0
                ? capture.BaseSlotTimeline
                : AnimationTimelines.Idle,
            null);
        return AnimationPortResult.Ok();
    }

    public AnimationPortResult PlayEmote(ActorId actor, uint emoteId)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);
        return PlayEmoteNative(character, emoteId)
            ? AnimationPortResult.Ok()
            : AnimationPortResult.Fail("The emote entry point is unavailable.");
    }

    /// <summary>
    /// NOT IMPLEMENTED on this build, deliberately.
    ///
    /// Force loop needs the game's persistent forced-timeline field — the
    /// one Ktisis calls <c>ActionTimelineId</c> and re-writes so the engine
    /// keeps re-driving a timeline instead of falling back to idle. That
    /// field could not be proven for the current client:
    ///
    ///  · current ClientStructs maps no such member on TimelineContainer or
    ///    ActionTimelineSequencer, and exposes no accessor for it (its only
    ///    member functions are height-adjust, lips, speed, and intro/loop);
    ///  · the only other <c>ActionTimelineId</c> in ClientStructs belongs to
    ///    EventFramework's queued-callback payload and is unreachable here;
    ///  · Ktisis' literal 0x2D0 cannot be inherited. Its checkout is a patch
    ///    behind (Character.EmoteController 0x620 vs 0x630, Mode 0x2354 vs
    ///    0x2364), and the offset is inconsistent with its own struct: it
    ///    declares AnimationTimeline as Size 0x1F0 — which matches the
    ///    sequencer's real extent, since TimelineTransit follows it — yet
    ///    places the field at 0x2D0, past that end.
    ///
    /// The alternatives are all worse than an honest gap: BaseOverride is
    /// already the Base latch, so routing loop through it would collapse
    /// Blend into Base; blending Idle on disable yanks the actor off its
    /// animation instead of merely un-looping; and probing offsets with
    /// writes risks corrupting a live game process. So this fails
    /// explicitly and the UI does not offer the control.
    /// </summary>
    /// <summary>
    /// Plays an emote through the game's own entry point. Falls back to
    /// blending idle when the entry point was not found, because the
    /// alternative — leaving the pose index written with nothing driving
    /// it — shows the actor in a state the UI claims it is not in.
    /// </summary>
    private bool PlayEmoteNative(Character* character, uint emoteId)
    {
        if (_playEmote == null)
        {
            character->Timeline.TimelineSequencer.PlayTimeline(
                AnimationTimelines.Idle, null);
            return false;
        }
        _playEmote(&character->EmoteController, (nint)emoteId, nint.Zero, nint.Zero);
        return true;
    }

    // ── Loops ───────────────────────────────────────────

    /// <summary>One armed loop. The cooldown keeps the frame or two of
    /// play transition from re-firing the play every tick.</summary>
    private sealed class LoopArm
    {
        public ushort Timeline;
        public int Cooldown;
    }

    private const int LoopCooldownTicks = 15;
    private readonly Dictionary<ActorId, Dictionary<int, LoopArm>> _loops = new();

    public bool LoopsSuspended { get; set; }

    public AnimationPortResult SetSlotLoop(ActorId actor, AnimationSlot slot, ushort timeline)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);
        if (!_loops.TryGetValue(actor, out var slots))
            _loops[actor] = slots = new Dictionary<int, LoopArm>();
        slots[(int)slot] = new LoopArm { Timeline = timeline, Cooldown = LoopCooldownTicks };
        return AnimationPortResult.Ok();
    }

    public AnimationPortResult ClearSlotLoop(ActorId actor, AnimationSlot slot)
    {
        if (_loops.TryGetValue(actor, out var slots))
        {
            slots.Remove((int)slot);
            if (slots.Count == 0)
                _loops.Remove(actor);
        }
        return AnimationPortResult.Ok();
    }

    public void ClearLoops(ActorId actor) => _loops.Remove(actor);

    /// <summary>
    /// The loop tick: an armed slot that no longer plays its timeline
    /// (the one-shot ended; the game swapped its own idle in) gets the
    /// timeline played again — the same proven call as a user pick. The
    /// unproven forced-timeline field is never touched.
    /// </summary>
    private void EnforceLoops(IFramework framework)
    {
        if (LoopsSuspended || _loops.Count == 0)
            return;
        foreach (var (actor, slots) in _loops)
        {
            var character = Resolve(actor, out _);
            if (character == null)
                continue;
            foreach (var (slot, arm) in slots)
            {
                if (arm.Cooldown > 0)
                {
                    arm.Cooldown--;
                    continue;
                }
                if (character->Timeline.TimelineSequencer.TimelineIds[slot] != arm.Timeline)
                {
                    PlayWithMode(character, arm.Timeline);
                    arm.Cooldown = LoopCooldownTicks;
                }
            }
        }
    }

    public AnimationPortResult SetForceLoop(ActorId actor, ushort timeline) =>
        AnimationPortResult.Fail(
            "Force loop is unavailable: the game's forced-timeline field is not " +
            "mapped for this client version.");

    public bool SupportsForceLoop => false;

    public bool SupportsStance => _setEmoteMode != null && _cancelTimeline != null;

    // ── Speed ─────────────────────────────────────────────────────────

    public AnimationPortResult SetOverallSpeed(ActorId actor, float speed)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);
        if (!float.IsFinite(speed))
            return AnimationPortResult.Fail("Speed must be a finite number.");
        // Without the ENABLED hook the game re-wins every recalculation:
        // the value would hold for one frame and silently drift back.
        // Refuse rather than pretend; non-null alone proves nothing.
        if (!_overallSpeedHookEnabled)
            return AnimationPortResult.Fail(
                "Speed is unavailable: the game's speed hook is not active.");

        EnforcementFor(actor).OverallSpeed = speed;
        SyncEnforcementIndex();
        ApplySpeedNow(character, speed);
        return AnimationPortResult.Ok();
    }

    public AnimationPortResult ClearOverallSpeed(ActorId actor)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);

        // Exact ownership is resolved BEFORE enforcement drops, and the
        // hand-back write happens only for a speed Poser actually
        // enforced. An unconditional 1 would stomp a speed the game or
        // another tool is driving on an actor Poser never touched — and
        // ApplySpeedNow reaches every Havok control, so it would also
        // unpin playback state that was never Poser's.
        if (!_enforcement.TryGetValue(actor, out var enforcement) ||
            enforcement.OverallSpeed == null)
            return AnimationPortResult.Ok();

        enforcement.OverallSpeed = null;
        PruneEnforcement(actor);
        // Hand the actor back at normal speed. The container write alone
        // is not enough: the game re-drives the container but not every
        // Havok control, so a Poser pause (controls at 0) is released
        // here or not at all.
        ApplySpeedNow(character, 1f);
        return AnimationPortResult.Ok();
    }

    /// <summary>Writes the container speed and every Havok control's
    /// playback speed — the controls are what keep breathing and facial
    /// motion running when only the container is set.</summary>
    private static void ApplySpeedNow(Character* character, float speed)
    {
        character->Timeline.OverallSpeed = speed;
        var drawObject = character->GameObject.DrawObject;
        if (drawObject == null || drawObject->Object.GetObjectType() != ObjectType.CharacterBase)
            return;
        var charaBase = (CharacterBase*)drawObject;
        if (charaBase->Skeleton == null)
            return;
        var skeleton = charaBase->Skeleton;
        for (int p = 0; p < skeleton->PartialSkeletonCount; p++)
        {
            var partial = &skeleton->PartialSkeletons[p];
            var animated = partial->GetHavokAnimatedSkeleton(0);
            if (animated == null)
                continue;
            for (int c = 0; c < animated->AnimationControls.Length; c++)
            {
                var control = animated->AnimationControls[c].Value;
                if (control == null)
                    continue;
                control->PlaybackSpeed = speed;
            }
        }
    }

    /// <summary>
    /// Brio's settle rewind, traversal-literal (ActionTimelineCapability.
    /// StopSpeedAndResetTimeline's tick body, Brio\Brio\Capabilities\Actor\
    /// ActionTimelineCapability.cs:120-165): DrawObject (ATC:124) →
    /// ObjectType.CharacterBase gate (ATC:128) → CharacterBase.Skeleton
    /// null gate (ATC:131-133) → every PartialSkeleton (ATC:136-138) →
    /// GetHavokAnimatedSkeleton(0) (ATC:140) → every AnimationControls
    /// entry (ATC:144-148) → hkaAnimationControl.Binding null gate
    /// (ATC:150-152) → Binding.Animation null gate (ATC:154-156) → and
    /// only where PlaybackSpeed == 0 (ATC:158), hkaAnimationControl.
    /// LocalTime = 0 (ATC:160). A missing draw object or skeleton is
    /// Brio's silent early return, not an error: there is simply nothing
    /// to rewind.
    /// </summary>
    public AnimationPortResult RewindPausedControls(ActorId actor)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);

        var drawObject = character->GameObject.DrawObject;                       // ATC:124
        if (drawObject == null ||
            drawObject->Object.GetObjectType() != ObjectType.CharacterBase)      // ATC:125-129
            return AnimationPortResult.Ok();
        var charaBase = (CharacterBase*)drawObject;                              // ATC:131
        if (charaBase->Skeleton == null)                                         // ATC:132-133
            return AnimationPortResult.Ok();
        var skeleton = charaBase->Skeleton;                                      // ATC:135

        for (int p = 0; p < skeleton->PartialSkeletonCount; p++)                 // ATC:136
        {
            var partial = &skeleton->PartialSkeletons[p];                        // ATC:138
            var animated = partial->GetHavokAnimatedSkeleton(0);                 // ATC:140
            if (animated == null)
                continue;
            for (int c = 0; c < animated->AnimationControls.Length; c++)         // ATC:144
            {
                var control = animated->AnimationControls[c].Value;              // ATC:146
                if (control == null)
                    continue;
                var binding = control->hkaAnimationControl.Binding;              // ATC:150
                if (binding.ptr == null)                                         // ATC:151-152
                    continue;
                if (binding.ptr->Animation.ptr == null)                          // ATC:154-156
                    continue;
                if (control->PlaybackSpeed == 0)                                 // ATC:158
                    control->hkaAnimationControl.LocalTime = 0;                  // ATC:160
            }
        }
        return AnimationPortResult.Ok();
    }

    public AnimationPortResult SetSlotSpeed(ActorId actor, AnimationSlot slot, float speed)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);
        if (!float.IsFinite(speed))
            return AnimationPortResult.Fail("Speed must be a finite number.");
        if (!_slotSpeedHookEnabled)
            return AnimationPortResult.Fail(
                "Layer speed is unavailable: the game's slot-speed hook is not active.");

        EnforcementFor(actor).SlotSpeeds[(int)slot] = speed;
        SyncEnforcementIndex();
        character->Timeline.TimelineSequencer.SetSlotSpeed((uint)slot, speed);
        return AnimationPortResult.Ok();
    }

    public AnimationPortResult ClearSlotSpeed(ActorId actor, AnimationSlot slot)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);

        // Same ownership rule as the overall clear: only a slot Poser
        // pinned is handed back with a 1. Clearing an unowned slot is a
        // native no-op — which is also Brio's exact release behavior
        // (ResetSlotSpeedOverride only drops the dictionary entry), so
        // the expression release's second defensive unpin stays safe.
        if (!_enforcement.TryGetValue(actor, out var enforcement) ||
            !enforcement.SlotSpeeds.Remove((int)slot))
            return AnimationPortResult.Ok();

        PruneEnforcement(actor);
        character->Timeline.TimelineSequencer.SetSlotSpeed((uint)slot, 1f);
        return AnimationPortResult.Ok();
    }

    // ── Lips, stance, weapon, position ────────────────────────────────

    public AnimationPortResult SetLips(ActorId actor, ushort timeline)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);
        // Must go through the native setter: it does sequencer bookkeeping
        // that a direct field write skips.
        character->Timeline.SetLipsOverrideTimeline(timeline);
        return AnimationPortResult.Ok();
    }

    /// <summary>
    /// Ktisis' stance transition, which is a sequence rather than a pair of
    /// field writes: cancel the running timeline, set the emote mode through
    /// the game's own function, THEN write pose type and pose index, then
    /// drive the resulting idle or emote.
    ///
    /// Sit-chair additionally preserves the draw and camera offsets across
    /// the change, because the mode switch recomputes them and the actor
    /// otherwise jumps. Ktisis also clears an unmapped EmoteController flag
    /// and calls a recompute entry point before restoring; neither could be
    /// verified on this client, and restoring the saved vectors after the
    /// transition reaches the same final offsets without an unproven write.
    /// </summary>
    public AnimationPortResult SetStance(ActorId actor, AnimationStance stance, int pose)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);
        if (_setEmoteMode == null || _cancelTimeline == null)
            return AnimationPortResult.Fail(
                "Stance changes are unavailable: a required game function was not found.");

        var poseType = stance switch
        {
            AnimationStance.SitChair => EmoteController.PoseType.Sit,
            AnimationStance.SitGround => EmoteController.PoseType.GroundSit,
            AnimationStance.Sleeping => EmoteController.PoseType.Doze,
            _ => EmoteController.PoseType.Idle,
        };
        uint emoteMode = stance switch
        {
            AnimationStance.SitChair => EmoteModeSitChair,
            AnimationStance.SitGround => EmoteModeSitGround,
            AnimationStance.Sleeping => EmoteModeSleeping,
            _ => EmoteModeNormal,
        };

        // The game reports how many poses this family has. For Idle the
        // wrap must ALSO stay inside the emote table that drives those
        // poses: wrapping to a count the table cannot serve would step to
        // a pose index with no emote behind it.
        int available = EmoteController.GetAvailablePoses(poseType);
        if (available <= 0)
            available = 1;
        if (stance == AnimationStance.Idle)
            available = Math.Min(available, AnimationTimelines.IdlePoses.Count);
        int wrapped = ((pose % available) + available) % available;

        bool preserveOffsets = stance == AnimationStance.SitChair;
        var drawOffset = preserveOffsets ? character->DrawOffset : default;
        var cameraOffset = preserveOffsets ? character->CameraOffset : default;

        // A stale base latch (an older build, or Brio) re-drives its
        // timeline the moment the transition settles — the stance holds
        // for one playback and reverts. Dismantle it regardless of what
        // this session owns; session bookkeeping cannot see a latch that
        // predates it.
        if (character->Mode == CharacterModes.AnimLock)
        {
            character->Mode = CharacterModes.Normal;
            character->ModeParam = 0;
            character->Timeline.BaseOverride = 0;
        }

        _cancelTimeline(&character->Timeline, nint.Zero, nint.Zero);
        _setEmoteMode(&character->EmoteController, emoteMode);
        character->EmoteController.CurrentPoseType = poseType;
        character->EmoteController.CPoseState = (byte)wrapped;

        if (preserveOffsets)
        {
            character->DrawOffset = drawOffset;
            character->CameraOffset = cameraOffset;
        }

        // Sit and sleep stances are fully carried by the mode change above.
        // Idle is the one family the game does not drive on its own, so its
        // poses are played explicitly — as emotes past index 0, since those
        // poses only exist as emotes.
        if (stance != AnimationStance.Idle)
            return AnimationPortResult.Ok();

        bool weaponDrawn = character->Timeline.IsWeaponDrawn;
        if (wrapped == 0)
        {
            character->Timeline.TimelineSequencer.PlayTimeline(
                weaponDrawn ? AnimationTimelines.BattleIdle : AnimationTimelines.Idle, null);
        }
        else if (weaponDrawn)
        {
            PlayEmoteNative(character, AnimationTimelines.BattlePose);
        }
        else if (wrapped < AnimationTimelines.IdlePoses.Count &&
            AnimationTimelines.IdlePoses[wrapped] is var emote and not 0)
        {
            PlayEmoteNative(character, emote);
        }
        return AnimationPortResult.Ok();
    }

    /// <summary>
    /// Plays the draw or sheathe timeline and then sets the weapon-state
    /// flag. Both halves are required: the game does not update its own
    /// flag for a timeline Poser forced, so without the second write the
    /// actor animates but every later read still reports the old state.
    /// Ktisis XORs a raw CombatFlags byte; ClientStructs exposes the same
    /// state as a settable member, which needs no offset.
    /// </summary>
    public AnimationPortResult SetWeaponDrawn(ActorId actor, bool drawn)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);
        if (character->Timeline.IsWeaponDrawn == drawn)
            return AnimationPortResult.Ok();
        character->Timeline.TimelineSequencer.PlayTimeline(
            drawn ? AnimationTimelines.DrawWeapon : AnimationTimelines.SheatheWeapon, null);
        character->Timeline.IsWeaponDrawn = drawn;
        return AnimationPortResult.Ok();
    }

    /// <summary>
    /// Position lock reuses the ONE position authority (the model
    /// transform override that already suppresses the game's per-frame
    /// write) rather than adding a second hook. Releasing only clears an
    /// override this port created, so a placement the user made with the
    /// gizmo survives unlocking.
    /// </summary>
    public AnimationPortResult SetPositionLock(ActorId actor, bool locked)
    {
        if (ResolveActor(actor) is not { } legacy)
            return AnimationPortResult.Fail($"Actor {actor} is no longer available.");

        if (locked)
        {
            if (_posing.HasTransformOverride(legacy))
            {
                // Already held in place by the user's own placement.
                return AnimationPortResult.Ok();
            }
            _posing.SetTransformOverride(legacy, _posing.GetEffectiveTransform(legacy));
            _positionLocks.Add(actor);
            return AnimationPortResult.Ok();
        }

        if (_positionLocks.Remove(actor))
            _posing.ClearTransformOverride(legacy);
        return AnimationPortResult.Ok();
    }

    // ── Scrubbing ─────────────────────────────────────────────────────

    public AnimationPortResult SetControlTime(
        ActorId actor, ScrubControlId control, float time, ulong token)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);
        if (!float.IsFinite(time))
            return AnimationPortResult.Fail("Scrub time must be a finite number.");

        var drawObject = character->GameObject.DrawObject;
        if (drawObject == null || drawObject->Object.GetObjectType() != ObjectType.CharacterBase)
            return AnimationPortResult.Fail("Actor has no character skeleton.");
        var charaBase = (CharacterBase*)drawObject;
        if (charaBase->Skeleton == null)
            return AnimationPortResult.Fail("Actor has no character skeleton.");
        var skeleton = charaBase->Skeleton;

        if (control.Partial < 0 || control.Partial >= skeleton->PartialSkeletonCount)
            return AnimationPortResult.Fail("Scrub target no longer exists.");
        var partial = &skeleton->PartialSkeletons[control.Partial];
        var animated = partial->GetHavokAnimatedSkeleton(0);
        if (animated == null ||
            control.Control < 0 || control.Control >= animated->AnimationControls.Length)
            return AnimationPortResult.Fail("Scrub target no longer exists.");
        var target = animated->AnimationControls[control.Control].Value;
        if (target == null)
            return AnimationPortResult.Fail("Scrub target no longer exists.");
        var binding = target->hkaAnimationControl.Binding;
        if (binding.ptr == null || binding.ptr->Animation.ptr == null)
            return AnimationPortResult.Fail("Scrub target no longer exists.");

        // Re-derive the token from the live skeleton: a replacement moves
        // the skeleton or changes the control count, and the write is
        // refused rather than landing on whatever now occupies the slot.
        if (token != 0 && token != CurrentToken(skeleton))
            return AnimationPortResult.Fail("Skeleton changed; scrub cancelled.");

        float duration = binding.ptr->Animation.ptr->Duration;
        target->hkaAnimationControl.LocalTime = Math.Clamp(time, 0f, duration);
        return AnimationPortResult.Ok();
    }

    private static ulong CurrentToken(
        FFXIVClientStructs.FFXIV.Client.Graphics.Render.Skeleton* skeleton)
    {
        int count = 0;
        for (int p = 0; p < skeleton->PartialSkeletonCount; p++)
        {
            var partial = &skeleton->PartialSkeletons[p];
            var animated = partial->GetHavokAnimatedSkeleton(0);
            if (animated == null)
                continue;
            for (int c = 0; c < animated->AnimationControls.Length; c++)
            {
                var control = animated->AnimationControls[c].Value;
                if (control == null)
                    continue;
                var binding = control->hkaAnimationControl.Binding;
                if (binding.ptr == null || binding.ptr->Animation.ptr == null)
                    continue;
                count++;
            }
        }
        return unchecked(((ulong)(nint)skeleton * 397) ^ (ulong)count);
    }

    // ── Physics ───────────────────────────────────────────────────────

    public bool IsPhysicsFrozen => _physics.IsFrozen;

    public AnimationPortResult SetPhysicsFrozen(bool frozen) => _physics.SetFrozen(frozen);

    public void Dispose()
    {
        _framework.Update -= EnforceLoops;
        _loops.Clear();
        _speedHook?.Dispose();
        _slotSpeedHook?.Dispose();
        _enforcement.Clear();
        _byAddress.Clear();
        // The session restores per-actor overrides before disposal; the
        // global code patch is the patcher's own, and its dispose restores
        // it (or reports the failure explicitly).
        _physics.Dispose();
        GC.SuppressFinalize(this);
    }
}
