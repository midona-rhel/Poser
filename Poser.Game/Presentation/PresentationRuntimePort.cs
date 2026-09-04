using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Dalamud.Plugin;
using Poser.Application.Integration;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Poser.Application.Presentation;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;
using Poser.Game.Bindings;
using CSCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace Poser.Game.Presentation;

/// <summary>
/// Native side of runtime presentation. Reference provenance, verified
/// against current ClientStructs by reflection:
///
///  · Opacity — Brio: <c>Character.Alpha</c> (CS-named, 0x22E8), written
///    on change; the game may drive its own fades, exactly as in Brio.
///  · Tint — Brio: <c>CharacterBase.Tint</c> (CS-named, 0x290; identical
///    on Human and Weapon). Brio keeps writes alive by suppressing the
///    game's tint update virtual; this port does the same but gates the
///    detour PER MODEL INSTANCE on the owned set instead of globally.
///    The vfunc (index 24, vtable byte 0xC0 off the CS-named
///    <c>StaticVirtualTablePointer</c>) has no ClientStructs symbol; its
///    position is verified against the named neighbors
///    SetTransparency (26) and GetTransparency (27).
///  · Wetness — Ktisis: <c>WeatherWetness/SwimmingWetness/WetnessDepth</c>
///    (CS-named, 0x2E0/0x2E4/0x2E8 on the character's draw object),
///    re-applied unconditionally on the framework tick while owned,
///    exactly Ktisis' enforcement.
///
/// The tick also re-applies owned tints, which is what survives a
/// draw-object replacement: pointers are re-resolved fresh every tick
/// through the type-checked slot resolution, so a replaced model
/// instance is rebound and rewritten within a frame — and never read
/// back as a new capture. A missing weapon model resolves to null and
/// fails; it is never redirected to another model.
/// </summary>
public sealed unsafe partial class PresentationRuntimePort : IPresentationRuntimePort, IDisposable
{
    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private readonly StableBindingRegistry _bindings;

    private sealed class Owned
    {
        public readonly Dictionary<PresentationModel, Vector4> Tints = new();
        public WetnessState? Wetness;
        public readonly Dictionary<AppearanceColorChannel, Vector4> Colors = new();
        public bool ColorsSuspended;
        public ulong ColorRevision;
        public bool IsEmpty => Tints.Count == 0 && Wetness == null && Colors.Count == 0;
    }

    // Authoritative, stable-id keyed.
    private readonly Dictionary<ActorId, Owned> _owned = new();
    // Derived per-frame index for the tint detour only: the exact model
    // instances whose tint Poser owns right now. Never a source of truth.
    // SWAP-PUBLISHED, never mutated in place: the detour reads it from
    // whichever thread the game calls the tint vfunc on, and a HashSet being
    // cleared and refilled underneath that read tears. Writers build a fresh
    // set and assign; readers take one reference and use it for the call.
    private volatile HashSet<nint> _ownedTintBases = new();

    private delegate nint UpdateTintDelegate(nint characterBase, nint tint);
    private readonly Hook<UpdateTintDelegate>? _updateTintHook;
    // Non-null is NOT enabled: construction can succeed and Enable still
    // throw. Tint commands gate on this, set only after Enable returned;
    // a partially constructed hook is still disposed.
    private readonly bool _tintHookEnabled;

    public PresentationRuntimePort(
        IFramework framework,
        IGameInteropProvider hooking,
        IPluginLog log,
        StableBindingRegistry bindings,
        IIntegrationRuntimePort integration,
        IDalamudPluginInterface pluginInterface)
    {
        _framework = framework;
        _log = log;
        _bindings = bindings;
        _integration = integration;
        _colorRelease = new ColorReleaseCoordinator(ColorIntentFor, ResolveColorTarget, InspectColor,
            actor =>
            {
                var result = _integration.RequestRedraw(actor);
                return result.Success ? PresentationPortResult.Ok() : PresentationPortResult.Fail(result.Detail!);
            }, ReleaseColor, EnforceInspectedColors);
        _redrawn = pluginInterface.GetIpcSubscriber<nint, int, object?>("Penumbra.GameObjectRedrawn");
        _redrawn.Subscribe(OnColorRedrawn);

        try
        {
            // CharacterBase vfunc 24 (vtable byte 0xC0): the game's own
            // tint update, hooked so owned instances keep Poser's value.
            // No CS symbol exists for this slot; the address is read from
            // the CS-named static vtable at runtime, never a raw scan.
            var address = Marshal.ReadInt64((nint)CharacterBase.StaticVirtualTablePointer + 0xC0);
            _updateTintHook = hooking.HookFromAddress<UpdateTintDelegate>((nint)address, UpdateTintDetour);
            _updateTintHook.Enable();
            _tintHookEnabled = true;
        }
        catch (Exception ex)
        {
            _log.Error($"Tint hook unavailable; tint overrides will fail explicitly: {ex.Message}");
        }

        _framework.Update += EnforceOwned;
    }

    private nint UpdateTintDetour(nint characterBase, nint tint)
    {
        // Only the exact instances Poser owns are suppressed; everything
        // else keeps the game's own tinting.
        // One read of the field, then work off that snapshot: a concurrent
        // rebuild replaces the reference, it never edits this instance.
        if (_ownedTintBases.Contains(characterBase))
            return 0;
        return _updateTintHook!.Original(characterBase, tint);
    }

    // ── Resolution (the AnimationRuntimePort pattern, verbatim) ───────

    private CSCharacter* Resolve(ActorId actor, out string? detail)
    {
        detail = null;
        if (!_framework.IsInFrameworkUpdateThread)
        {
            detail = "Presentation writes must run on the framework thread.";
            return null;
        }
        var resolved = _bindings.Resolve(actor);
        if (!resolved.Success || resolved.Value is not { } legacy || legacy.Address == nint.Zero)
        {
            detail = resolved.Detail ?? $"Actor {actor} is no longer available.";
            return null;
        }
        return (CSCharacter*)legacy.Address;
    }

    private static PoseSlot SlotFor(PresentationModel model) => model switch
    {
        PresentationModel.Character => PoseSlot.Character,
        PresentationModel.MainHand => PoseSlot.MainHand,
        _ => PoseSlot.OffHand,
    };

    /// <summary>The exact model instance for one tint target, type
    /// checked; null when absent. Never another model.</summary>
    private static CharacterBase* BaseFor(CSCharacter* character, PresentationModel model) =>
        SlotCharacterBases.Resolve((nint)character, SlotFor(model));

    public bool IsSupported(ActorId actor)
    {
        var resolved = _bindings.Resolve(actor);
        return resolved.Success && resolved.Value is { } actorValue && actorValue.Address != nint.Zero;
    }

    public PresentationReading? Read(ActorId actor)
    {
        var character = Resolve(actor, out _);
        if (character == null)
            return null;

        Vector4? TintOf(PresentationModel model)
        {
            var characterBase = BaseFor(character, model);
            return characterBase == null ? null : characterBase->Tint;
        }

        var mainBase = BaseFor(character, PresentationModel.Character);
        var wetness = mainBase == null
            ? default
            : new WetnessState(
                mainBase->WeatherWetness,
                mainBase->SwimmingWetness,
                mainBase->WetnessDepth);

        return new PresentationReading(
            character->Alpha,
            TintOf(PresentationModel.Character),
            TintOf(PresentationModel.MainHand),
            TintOf(PresentationModel.OffHand),
            wetness);
    }

    // ── Opacity ───────────────────────────────────────────────────────

    public PresentationPortResult SetOpacity(ActorId actor, float opacity)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return PresentationPortResult.Fail(detail!);
        if (!float.IsFinite(opacity))
            return PresentationPortResult.Fail("Opacity must be a finite number.");
        character->Alpha = Math.Clamp(opacity, 0f, 1f);
        return PresentationPortResult.Ok();
    }

    public PresentationPortResult RestoreOpacity(ActorId actor, float incoming)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return PresentationPortResult.Fail(detail!);
        character->Alpha = incoming;
        return PresentationPortResult.Ok();
    }

    // ── Tint ──────────────────────────────────────────────────────────

    public PresentationPortResult SetTint(ActorId actor, PresentationModel model, Vector4 tint)
    {
        if (!_tintHookEnabled)
            return PresentationPortResult.Fail(
                "Tint is unavailable: the game's tint update is not hooked.");
        var character = Resolve(actor, out var detail);
        if (character == null)
            return PresentationPortResult.Fail(detail!);
        var characterBase = BaseFor(character, model);
        if (characterBase == null)
            return PresentationPortResult.Fail(
                model == PresentationModel.Character
                    ? "The character model is not available."
                    : "That weapon model is not present.");

        characterBase->Tint = tint;
        OwnedFor(actor).Tints[model] = tint;
        RebuildTintIndex();
        return PresentationPortResult.Ok();
    }

    public PresentationPortResult RestoreTint(ActorId actor, PresentationModel model, Vector4 incoming)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return PresentationPortResult.Fail(detail!);

        var characterBase = BaseFor(character, model);
        if (characterBase != null)
            characterBase->Tint = incoming;
        // An absent model has nothing left to restore into: the game
        // rebuilt it with its own defaults, so releasing ownership IS the
        // restoration.
        Release(actor, owned => owned.Tints.Remove(model));
        return PresentationPortResult.Ok();
    }

    // ── Wetness ───────────────────────────────────────────────────────

    public PresentationPortResult SetWetness(ActorId actor, WetnessState state)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return PresentationPortResult.Fail(detail!);
        var characterBase = BaseFor(character, PresentationModel.Character);
        if (characterBase == null)
            return PresentationPortResult.Fail("The character model is not available.");

        WriteWetness(characterBase, state);
        OwnedFor(actor).Wetness = state;
        return PresentationPortResult.Ok();
    }

    public PresentationPortResult ClearWetness(ActorId actor, WetnessState incoming)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return PresentationPortResult.Fail(detail!);
        var characterBase = BaseFor(character, PresentationModel.Character);
        if (characterBase == null)
            return PresentationPortResult.Fail("The character model is not available.");

        WriteWetness(characterBase, incoming);
        Release(actor, owned => owned.Wetness = null);
        return PresentationPortResult.Ok();
    }

    private static void WriteWetness(CharacterBase* characterBase, WetnessState state)
    {
        characterBase->WeatherWetness = state.Weather;
        characterBase->SwimmingWetness = state.Swimming;
        characterBase->WetnessDepth = state.Depth;
    }

    // ── Ownership plumbing ────────────────────────────────────────────

    public void ClearOwned(ActorId actor)
    {
        SuspendColors(actor);
        if (_owned.Remove(actor))
            RebuildTintIndex();
    }

    private Owned OwnedFor(ActorId actor)
    {
        if (!_owned.TryGetValue(actor, out var owned))
            _owned[actor] = owned = new Owned();
        return owned;
    }

    private void Release(ActorId actor, Action<Owned> change)
    {
        if (!_owned.TryGetValue(actor, out var owned))
            return;
        change(owned);
        if (owned.IsEmpty)
            _owned.Remove(actor);
        RebuildTintIndex();
    }

    /// <summary>
    /// The per-tick enforcement: wetness is rewritten because the game
    /// recomputes it every frame (Ktisis' model), and owned tints are
    /// rewritten so a REPLACED draw object gets the owned value on its
    /// exact new instance within a frame — the replacement's defaults
    /// are written over, never read back. The suppression index is
    /// rebuilt from the same fresh resolution.
    /// </summary>
    private void EnforceOwned(IFramework framework)
    {
        _colorRelease.AdvanceFrame();
        if (_owned.Count == 0)
            return;

        var rebuilt = new HashSet<nint>();
        foreach (var (actor, owned) in _owned.ToArray())
        {
            _colorRelease.Tick(actor);
            if (_colorsDisposed) return;
            if (!_owned.TryGetValue(actor, out var currentOwned) || !ReferenceEquals(currentOwned, owned)) continue;
            var resolved = _bindings.Resolve(actor);
            if (!resolved.Success || resolved.Value is not { } legacy || legacy.Address == nint.Zero)
                continue;
            var character = (CSCharacter*)legacy.Address;


            foreach (var (model, tint) in owned.Tints)
            {
                var characterBase = BaseFor(character, model);
                if (characterBase == null)
                    continue;
                characterBase->Tint = tint;
                rebuilt.Add((nint)characterBase);
            }

            if (owned.Wetness is { } wetness)
            {
                var mainBase = BaseFor(character, PresentationModel.Character);
                if (mainBase != null)
                    WriteWetness(mainBase, wetness);
            }
        }

        // Publish last: the detour never observes a half-built index.
        _ownedTintBases = rebuilt;
    }

    private void RebuildTintIndex()
    {
        var rebuilt = new HashSet<nint>();
        foreach (var (actor, owned) in _owned)
        {
            if (owned.Tints.Count == 0)
                continue;
            var resolved = _bindings.Resolve(actor);
            if (!resolved.Success || resolved.Value is not { } legacy || legacy.Address == nint.Zero)
                continue;
            var character = (CSCharacter*)legacy.Address;
            foreach (var model in owned.Tints.Keys)
            {
                var characterBase = BaseFor(character, model);
                if (characterBase != null)
                    rebuilt.Add((nint)characterBase);
            }
        }
        _ownedTintBases = rebuilt;
    }

    public void Dispose()
    {
        _colorsDisposed = true;
        _colorRelease.Dispose();
        _redrawn.Unsubscribe(OnColorRedrawn);
        _framework.Update -= EnforceOwned;
        _owned.Clear();
        _ownedTintBases = new HashSet<nint>();
        _updateTintHook?.Dispose();
        GC.SuppressFinalize(this);
    }
}
