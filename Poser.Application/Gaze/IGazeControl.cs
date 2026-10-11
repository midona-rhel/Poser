using System.Numerics;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Application.Gaze;

public readonly record struct GazeSettings(
    GazeTargetMode Mode, GazeTargetType TargetType,
    Vector3 Position, Vector3 EyesPosition, Vector3 HeadPosition, Vector3 BodyPosition,
    bool EyesLocked, bool HeadLocked, bool BodyLocked)
{
    public bool PoseAware { get; init; }

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
    Outcome SetMode(ActorId actor, GazeTargetMode mode);
    Outcome SetPoseAware(ActorId actor, bool enabled);
    Outcome SetParts(ActorId actor, GazeTargetType parts);
    Outcome SetTarget(ActorId actor, ActorId target);
    Outcome SetPartLock(ActorId actor, GazeTargetType part, bool locked);
    Outcome SnapPartToCamera(ActorId actor, GazeTargetType part);
    Outcome Reset(ActorId actor);
    Outcome SetGazePosition(ActorId actor, Vector3 position);
    Outcome SetPartPosition(ActorId actor, GazeTargetType part, Vector3 position);
    void Seal();
}
