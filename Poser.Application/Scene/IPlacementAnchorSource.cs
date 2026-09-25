using System;
using System.Collections.Generic;
using System.Numerics;
using Poser.Core;
using Poser.Domain.Actors;
using Poser.Domain.Identity;
using Poser.Domain.Operations;
using Poser.Domain.Posing;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;
using Poser.Entities;
using Poser.Files;
using Poser.Scene;

namespace Poser.Services;

/// <summary>Where a spawn lands: the anchor for each placement mode.</summary>
public interface IPlacementAnchorSource
{
    PlacementAnchorData? CameraAnchorNow();
    PlacementAnchorData? ActorAnchorNow();
    bool TryCurrentFor( ObjectPlacementMode mode, out Vector3 position, out float yaw, out string? refusal);
}
