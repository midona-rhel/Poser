using System.Numerics;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Application.Gaze;

public readonly record struct GazeSettings(
    GazeTargetMode Mode, GazeTargetType TargetType,
    Vector3 Position, Vector3 EyesPosition, Vector3 HeadPosition, Vector3 BodyPosition,
    bool EyesLocked, bool HeadLocked, bool BodyLocked)
{
    public bool IsPartLocked(GazeTargetType part) =>
        ((part & GazeTargetType.Eyes) != 0 && EyesLocked) ||
        ((part & GazeTargetType.Head) != 0 && HeadLocked) ||
        ((part & GazeTargetType.Body) != 0 && BodyLocked);

    public Vector3 PartPosition(GazeTargetType part) => part switch
    {
        GazeTargetType.Eyes => EyesPosition,
        GazeTargetType.Head => HeadPosition,
        GazeTargetType.Body => BodyPosition,
        _ => Position,
    };
}

public sealed record GazeReading(
    GazeSettings Settings, ActorId? Target, bool Active, bool TargetStale);

/// <summary>UI-facing gaze control. Commands and history name exact actor generations.</summary>
public interface IGazeControl
{
    bool IsAvailable { get; }
    string? UnavailableDetail { get; }
    GazeReading? Read(ActorId actor);
    GazeResult SetMode(ActorId actor, GazeTargetMode mode);
    GazeResult SetParts(ActorId actor, GazeTargetType parts);
    GazeResult SetTarget(ActorId actor, ActorId target);
    GazeResult SetPartLock(ActorId actor, GazeTargetType part, bool locked);
    GazeResult SnapPartToCamera(ActorId actor, GazeTargetType part);
    GazeResult Reset(ActorId actor);
    GazeResult SetGazePosition(ActorId actor, Vector3 position);
    GazeResult SetPartPosition(ActorId actor, GazeTargetType part, Vector3 position);
    void Seal();
}
