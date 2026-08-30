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

using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace Poser.Game.Integration;

/// <summary>
/// Hides an actor's skin, hair and eyes so only the clothing remains —
/// Ktisis' invisible-skin mechanism, verified against its source
/// (PenumbraIpcProvider.AssignInvisibleSkin + McdfManager.SetInvisibleSkin):
/// a temporary Penumbra mod on the actor's EFFECTIVE collection remaps
/// every known skin material to one bundled invisible material, a redraw
/// bakes it into the drawn body, the hair/face/tail model slots are then
/// nulled natively, and the temporary mod is removed again — the
/// collection is only dirty for the redraw's duration, so nothing else
/// sharing it keeps the change. The drawn body holds the state until its
/// next redraw, which is therefore the restore.
/// </summary>
public sealed class InvisibleSkinService
{
    private readonly IntegrationRuntimePort _port;
    private readonly IFramework _framework;
    private readonly Lazy<StableBindingRegistry> _bindings;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IPluginLog _log;

    /// <summary>The path table, loaded once: every key from the bundled
    /// skin-paths list mapped to the bundled invisible material. Null when
    /// the assets are missing, which refuses cleanly.</summary>
    private Dictionary<string, string>? _paths;
    private bool _pathsLoaded;

    public InvisibleSkinService(
        IntegrationRuntimePort port,
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
        var characterBase = SlotCharacterBases.Resolve(
            actorAddress, PoseSlot.Character);
        return characterBase != null
            && characterBase->GetModelType() == CharacterBase.ModelType.Human;
    }

    /// <inheritdoc cref="IsHuman(nint)"/>
    public bool IsHuman(ActorId actor)
        => _bindings.Value.Resolve(actor) is { Success: true, Value: { } legacy }
            && IsHuman(legacy.Address);

    /// <summary>Runs the whole flow and reports the outcome to
    /// <paramref name="onFailure"/> (framework thread) when it fails.
    /// Redraw restores the actor; this verb has no other undo.</summary>
    public void Request(ActorId actor, Action<string>? onFailure)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var failure = await Apply(actor).ConfigureAwait(false);
                if (failure != null && onFailure != null)
                    await _framework.RunOnFrameworkThread(
                        () => onFailure(failure)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error($"[InvisibleSkin] failed: {ex}");
            }
        });
    }

    private async Task<string?> Apply(ActorId actor)
    {
        var paths = LoadPaths();
        if (paths is null)
            return "The invisible-skin files are missing from the plugin "
                + "folder.";

        var collection = await _framework.RunOnFrameworkThread(
            () => _port.GetEffectiveCollection(actor)).ConfigureAwait(false);
        if (!collection.Success)
            return collection.Detail;

        var added = await _framework.RunOnFrameworkThread(
            () => _port.AddInvisibleSkinMods(collection.Value, paths))
            .ConfigureAwait(false);
        if (!added.Success)
            return added.Detail;

        // The redraw is what bakes the remapped materials into the drawn
        // body; the temporary mod is removed again afterwards WHATEVER
        // happened, so the collection never stays dirty.
        string? failure = null;
        var redraw = await _port.RedrawAndWait(
            actor, TimeSpan.FromSeconds(20), CancellationToken.None)
            .ConfigureAwait(false);
        if (redraw.Success)
            await _framework.RunOnFrameworkThread(
                () => StripHeadModels(actor)).ConfigureAwait(false);
        else
            failure = redraw.Detail;

        await _framework.RunOnFrameworkThread(
            () => _port.RemoveInvisibleSkinMods(collection.Value))
            .ConfigureAwait(false);
        return failure;
    }

    /// <summary>Nulls the hair, face and tail model slots on the redrawn
    /// body — the material remap covers the body skin; these three carry
    /// the hair, the face (eyes included) and the tail. Ktisis'
    /// SetInvisibleSkin, verbatim. Framework thread only, and only on a
    /// GPose copy — the native-write gate every other write honors.</summary>
    private unsafe void StripHeadModels(ActorId actor)
    {
        var resolved = _bindings.Value.Resolve(actor);
        if (!resolved.Success || resolved.Value is not { } legacy
            || legacy.Address == nint.Zero)
            return;
        int index = ((CSGameObject*)legacy.Address)->ObjectIndex;
        if (index is < 201 or > 439)
            return;
        var characterBase = SlotCharacterBases.Resolve(
            legacy.Address, PoseSlot.Character);
        if (characterBase == null
            || characterBase->GetModelType() != CharacterBase.ModelType.Human)
            return;
        for (int slot = HairModelSlot; slot <= TailModelSlot; slot++)
        {
            if (slot >= characterBase->SlotCount)
                break;
            var model = characterBase->Models[slot];
            if (model == null)
                continue;
            characterBase->Models[slot] = null;
            model->ModelResourceHandle->DecRef();
            model->RefCount = 0;
        }
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
