using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Poser.Domain.Identity;
using Poser.Game.Bindings;
using Poser.Game.Core;
using Poser.Application.Appearance;

namespace Poser.Game.Integration;

/// <summary>
/// Temporarily remaps skin materials in the actor's effective Penumbra collection,
/// redraws the actor, then removes its hair, face, and tail model slots.
/// Cleanup requests removal of the temporary mod; the next redraw restores the body.
/// </summary>
public sealed class InvisibleSkinService : IClothingOnlyRuntimePort
{
    private readonly PenumbraIpc _port;
    private readonly IFramework _framework;
    private readonly Lazy<StableBindingRegistry> _bindings;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IPluginLog _log;

    /// <summary>The path table, loaded once: every key from the bundled
    /// skin-paths list mapped to the bundled invisible material. Null when
    /// the assets are missing, which refuses cleanly.</summary>
    private Dictionary<string, string>? _paths;
    private bool _pathsLoaded;
    // Bound to the rendered body, not just an actor/slot: an ordinary redraw
    // restores skin and must not be saved as still clothing-only.
    private readonly Dictionary<ActorId, nint> _appliedBodies = new();

    private unsafe nint Body(ActorId actor) =>
        _bindings.Value.Resolve(actor) is { Success: true, Value: { } live }
            ? (nint)SlotCharacterBases.Resolve(live.Address, PoseSlot.Character) : 0;

    private unsafe bool StillApplied(ActorId actor, nint body)
    {
        if (body == 0 || Body(actor) != body) return false;
        var character = (CharacterBase*)body;
        // The native allocator may reuse the address after redraw. Restored
        // head models invalidate the marker even when the address is equal.
        for (int slot = HairModelSlot; slot <= TailModelSlot && slot < character->SlotCount; slot++)
            if (character->Models[slot] != null) return false;
        return true;
    }

    public void Reconcile(Func<ActorId, bool> isCurrent)
    {
        foreach (var actor in new List<ActorId>(_appliedBodies.Keys))
            if (!isCurrent(actor))
                _appliedBodies.Remove(actor);
    }

    public bool IsApplied(ActorId actor)
    {
        if (!_appliedBodies.TryGetValue(actor, out var body)) return false;
        if (StillApplied(actor, body)) return true;
        _appliedBodies.Remove(actor);
        return false;
    }

    public InvisibleSkinService(
        PenumbraIpc port,
        IFramework framework,
        Lazy<StableBindingRegistry> bindings,
        IDalamudPluginInterface pluginInterface,
        IPluginLog log)
    {
        _port = port;
        _framework = framework;
        _bindings = bindings;
        _pluginInterface = pluginInterface;
        _log = log;
    }

    /// <summary>Whether the actor's drawn body is a Human — the only model
    /// type the material remap and the model-slot layout apply to. Main
    /// thread only (reads the draw object).</summary>
    public unsafe bool IsHuman(nint actorAddress)
    {
        if (actorAddress == nint.Zero)
            return false;
        return GPoseObjectTable.AsHuman(
            SlotCharacterBases.Resolve(actorAddress, PoseSlot.Character)) != null;
    }

    /// <inheritdoc cref="IsHuman(nint)"/>
    public bool IsHuman(ActorId actor)
        => _bindings.Value.Resolve(actor) is { Success: true, Value: { } legacy }
            && IsHuman(legacy.Address);

    public async Task<string?> Apply(ActorId actor, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!await _framework.RunOnFrameworkThread(() => IsHuman(actor)).ConfigureAwait(false))
            return "Only a loaded human actor can hide its body.";
        var paths = LoadPaths();
        if (paths is null)
            return "The invisible-skin files are missing from the plugin "
                + "folder.";

        var collection = await _framework.RunOnFrameworkThread(
            () => _port.GetEffectiveCollection(actor)).ConfigureAwait(false);
        if (!collection.Success)
            return collection.Detail;

        var added = await _framework.RunOnFrameworkThread(() =>
            {
                cancellation.ThrowIfCancellationRequested();
                return _port.AddInvisibleSkinMods(collection.Value, paths);
            })
            .ConfigureAwait(false);
        if (!added.Success)
            return added.Detail;

        // Redraw loads the remapped materials. Remove the collection override even
        // when the actor disappears or the redraw is cancelled.
        try
        {
            var redraw = await _port.RedrawAndWait(
                actor, TimeSpan.FromSeconds(20), cancellation).ConfigureAwait(false);
            if (!redraw.Success) return redraw.Detail;
            return await _framework.RunOnFrameworkThread(() =>
            {
                cancellation.ThrowIfCancellationRequested();
                if (!StripHeadModels(actor)) return "The clothing-only actor is no longer available.";
                _appliedBodies[actor] = Body(actor);
                return (string?)null;
            }).ConfigureAwait(false);
        }
        finally
        {
            var cleanup = await _framework.RunOnFrameworkThread(
                () => _port.RemoveInvisibleSkinMods(collection.Value)).ConfigureAwait(false);
            if (!cleanup.Success)
                throw new InvalidOperationException($"Clothing-only material cleanup failed: {cleanup.Detail}");
        }
    }

    /// <summary>Nulls the hair, face and tail model slots on the redrawn
    /// body — the material remap covers the body skin; these three carry
    /// the hair, the face (eyes included) and the tail. Ktisis'
    /// SetInvisibleSkin's slot selection. Framework thread only, and only on a
    /// actor in the GPose object-table range.</summary>
    private unsafe bool StripHeadModels(ActorId actor)
    {
        var resolved = _bindings.Value.Resolve(actor);
        if (!resolved.Success || resolved.Value is not { } legacy
            || legacy.Address == nint.Zero)
            return false;
        if (!GPoseObjectTable.IsActorIndex(GPoseObjectTable.IndexOf(legacy.Address)))
            return false;
        var characterBase = SlotCharacterBases.Resolve(
            legacy.Address, PoseSlot.Character);
        if (GPoseObjectTable.AsHuman(characterBase) == null)
            return false;
        for (int slot = HairModelSlot; slot <= TailModelSlot; slot++)
        {
            Appearance.CharacterModelNative.ReleaseSlot(characterBase, slot);
        }
        return true;
    }

    /// <summary>Human model slots (Ktisis SetInvisibleSkin): 10 hair,
    /// 11 face, 12 tail.</summary>
    private const int HairModelSlot = 10;
    private const int TailModelSlot = 12;

    private Dictionary<string, string>? LoadPaths()
    {
        if (_pathsLoaded)
            return _paths;
        _pathsLoaded = true;
        try
        {
            var root = Path.Combine(
                _pluginInterface.AssemblyLocation.DirectoryName!,
                "Data", "Integration");
            var listPath = Path.Combine(root, "skin-paths.json");
            var materialPath = Path.Combine(root, "mt_c0101b0001_a.mtrl");
            if (!File.Exists(listPath) || !File.Exists(materialPath))
                return null;
            var parsed = System.Text.Json.JsonSerializer
                .Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(listPath));
            if (parsed is null || parsed.Count == 0)
                return null;
            var paths = new Dictionary<string, string>(parsed.Count);
            foreach (var key in parsed.Keys)
                paths[key] = materialPath;
            _paths = paths;
        }
        catch (Exception ex)
        {
            _log.Error($"[InvisibleSkin] asset load failed: {ex}");
            _paths = null;
        }
        return _paths;
    }
}
