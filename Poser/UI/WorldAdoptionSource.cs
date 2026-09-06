using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Poser.Application.Selection;
using Poser.Application.World;
using Poser.Services;

namespace Poser.UI;

public enum WorldAdoptionKind { Actor, Light, WorldObject, Effect }
public static class WorldAdoptionClasses
{
    public static readonly WorldAdoptionKind[] All =
        [WorldAdoptionKind.Actor, WorldAdoptionKind.Light, WorldAdoptionKind.WorldObject, WorldAdoptionKind.Effect];
}

public readonly record struct WorldAdoptionCandidate(
    WorldAdoptionKind Kind, string Name, Vector3 Position, float DistanceFromCamera, WorldCandidateId Id);

/// <summary>Overlay-only filters, projection range and selection; world commands belong to IWorldService.</summary>
public sealed class WorldAdoptionSource(IWorldService world, ICameraService camera, SelectionSession selection, UserNotices notices)
{
    public const float RangeYalms = 30f;
    public bool ShowActors { get; set; }
    public bool ShowLights { get; set; }
    public bool ShowWorldObjects { get; set; }
    public bool ShowEffects { get; set; }
    public bool Enabled => ShowActors || ShowLights || ShowWorldObjects || ShowEffects;
    private readonly List<WorldAdoptionCandidate> _candidates = new();
    private Task<WorldSnapshot>? _refresh;
    private Task<WorldAcquisition>? _acquire;
    public IReadOnlyList<WorldAdoptionCandidate> Candidates => _candidates;

    public bool IsShown(WorldAdoptionKind kind) => kind switch
    {
        WorldAdoptionKind.Actor => ShowActors,
        WorldAdoptionKind.Light => ShowLights,
        WorldAdoptionKind.WorldObject => ShowWorldObjects,
        _ => ShowEffects,
    };
    public void SetShown(WorldAdoptionKind kind, bool shown)
    {
        switch (kind)
        {
            case WorldAdoptionKind.Actor: ShowActors = shown; break;
            case WorldAdoptionKind.Light: ShowLights = shown; break;
            case WorldAdoptionKind.WorldObject: ShowWorldObjects = shown; break;
            case WorldAdoptionKind.Effect: ShowEffects = shown; break;
        }
    }
    public void EndSession()
    {
        ShowActors = ShowLights = ShowWorldObjects = ShowEffects = false;
        SetHovered(null);
        _candidates.Clear();
        _acquire = null;
        _refresh = null;
    }
    public void SetHovered(WorldAdoptionCandidate? candidate) => world.Highlight(candidate?.Id);
    public void Adopt(in WorldAdoptionCandidate candidate)
    {
        if (_acquire is { IsCompleted: false }) return;
        _acquire = world.Acquire(candidate.Id);
    }
    public void Tick()
    {
        if (_acquire is { IsCompleted: true } acquire)
        {
            _acquire = null;
            if (acquire.IsCompletedSuccessfully && acquire.Result.Entity is { } entity)
                selection.Select(entity);
            else notices.Refused(acquire.IsCompletedSuccessfully
                ? acquire.Result.Detail ?? "That world asset could not be borrowed."
                : "The world borrowing command failed.");
        }
        _candidates.Clear();
        if (!Enabled) return;
        var kinds = (ShowActors ? WorldKinds.Actor : WorldKinds.None)
            | (ShowLights ? WorldKinds.Light : WorldKinds.None)
            | (ShowWorldObjects ? WorldKinds.Object : WorldKinds.None)
            | (ShowEffects ? WorldKinds.Effect : WorldKinds.None);
        if (_refresh is not { IsCompleted: false }) _refresh = world.Refresh(kinds);
        var eye = camera.GetCameraPosition();
        foreach (var row in world.Snapshot.Candidates)
        {
            var kind = row.Kind switch
            {
                WorldKinds.Actor => WorldAdoptionKind.Actor,
                WorldKinds.Light => WorldAdoptionKind.Light,
                WorldKinds.Object => WorldAdoptionKind.WorldObject,
                _ => WorldAdoptionKind.Effect,
            };
            if (!IsShown(kind)) continue;
            // Ktisis ranges adoption handles from the camera in the ground plane.
            float distance = Vector2.Distance(new(eye.X, eye.Z), new(row.Position.X, row.Position.Z));
            if (distance <= RangeYalms)
                _candidates.Add(new(kind, row.Name, row.Position, distance, row.Id));
        }
        _candidates.Sort((a, b) => a.DistanceFromCamera.CompareTo(b.DistanceFromCamera));
    }
}
