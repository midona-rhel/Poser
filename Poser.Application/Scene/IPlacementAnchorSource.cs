using System;
using System.Collections.Generic;
using System.Numerics;
using Poser.Domain.Actors;
using Poser.Domain.Identity;
using Poser.Domain.Operations;
using Poser.Domain.Posing;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;
using Poser.Documents.Files;

namespace Poser.Application.Scene;

/// <summary>The point and heading each placement mode measures from: the
/// camera or the selected actor. Used to place new entities and loaded
/// scenes.</summary>
public interface IPlacementAnchorSource
{
    PlacementAnchorData? CameraAnchorNow();
    PlacementAnchorData? ActorAnchorNow();
    /// <param name="onGround">For In front of camera: true puts the point on the
    /// ground in front of the camera, at the height of the actor being worked on
    /// (actors, props, objects, loaded scenes); false puts it 3 m along the view
    /// (lights, cameras, colliders).</param>
    bool TryCurrentFor(ObjectPlacementMode mode, out Vector3 position, out float yaw, out string? refusal,
        bool onGround = true);
}
