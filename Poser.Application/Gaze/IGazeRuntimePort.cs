using System.Numerics;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Application.Gaze;

/// <summary>Native mechanisms only; each call resolves the exact generation again.</summary>
public interface IGazeRuntimePort
{
    bool IsAvailable { get; }
    string? UnavailableDetail { get; }
    GazeReading? Read(ActorId actor);
    Outcome RestoreSettings(ActorId actor, GazeSettings settings);
    Outcome SetMode(ActorId actor, GazeTargetMode mode);
    Outcome SetParts(ActorId actor, GazeTargetType parts);
    Outcome SetTarget(ActorId actor, ActorId target);
    Outcome SetPartLock(ActorId actor, GazeTargetType part, bool locked);
    Outcome SnapPartToCamera(ActorId actor, GazeTargetType part);
    Outcome Reset(ActorId actor);
    Outcome SetGazePosition(ActorId actor, Vector3 position);
    Outcome SetPartPosition(ActorId actor, GazeTargetType part, Vector3 position);
}
