#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using Poser.Application.Scene;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Presentation;
using Poser.Domain.Scene;

namespace Poser.Bridge;

public sealed partial class DebugBridge
{
    private object PlacementCommand(Dictionary<string, string> query)
    {
        if (query.TryGetValue("mode", out var requested))
        {
            if (!Enum.TryParse<ObjectPlacementMode>(requested, true, out var mode) || !Enum.IsDefined(mode))
                return new { error = "Unknown placement mode." };
            _configuration.Config.DefaultSpawnPlacement = mode;
        }
        SceneCreationResult? made = null;
        if (query.TryGetValue("create", out var kind))
            made = kind switch
            {
                "actor" => _creation.CreateActor(new()),
                "light" => _creation.CreateLight(LightKind.Spot),
                "prop" => _creation.CreateProp(),
                "camera" => _creation.CreateCamera(CameraKind.Free),
                "orbit" => _creation.CreateCamera(CameraKind.Game),
                "collider" => _creation.CreateCollider(IkColliderShape.Sphere),
                "overlay" => _creation.CreateOverlay(OverlayNodeKind.Talk),
                "world" when query.TryGetValue("path", out var path) => _creation.CreateWorldObject(path),
                _ => new(null, "Unknown creation kind or missing world-object path."),
            };
        IReadOnlyDictionary<SelectionId, SceneCreationResult>? copies = null;
        if (query.TryGetValue("duplicate", out var keys))
        {
            var ids = keys.Split(',').Select(ResolveLibrarySelection).ToArray();
            if (ids.Any(id => id == null)) return new { error = "Unknown duplicate source." };
            copies = _creation.DuplicateMany(ids.Select(id => id!.Value).ToArray(),
                withPose: query.GetValueOrDefault("posed") == "true");
        }
        var selectedMode = _configuration.Config.DefaultSpawnPlacement;
        var available = _anchors.TryCurrentFor(selectedMode, out var anchor, out _, out var refusal);
        return new
        {
            mode = selectedMode.ToString(), available, anchor, refusal,
            created = made == null ? null : (bool?)(made.Handle != null), detail = made?.Detail,
            copies = copies?.Select(copy => new { source = copy.Key.ToString(), ok = copy.Value.Handle != null, copy.Value.Detail }),
            entities = SelectionProbeEntities().Where(entry => entry.Id.Bone == null).Select(entry => new
            {
                id = entry.Id.ToString(), entry.Name, kind = entry.Id.Kind.ToString(),
                position = entry.Id.Camera is { } camera
                    ? _bindings.Resolve(camera).Value?.WorldPosition
                    : _parentingRuntime.Read(entry.Id)?.Position,
            }).ToArray(),
        };
    }
}
#endif

