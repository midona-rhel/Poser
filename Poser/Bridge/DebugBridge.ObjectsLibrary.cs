#if DEBUG
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Poser.Application.Library;
using Poser.Application.Presentation;
using Poser.Domain.Identity;
using Poser.Domain.Library;
using Poser.Domain.Presentation;
using Poser.Domain.Scene;

namespace Poser.Bridge;

public sealed partial class DebugBridge
{
    private ISceneObjectControl BridgeObjects =>
        (ISceneObjectControl)_services.GetService(typeof(ISceneObjectControl))!;
    private IOverlayControl BridgeOverlays =>
        (IOverlayControl)_services.GetService(typeof(IOverlayControl))!;
    private ILibrarySceneSave BridgeLibrarySave =>
        (ILibrarySceneSave)_services.GetService(typeof(ILibrarySceneSave))!;
    private ILibrarySceneActions BridgeLibraryActions =>
        (ILibrarySceneActions)_services.GetService(typeof(ILibrarySceneActions))!;

    private object PropModelCommand(Dictionary<string, string> query)
    {
        if (ResolveSelection(query.GetValueOrDefault("prop", "")) is not
            { Prop: { } propId } selection)
            return new { accepted = false, applied = false, refused = true,
                reason = "Unknown prop selection." };
        var before = BridgeObjects.Read(propId);
        if (before == null)
            return new { accepted = false, applied = false, refused = true,
                reason = "The prop is no longer available." };
        if (!query.ContainsKey("model") && !query.ContainsKey("submodel")
            && !query.ContainsKey("variant"))
            return PropModelState(selection, before);
        if (!TryNumber(query, "model", out ushort model)
            || !TryNumber(query, "submodel", out ushort submodel)
            || !TryNumber(query, "variant", out byte variant))
            return new { accepted = false, applied = false, refused = true,
                reason = "model, submodel and variant are required unsigned integers." };
        var requested = before.Model with { Model = model, Submodel = submodel, Variant = variant };
        var result = BridgeObjects.SetModel(propId, requested);
        var after = BridgeObjects.Read(propId);
        bool applied = result.Success && after?.Model == requested;
        return new { accepted = true, applied, refused = !result.Success,
            reason = result.Detail, id = selection.ToString(), model, submodel, variant,
            ready = after != null, historyLabel = _transforms.UndoDescription };
    }

    private static object PropModelState(SelectionId selection, PropReading value) => new
    {
        accepted = true,
        id = selection.ToString(),
        model = value.Model.Model,
        submodel = value.Model.Submodel,
        variant = value.Model.Variant,
        ready = true,
    };

    private async Task<string> WorldObjectModel(Dictionary<string, string> query)
    {
        SelectionId? selection = null;
        WorldObjectReading? before = null;
        await _framework.RunOnFrameworkThread(() =>
        {
            selection = ResolveSelection(query.GetValueOrDefault("object", ""));
            if (selection is { WorldObject: { } id }) before = BridgeObjects.Read(id);
        });
        if (selection is not { WorldObject: { } worldId } || before == null)
            return Json(new { accepted = false, terminal = true, outcome = "Refused",
                reason = "Unknown world-object selection." });
        var stableSelection = selection.Value;
        var baseline = before;
        if (!query.TryGetValue("path", out var path))
            return await _framework.RunOnFrameworkThread(() => Json(WorldObjectState(stableSelection, baseline)));
        if (string.IsNullOrWhiteSpace(path))
            return Json(new { accepted = false, terminal = true, outcome = "Refused",
                reason = "A non-empty resource path is required." });
        Task<global::Poser.Domain.Outcome>? operation = null;
        await _framework.RunOnFrameworkThread(() =>
        {
            operation = BridgeObjects.Respawn(worldId, path);
        });
        var result = await operation!;
        return await _framework.RunOnFrameworkThread(() =>
        {
            var current = BridgeObjects.Read(worldId);
            return Json(new { accepted = true, terminal = true,
                outcome = result.Success ? "Applied" : "Refused", reason = result.Detail,
                oldStableId = stableSelection.ToString(), newStableId = current?.Id.ToString(),
                oldPath = baseline.Path, newPath = current?.Path, ready = current != null });
        });
    }

    private object WorldObjectState(SelectionId selection, WorldObjectReading value)
    {
        var transform = _parentingRuntime.Read(selection);
        return new
        {
            accepted = true,
            id = selection.ToString(),
            value.Path,
            value.Spawned,
            ready = transform != null,
            position = transform is { } pose ? new
                { pose.Position.X, pose.Position.Y, pose.Position.Z } : null,
            rotation = transform is { } rotated ? new
                { rotated.Rotation.X, rotated.Rotation.Y, rotated.Rotation.Z, rotated.Rotation.W } : null,
            scale = transform is { } scaled ? new
                { scaled.Scale.X, scaled.Scale.Y, scaled.Scale.Z } : null,
        };
    }

    private object OverlayNodeCommand(Dictionary<string, string> query)
    {
        if (string.Equals(query.GetValueOrDefault("action"), "create", StringComparison.OrdinalIgnoreCase))
        {
            if (!Enum.TryParse<OverlayNodeKind>(query.GetValueOrDefault("kind"), true, out var kind)
                || kind is not (OverlayNodeKind.Talk or OverlayNodeKind.Balloon or OverlayNodeKind.Status))
                return new { accepted = false, outcome = "Refused",
                    reason = "kind must be Talk, Balloon or Status." };
            var result = _creation.CreateOverlay(kind);
            var id = result.Handle is { } handle ? _creation.Resolve(handle)?.Overlay : null;
            bool accepted = result.Handle != null;
            bool published = id != null;
            return new { accepted = result.Handle != null,
                published, outcome = !accepted ? "Refused" : published ? "Applied" : "Pending",
                reason = result.Detail,
                id = id is { } stable ? SelectionId.ForOverlay(stable).ToString() : null };
        }
        if (ResolveSelection(query.GetValueOrDefault("overlay", "")) is not
            { Overlay: { } overlayId } selection || BridgeOverlays.Read(overlayId) is not { } reading)
            return new { accepted = false, outcome = "Refused", reason = "Unknown overlay selection." };
        if (query.ContainsKey("x") || query.ContainsKey("y") || query.ContainsKey("scale"))
        {
            var before = OverlayNodeState(selection, reading);
            bool move = query.ContainsKey("x") || query.ContainsKey("y");
            if (move && query.ContainsKey("scale"))
                return new { accepted = false, outcome = "Refused",
                    reason = "Move and scale are separate production edits." };
            BridgeOverlays.Seal();
            global::Poser.Domain.Outcome result;
            if (move)
            {
                if (!TryFloat(query, "x", out var x) || !TryFloat(query, "y", out var y))
                    return new { accepted = false, outcome = "Refused",
                        reason = "Both finite x and y are required." };
                result = BridgeOverlays.Set(overlayId, OverlayProperties.Position, new Vector2(x, y));
            }
            else
            {
                if (!TryFloat(query, "scale", out var scale))
                    return new { accepted = false, outcome = "Refused",
                        reason = "A finite scale is required." };
                result = BridgeOverlays.Set(overlayId, OverlayProperties.Scale, scale);
            }
            BridgeOverlays.Seal();
            if (!result.Success || BridgeOverlays.Read(overlayId) is not { } updated)
                return new { accepted = result.Success, applied = false, outcome = "Refused",
                    reason = result.Detail, before, after = (object?)null, historyLabel = _transforms.UndoDescription };
            return new { accepted = true, applied = true, outcome = "Applied",
                reason = result.Detail, before, after = OverlayNodeState(selection, updated),
                historyLabel = _transforms.UndoDescription };
        }
        return OverlayNodeState(selection, reading);
    }

    private static object OverlayNodeState(SelectionId selection, OverlayReading reading) => new
        {
            accepted = true,
            outcome = "Applied",
            id = selection.ToString(),
            kind = reading.State.Kind.ToString(),
            position = new { reading.State.Position.X, reading.State.Position.Y },
            rotation = (object?)null,
            scale = reading.State.Scale,
            state = new
            {
                reading.State.Name,
                reading.State.Visible,
                reading.State.Draggable,
                reading.State.Alpha,
                reading.State.Text,
                reading.State.Speaker,
                reading.State.FontSize,
                talkBackground = reading.State.TalkBackground.ToString(),
                talkCursor = reading.State.TalkCursor.ToString(),
                balloonChannel = reading.State.BalloonChannel.ToString(),
                balloonGradient = reading.State.BalloonGradient.ToString(),
                reading.State.ArrowVisible,
                reading.State.ArrowX,
                statusKind = reading.State.StatusKind.ToString(),
                reading.State.StatusIconId,
            },
            externalReferences = Array.Empty<string>(),
        };

    private object LibraryEntryCommand(Dictionary<string, string> query)
    {
        string action = query.GetValueOrDefault("action", "");
        if (!query.TryGetValue("path", out var path) || !Path.IsPathFullyQualified(path))
            return new { accepted = false, outcome = "Refused",
                reason = "An absolute path is required." };
        if (action.Equals("save", StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(path))
                return new { accepted = false, outcome = "Refused",
                    reason = "Choose a new path; bridge saves never overwrite.", path };
            if (ResolveLibrarySelection(query.GetValueOrDefault("entity", "")) is not { } entity)
                return new { accepted = false, outcome = "Refused",
                    reason = "Unknown entity selection.", path };
            var result = BridgeLibrarySave.SaveEntryTo(entity, path);
            return new { accepted = result.Success, path, kind = entity.Kind.ToString(),
                outcome = result.Success ? "Pending" : "Refused", reason = result.Detail };
        }
        if (action.Equals("spawn", StringComparison.OrdinalIgnoreCase))
        {
            if (!Enum.TryParse<PoseLibraryEntryKind>(query.GetValueOrDefault("kind"), true, out var kind)
                || !Enum.IsDefined(kind))
                return new { accepted = false, outcome = "Refused", reason = "Unknown entry kind.", path };
            if (!Enum.TryParse<ObjectPlacementMode>(query.GetValueOrDefault("placement", "AsSaved"), true,
                    out var placement) || !Enum.IsDefined(placement))
                return new { accepted = false, outcome = "Refused", reason = "Unknown placement mode.", path };
            bool fallbackToSaved = false;
            if (query.TryGetValue("fallbackToSaved", out var fallback)
                && !TryBoolean(fallback, out fallbackToSaved))
                return new { accepted = false, outcome = "Refused",
                    reason = "fallbackToSaved must be true, false, 1 or 0.", path };
            var before = ParentProbeEntities().Select(entity => entity.Id.ToString()).ToHashSet();
            var result = BridgeLibraryActions.SpawnEntry(path, kind, placement, fallbackToSaved);
            // Loading is asynchronous. The caller polls /scene and diffs /parenting;
            // IDs cannot truthfully be reported before the production load commits.
            return new { accepted = result.Success, path, kind = kind.ToString(),
                outcome = result.Success ? "Pending" : "Refused", reason = result.Detail,
                placement = placement.ToString(), fallbackToSaved,
                existingStableIds = before.ToArray() };
        }
        return new { accepted = false, outcome = "Refused", reason = "Unknown library-entry action.", path };
    }

    private object GazeTargetCommand(Dictionary<string, string> query)
    {
        if (ResolveActorId(query.GetValueOrDefault("actor", "")) is not { } actor
            || ResolveActorId(query.GetValueOrDefault("target", "")) is not { } target)
            return new { accepted = false, applied = false, reason = "Both current actors are required." };
        var result = _gaze.SetTarget(actor, target);
        _gaze.Seal();
        var reading = _gaze.Read(actor);
        return new { accepted = result.Success, applied = result.Success, reason = result.Detail,
            actor = actor.ToString(), requestedTarget = target.ToString(),
            target = reading?.Target?.ToString(), mode = reading?.Settings.Mode.ToString(),
            stale = reading?.TargetStale };
    }

    private object CameraTargetCommand(Dictionary<string, string> query)
    {
        CameraId? camera = null;
        string cameraValue = query.GetValueOrDefault("camera", "");
        foreach (var candidate in _sceneSession.Snapshot.Cameras)
        {
            var selection = SelectionId.ForCamera(candidate.Id);
            if (string.Equals(selection.ToString(), cameraValue, StringComparison.OrdinalIgnoreCase)
                && _sceneSession.Resolve(selection) == selection)
            {
                camera = candidate.Id;
                break;
            }
        }
        if (camera is not { } cameraId
            || ResolveActorId(query.GetValueOrDefault("actor", "")) is not { } actorId
            || _bindings.Resolve(actorId).Value is not { } actor)
            return new { accepted = false, applied = false,
                reason = "A current camera and actor are required." };
        var control = (ICameraTargetControl)_services.GetService(typeof(ICameraTargetControl))!;
        var result = control.Follow(cameraId, actorId,
            _configuration.GetDisplayName(actorId.LogicalId, actor.Name));
        var reading = control.Read(cameraId);
        return new { accepted = result.Success, applied = result.Success, reason = result.Detail,
            camera = cameraId.ToString(), requestedActor = actorId.ToString(),
            followedActor = reading?.FollowedActor?.ToString(), reading?.IsLocked,
            reading?.IsTracking, reading?.IsTargetLocked, trackingMode = reading?.TrackingMode.ToString() };
    }

    private static bool TryNumber<T>(Dictionary<string, string> query, string key, out T value)
        where T : struct, System.Numerics.INumberBase<T>
    {
        value = default;
        return query.TryGetValue(key, out var text)
            && T.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private SelectionId? ResolveLibrarySelection(string value)
    {
        if (ResolveSelection(value) is { } entity)
            return entity;
        foreach (var camera in _sceneSession.Snapshot.Cameras)
        {
            var id = SelectionId.ForCamera(camera.Id);
            if (string.Equals(id.ToString(), value, StringComparison.OrdinalIgnoreCase)
                && _sceneSession.Resolve(id) == id)
                return id;
        }
        return null;
    }

    private static bool TryFloat(Dictionary<string, string> query, string key, out float value)
    {
        value = default;
        return query.TryGetValue(key, out var text)
            && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && float.IsFinite(value);
    }
}
#endif
