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

/// <summary>World objects: the candidates, a spawn, the outline and the paused-animation hold.</summary>
public interface IWorldObjectService
{
    bool AnchorPumpedFromRender { get; set; }
    void HoldPausedAnimations();
    bool IsAvailable { get; }
    IReadOnlyList<WorldObjectCandidate> GetCandidates();
    IReadOnlyList<WorldObjectCandidate> GetEffectCandidates();
    bool TryReadOutline(nint address, out byte outline);
    void WriteOutline(nint address, byte outline);
    IWorldObject? Spawn( string path, Transform placement, bool visible, out string? detail);
}
