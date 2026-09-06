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

/// <summary>Spawned world objects and the render-phase animation pump. Borrowing is controlled by IWorldService.</summary>
public interface IWorldObjectService
{
    bool AnchorPumpedFromRender { get; set; }
    void HoldPausedAnimations();
    bool IsAvailable { get; }
    IWorldObject? Spawn( string path, Transform placement, bool visible, out string? detail);
}
