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
/// step from where it last rested. A move still settling when another step is
/// added, or when undo is pressed, is recorded first: it comes back before that
/// step, and that undo takes it back. Poser's own
/// camera edits (a slider being dragged, a gesture), undo and redo re-base the
/// resting framing instead, so nothing is recorded twice. So does anything the
/// camera does in the half second after a step, because the game camera follows
/// an actor and rotates when that actor sits down.
/// </summary>
public sealed class CameraRestRecorder : IDisposable
{
    public const long RestMilliseconds = 5000;

    /// <summary>How long after a step the camera's changes belong to that step.
    /// The game camera follows an actor, so an actor sitting down rotates it a
    /// little in the frame after the emote starts.</summary>
    private const long SettleMilliseconds = 500;

    private readonly IFramework _framework;
    private readonly IVirtualCameraService _cameras;
    private readonly ValueJournal _journal;
    private readonly EditHistory _history;
    private readonly TransformGestureService _gestures;
    private readonly UndoJournal _undo;
    private readonly IEntityHistoryResolver<IVirtualCamera> _restored;
    private IVirtualCamera? _camera;
    private Framing _rested;
    private Framing _last;
    private long _changedAt;
    // True while the camera's last seen framing differs from the rested one.
    private bool _moved;
    private bool _rebase;
    private bool _poserEdit;
    private long _settleUntil;

    public CameraRestRecorder(IFramework framework, IVirtualCameraService cameras,
        ValueJournal journal, EditHistory history, TransformGestureService gestures, UndoJournal undo,
        IEntityHistoryResolver<IVirtualCamera> restored)
    {
        _restored = restored;
        _framework = framework;
        _cameras = cameras;
        _journal = journal;
        _history = history;
        _gestures = gestures;
        _undo = undo;
        _undo.Undoing += RecordSettlingMove;
        _undo.HasUnrecordedChange = HasSettlingMove;
        _history.Appending += RecordSettlingMove;
        _history.Appended += OnAppended;
        _history.Stepped += Rebase;
        _history.Cleared += Rebase;
        _framework.Update += Tick;
    }

    private void OnAppended(HistoryEntry _) => _rebase = true;
    private void Rebase() => _rebase = true;

    /// <summary>Another step is about to be added, or undo was pressed: a camera
    /// move that has not rested its 5 seconds yet is recorded first, up to the
    /// framing the last tick saw.</summary>
    private void RecordSettlingMove()
    {
        if (HasSettlingMove())
            Record(_camera!, _last);
    }

    /// <summary>Whether the camera was moved by its own controls and that move
    /// is not an undo step yet.</summary>
    private bool HasSettlingMove() =>
        // The camera may have been deleted since the last tick.
        _camera is { IsValid: true } && _moved && !_poserEdit;

    private void Tick(IFramework _)
    {
        // There is a live camera only in GPose, and only while the camera hooks loaded.
        if (_cameras.LiveCamera is not { } live)
        {
            _camera = null;
            return;
        }
        var now = Framing.Read(live);
        if (!ReferenceEquals(live, _camera) || _rebase)
        {
            if (_rebase)
                _settleUntil = System.Environment.TickCount64 + SettleMilliseconds;
            _camera = live;
            _rested = _last = now;
            _moved = false;
            _rebase = false;
            _poserEdit = false;
            return;
        }
        if (System.Environment.TickCount64 < _settleUntil)
        {
            // A step was just added, undone or redone: what the camera does
            // now follows from that step, not from a person moving it.
            _rested = _last = now;
            _moved = false;
            return;
        }
        if (!now.Near(_last))
        {
            _last = now;
            _changedAt = System.Environment.TickCount64;
            _moved = !now.Near(_rested);
            // Poser itself is moving the camera: its own step will follow.
            _poserEdit |= _journal.IsStaging || _gestures.ActiveGesture != null;
            return;
        }
        if (!_moved || System.Environment.TickCount64 - _changedAt < RestMilliseconds)
            return;
        if (_poserEdit)
        {
            _rested = now;
            _moved = false;
            _poserEdit = false;
            return;
        }
        Record(live, now);
    }

    private void Record(IVirtualCamera camera, Framing after)
    {
        // Cleared first: recording adds a step, which calls RecordSettlingMove again.
        _moved = false;
        var before = _rested;
        _rested = after;
        // The camera service as the first part of the key keeps this step in
        // global undo. A camera deleted and brought back by undo is a new
        // object, so the step looks up the current one. The journal runs the
        // last argument right before each write: when the camera is gone,
        // undo and redo of this step do nothing.
        IVirtualCamera? Current() => _restored.Resolve(camera) is { IsValid: true } live ? live : null;
        _journal.Record((_cameras, camera, nameof(CameraRestRecorder)), "Move camera", before, after, state =>
        {
            state.Apply(Current()!);
            return Outcome.Ok();
        }, () => Current() != null);
    }

    /// <summary>What the camera's own controls change: a free camera's place and
    /// rotation, the game camera's orbit, and for both roll and field of view.</summary>
    private readonly record struct Framing(
        CameraKind Kind, Vector3 Position, Vector3 Rotation, Vector3 PositionOffset,
        Vector2 Angle, Vector2 Pan, float Zoom, float Roll, float FoV)
    {
        public static Framing Read(IVirtualCamera c) => new(
            c.Kind, c.Position, c.Rotation, c.PositionOffset, c.Angle, c.Pan, c.Zoom, c.Roll, c.FoV);

        /// <summary>Compares two framings of the same camera, so the kind always matches.</summary>
        public bool Near(Framing other) =>
            Vector3.DistanceSquared(Position, other.Position) < 1e-8f
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
        _undo.Undoing -= RecordSettlingMove;
        _undo.HasUnrecordedChange = null;
        _history.Appending -= RecordSettlingMove;
        _history.Appended -= OnAppended;
        _history.Stepped -= Rebase;
        _history.Cleared -= Rebase;
    }
}
