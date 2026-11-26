using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Poser.Application.Integration;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;
using Poser.Game.Bindings;
using CSCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using Poser.Game.Natives;

namespace Poser.Game.Presentation;

/// <summary>
/// Reads and writes actor opacity, tint, wetness and shader colours in game
/// memory. Opacity is written once. Tint, wetness and colours that Poser set are
/// written again every frame, because the game recomputes them. Tint also hooks
/// the game's own tint update, as Brio does, and skips it only for the models
/// whose tint Poser set. Models are looked up again every frame, so a new body
/// after a redraw gets Poser's values too.
/// </summary>
public sealed unsafe partial class PresentationRuntimePort : IDisposable
{
    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private readonly StableBindingRegistry _bindings;
    private readonly IObjectTable _objects;

    private sealed class Owned
    {
        public readonly Dictionary<PresentationModel, Vector4> Tints = new();
        public WetnessState? Wetness;
        public readonly Dictionary<AppearanceColorChannel, Vector4> Colors = new();
        public bool ColorsSuspended;
        public bool IsEmpty => Tints.Count == 0 && Wetness == null && Colors.Count == 0;
    }

    // The values Poser set, by actor.
    private readonly Dictionary<ActorId, Owned> _owned = new();
    // The model addresses whose tint Poser set right now, rebuilt from _owned
    // for the tint hook. The hook runs on whatever thread the game calls it
    // from, so the set is never changed in place: a new set is built and
    // assigned, and the hook reads one reference per call.
    private volatile HashSet<nint> _ownedTintBases = new();

    private delegate nint UpdateTintDelegate(nint characterBase, nint tint);
    private readonly Hook<UpdateTintDelegate>? _updateTintHook;
    // Creating the hook can work while enabling it throws, so tint commands
    // check this flag, which is set only after Enable returned. A hook that
    // was created but not enabled is still disposed.
    private readonly bool _tintHookEnabled;
    private bool _tintFaultLogged;

    public PresentationRuntimePort(
        IFramework framework,
        IGameInteropProvider hooking,
        IPluginLog log,
        StableBindingRegistry bindings,
        IGlamourerPort integration,
        IObjectTable objects)
    {
        _framework = framework;
        _log = log;
        _bindings = bindings;
        _integration = integration;
        _objects = objects;

        try
        {
            // CharacterBase virtual function 24 (vtable offset 0xC0) is the
            // game's own tint update. FFXIVClientStructs has no name for it,
            // so the address is read from CharacterBase's vtable, which it does name.
            var address = Marshal.ReadInt64((nint)CharacterBase.StaticVirtualTablePointer + NativeFields.CharacterBase_VfSetTint);
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
        // Skip the game's tint update only for models whose tint Poser set.
        // The set is read once; a rebuild on another thread assigns a new set.
        try
        {
            if (_ownedTintBases.Contains(characterBase))
                return 0;
        }
        catch (Exception ex)
        {
            // If Poser's lookup throws, continue to the original tint function.
            if (!_tintFaultLogged)
            {
                _tintFaultLogged = true;
                _log.Error($"Tint detour faulted (logged once): {ex}");
            }
        }
        return _updateTintHook!.Original(characterBase, tint);
    }

    // ── Actor resolution ───────

    private CSCharacter* Resolve(ActorId actor, out string? detail)
    {
        detail = null;
        var resolved = _bindings.Resolve(actor);
        if (!resolved.Success || resolved.Value is not { } legacy)
        {
            detail = resolved.Detail ?? $"Actor {actor} is no longer available.";
            return null;
        }
        // Poser sees GPose end after the game may already have deleted its
        // GPose actors, while their bindings still exist. Borrowed world actors
        // are still there. Check the object table so nothing is written into a
        // deleted actor.
        var current = _objects.CreateObjectReference(legacy.Address);
        if (current is null || !current.IsValid())
        {
            detail = "The actor is no longer in the object table.";
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

    /// <summary>The body, main-hand or off-hand model; null when that model is
    /// absent. Never falls back to another model.</summary>
    private static CharacterBase* BaseFor(CSCharacter* character, PresentationModel model) =>
        SlotCharacterBases.Resolve((nint)character, SlotFor(model));

    public bool IsSupported(ActorId actor) => Resolve(actor, out _) != null;

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

    public Outcome SetOpacity(ActorId actor, float opacity)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);
        if (!float.IsFinite(opacity))
            return Outcome.Fail("Opacity must be a finite number.");
        character->Alpha = Math.Clamp(opacity, 0f, 1f);
        return Outcome.Ok();
    }

    public Outcome RestoreOpacity(ActorId actor, float incoming)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);
        character->Alpha = incoming;
        return Outcome.Ok();
    }

    // ── Tint ──────────────────────────────────────────────────────────

    public Outcome SetTint(ActorId actor, PresentationModel model, Vector4 tint)
    {
        if (!_tintHookEnabled)
            return Outcome.Fail(
                "Tint is unavailable: the game's tint update is not hooked.");
        var character = Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);
        var characterBase = BaseFor(character, model);
        if (characterBase == null)
            return Outcome.Fail(
                model == PresentationModel.Character
                    ? "The character model is not available."
                    : "That weapon model is not present.");

        characterBase->Tint = tint;
        OwnedFor(actor).Tints[model] = tint;
        RebuildTintIndex();
        return Outcome.Ok();
    }

    public Outcome RestoreTint(ActorId actor, PresentationModel model, Vector4 incoming)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);

        var characterBase = BaseFor(character, model);
        if (characterBase != null)
            characterBase->Tint = incoming;
        // An absent model has nothing to write back to; forget the tint, so a
        // later model keeps its own.
        Release(actor, owned => owned.Tints.Remove(model));
        return Outcome.Ok();
    }

    // ── Wetness ───────────────────────────────────────────────────────

    public Outcome SetWetness(ActorId actor, WetnessState state)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);
        var characterBase = BaseFor(character, PresentationModel.Character);
        if (characterBase == null)
            return Outcome.Fail("The character model is not available.");

        WriteWetness(characterBase, state);
        OwnedFor(actor).Wetness = state;
        return Outcome.Ok();
    }

    public Outcome ClearWetness(ActorId actor, WetnessState incoming)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);
        var characterBase = BaseFor(character, PresentationModel.Character);
        if (characterBase == null)
            return Outcome.Fail("The character model is not available.");

        WriteWetness(characterBase, incoming);
        Release(actor, owned => owned.Wetness = null);
        return Outcome.Ok();
    }

    private static void WriteWetness(CharacterBase* characterBase, WetnessState state)
    {
        characterBase->WeatherWetness = state.Weather;
        characterBase->SwimmingWetness = state.Swimming;
        characterBase->WetnessDepth = state.Depth;
    }

    // ── Remembering what Poser set ────────────────────────────────────

    /// <summary>Forgets every value Poser set on the actor, without writing anything.</summary>
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
    /// Every frame: writes Poser's values again to the current models, then
    /// replaces the tint hook's set. Missing actors or models are skipped until they are back.
    /// </summary>
    private void EnforceOwned(IFramework framework)
    {
        if (_owned.Count == 0)
            return;

        var rebuilt = new HashSet<nint>();
        foreach (var (actor, owned) in _owned.ToArray())
        {
            EnforceColors(actor, owned);
            if (_colorsDisposed) return;
            if (!_owned.TryGetValue(actor, out var currentOwned) || !ReferenceEquals(currentOwned, owned)) continue;
            var character = Resolve(actor, out _);
            if (character == null)
                continue;


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

        // Assign the new set last, so the tint hook never reads one half built.
        _ownedTintBases = rebuilt;
    }

    private void RebuildTintIndex()
    {
        var rebuilt = new HashSet<nint>();
        foreach (var (actor, owned) in _owned)
        {
            if (owned.Tints.Count == 0)
                continue;
            var character = Resolve(actor, out _);
            if (character == null)
                continue;
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
        _framework.Update -= EnforceOwned;
        _owned.Clear();
        _ownedTintBases = new HashSet<nint>();
        _updateTintHook?.Dispose();
        GC.SuppressFinalize(this);
    }
}
