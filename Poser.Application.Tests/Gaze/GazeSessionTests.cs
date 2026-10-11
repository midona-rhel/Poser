using System.Numerics;
using Poser.Application.Gaze;
using Poser.Application.Transforms;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Application.Tests.Gaze;

public sealed class GazeSessionTests
{
    [Fact]
    public void Drag_is_one_step_and_replays_original_and_final_points()
    {
        var port = new Runtime();
        var history = new EditHistory();
        var session = new GazeSession(new ValueJournal(history), port);
        var entries = new List<HistoryEntry>();
        history.Appended += entries.Add;
        var before = port.State.Settings;

        session.SetGazePosition(port.Actor, Vector3.One);
        session.SetGazePosition(port.Actor, new(2, 3, 4));
        Assert.Empty(entries);
        session.Seal();

        var step = Assert.IsType<JournalStep>(Assert.Single(entries));
        Assert.True(step.Undo());
        Assert.Equal(before.Position, port.State.Settings.Position);
        Assert.Equal(before.EyesPosition, port.State.Settings.EyesPosition);
        Assert.True(step.Redo());
        Assert.Equal(new Vector3(2, 3, 4), port.State.Settings.Position);
        Assert.Equal(before.EyesPosition, port.State.Settings.EyesPosition); // locked
    }

    [Fact]
    public void Old_generation_never_writes_into_replacement_actor()
    {
        var port = new Runtime();
        var history = new EditHistory();
        var session = new GazeSession(new ValueJournal(history), port);
        var old = port.Actor;
        session.SetGazePosition(old, Vector3.One);
        session.Seal();
        var step = Assert.IsType<JournalStep>(history.PeekUndo());
        port.Actor = old with { Generation = old.Generation + 1 };
        port.Writes = 0;

        Assert.Null(session.Read(old));
        Assert.False(session.SetMode(old, GazeTargetMode.Camera).Success);
        Assert.False(session.SetGazePosition(old, Vector3.Zero).Success);
        Assert.True(step.Undo()); // obsolete entry does not block earlier history
        Assert.True(step.Redo());
        Assert.Equal(0, port.Writes);
    }

    private sealed class Runtime : IGazeRuntimePort
    {
        public ActorId Actor = new(Guid.NewGuid(), 0);
        public GazeReading State = new(new(GazeTargetMode.Position, GazeTargetType.All,
            Vector3.Zero, new(4, 5, 6), Vector3.Zero, Vector3.Zero, true, false, false),
            null, true, false);
        public int Writes;
        public bool RefuseMode;
        public bool IsAvailable => true;
        public string? UnavailableDetail => null;
        public GazeReading? Read(ActorId actor) => actor == Actor ? State : null;

        private Outcome Change(ActorId actor, Func<GazeSettings, GazeSettings> change)
        {
            if (actor != Actor) return Outcome.Fail("Stale actor");
            Writes++;
            State = State with { Settings = change(State.Settings) };
            return Outcome.Ok();
        }

        public Outcome RestoreSettings(ActorId actor, GazeSettings settings) => RefuseMode
            ? Outcome.Fail("Native transition refused") : Change(actor, _ => settings);
        public Outcome SetMode(ActorId actor, GazeTargetMode mode) => RefuseMode
            ? Outcome.Fail("Native transition refused") : Change(actor, s => s with { Mode = mode });
        public Outcome SetParts(ActorId actor, GazeTargetType parts) =>
            Change(actor, s => s with { TargetType = parts });
        public Outcome SetTarget(ActorId actor, ActorId target) => Outcome.Fail("Stale target");
        public Outcome SetPartLock(ActorId actor, GazeTargetType part, bool locked) =>
            Change(actor, s => part switch
            {
                GazeTargetType.Eyes => s with { EyesLocked = locked },
                GazeTargetType.Head => s with { HeadLocked = locked },
                _ => s with { BodyLocked = locked },
            });
        public Outcome SnapPartToCamera(ActorId actor, GazeTargetType part) =>
            SetPartPosition(actor, part, new(8, 9, 10));
        public Outcome Reset(ActorId actor) => Change(actor, _ => default);
        public Outcome SetGazePosition(ActorId actor, Vector3 position) =>
            Change(actor, s => s with
            {
                Position = position,
                EyesPosition = s.EyesLocked ? s.EyesPosition : position,
                HeadPosition = s.HeadLocked ? s.HeadPosition : position,
                BodyPosition = s.BodyLocked ? s.BodyPosition : position,
            });
        public Outcome SetPartPosition(ActorId actor, GazeTargetType part, Vector3 position) =>
            Change(actor, s => part switch
            {
                GazeTargetType.Eyes => s with { EyesPosition = position },
                GazeTargetType.Head => s with { HeadPosition = position },
                _ => s with { BodyPosition = position },
            });
    }
}
