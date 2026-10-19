using System.Numerics;
using Dalamud.Plugin.Services;
using Poser.Application.Transforms;
using Poser.Domain;
using Poser.Domain.Scene;
using Poser.Game.Entities;
using Poser.Game.Services;

namespace Poser.Game.Cameras;

/// <summary>
/// Moving the camera with the game's own controls, or a free camera with its
/// keys, leaves no step behind. Once the live camera has moved and then held
/// still for <see cref="RestMilliseconds"/>, the new framing becomes one undo
/// step from where it last rested. Poser's own camera edits, undo and redo
/// re-base the resting framing instead, so nothing is recorded twice.
/// </summary>
public sealed class CameraRestRecorder : IDisposable
{
    public const long RestMilliseconds = 5000;
    private const string Unavailable = "The camera is no longer available.";

    private readonly IFramework _framework;
    private readonly IVirtualCameraService _cameras;
    private readonly ValueJournal _journal;
    private readonly EditHistory _history;
    private IVirtualCamera? _camera;
    private Framing _rested;
    private Framing _last;
    private long _changedAt;
    private bool _moved;
    private bool _rebase;

    public CameraRestRecorder(IFramework framework, IVirtualCameraService cameras,
        ValueJournal journal, EditHistory history)
    {
        _framework = framework;
        _cameras = cameras;
        _journal = journal;
        _history = history;
        _history.Appended += OnAppended;
        _history.Stepped += Rebase;
        _history.Cleared += Rebase;
        _framework.Update += Tick;
    }

    private void OnAppended(HistoryEntry _) => _rebase = true;
    private void Rebase() => _rebase = true;

    private void Tick(IFramework _)
    {
        if (!_cameras.IsAvailable || _cameras.LiveCamera is not { IsValid: true } live)
        {
            _camera = null;
            return;
        }
        var now = Framing.Read(live);
        if (!ReferenceEquals(live, _camera) || _rebase)
        {
            _camera = live;
            _rested = _last = now;
            _moved = false;
            _rebase = false;
            return;
        }
        if (!now.Near(_last))
        {
            _last = now;
            _changedAt = System.Environment.TickCount64;
            _moved = !now.Near(_rested);
            return;
        }
        if (!_moved || System.Environment.TickCount64 - _changedAt < RestMilliseconds)
            return;
        _moved = false;
        var before = _rested;
        _rested = now;
        var camera = live;
        // Keys led by the camera service name no entity: this step stays global.
        _journal.Record((_cameras, camera, nameof(CameraRestRecorder)), "Move camera", before, now, state =>
        {
            if (!camera.IsValid) return new(false, Unavailable);
            state.Apply(camera);
            return Outcome.Ok();
        }, () => camera.IsValid);
    }

    /// <summary>What the camera's own controls change: a free camera's place and
    /// turn, the game camera's orbit, and for both roll and field of view.</summary>
    private readonly record struct Framing(
        CameraKind Kind, Vector3 Position, Vector3 Rotation, Vector3 PositionOffset,
        Vector2 Angle, Vector2 Pan, float Zoom, float Roll, float FoV)
    {
        public static Framing Read(IVirtualCamera c) => new(
            c.Kind, c.Position, c.Rotation, c.PositionOffset, c.Angle, c.Pan, c.Zoom, c.Roll, c.FoV);

        public bool Near(Framing other) =>
            Kind == other.Kind
            && Vector3.DistanceSquared(Position, other.Position) < 1e-8f
            && Vector3.DistanceSquared(Rotation, other.Rotation) < 1e-8f
            && Vector3.DistanceSquared(PositionOffset, other.PositionOffset) < 1e-8f
            && Vector2.DistanceSquared(Angle, other.Angle) < 1e-8f
            && Vector2.DistanceSquared(Pan, other.Pan) < 1e-8f
            && MathF.Abs(Zoom - other.Zoom) < 1e-3f
            && MathF.Abs(Roll - other.Roll) < 1e-4f
            && MathF.Abs(FoV - other.FoV) < 1e-4f;

        public void Apply(IVirtualCamera c)
        {
            if (Kind == CameraKind.Free)
            {
                c.Position = Position;
                c.Rotation = Rotation;
            }
            else
            {
                c.Angle = Angle;
                c.Pan = Pan;
                c.Zoom = Zoom;
                c.PositionOffset = PositionOffset;
            }
            c.Roll = Roll;
            c.FoV = FoV;
        }
    }

    public void Dispose()
    {
        _framework.Update -= Tick;
        _history.Appended -= OnAppended;
        _history.Stepped -= Rebase;
        _history.Cleared -= Rebase;
    }
}
