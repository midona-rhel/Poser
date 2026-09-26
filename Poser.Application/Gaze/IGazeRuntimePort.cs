using System.Numerics;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Application.Gaze;

/// <summary>Native mechanisms only; each call resolves the exact generation again.</summary>
public interface IGazeRuntimePort
{
    bool IsAvailable { get; }
    string? UnavailableDetail { get; }
    GazeReading? Read(ActorId actor);
    GazeResult RestoreSettings(ActorId actor, GazeSettings settings);
    GazeResult SetMode(ActorId actor, GazeTargetMode mode);
    GazeResult SetParts(ActorId actor, GazeTargetType parts);
    GazeResult SetTarget(ActorId actor, ActorId target);
    GazeResult SetPartLock(ActorId actor, GazeTargetType part, bool locked);
    GazeResult SnapPartToCamera(ActorId actor, GazeTargetType part);
    GazeResult Reset(ActorId actor);
    GazeResult SetGazePosition(ActorId actor, Vector3 position);
    GazeResult SetPartPosition(ActorId actor, GazeTargetType part, Vector3 position);
}
