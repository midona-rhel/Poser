using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Application.Transforms;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Application.Tests.Selection;

public sealed class SelectionEntityCommandsTests
{
    [Fact]
    public async Task Commands_refuse_a_read_that_substitutes_a_successor_identity()
    {
        var original = PropId.New();
        var id = SelectionId.ForProp(original);
        var reads = new SubstitutingReads(new CurrentSelectionEntity(
            SelectionId.ForProp(original.NextGeneration()),
            CanChangeVisibility: true, IsVisible: true,
            SelectionRemoval.Destroy));
        var port = new RecordingPort();
        var commands = new SelectionEntityCommands(reads, port, new TransformHistory());

        Assert.Null(commands.ReadVisibility(id));
        var hidden = commands.SetVisibility([id], visible: false);
        Assert.Equal(0, hidden.AppliedCount);
        Assert.False(Assert.Single(hidden.Items).Result.Success);
        Assert.Equal(0, (await commands.Remove([id])).AppliedCount);
        Assert.Equal(0, port.RemovalBatchCount);
        Assert.Empty(port.Calls);
        Assert.True(port.Visibility);
    }

    [Fact]
    public void Hide_selection_is_one_entry_and_replays_only_landed_targets()
    {
        var history = new TransformHistory();
        var actors = new[] { SelectionId.ForActor(ActorId.New()), SelectionId.ForActor(ActorId.New()) };
        var light = SelectionId.ForLight(LightId.New());
        var prop = SelectionId.ForProp(PropId.New());
        var port = new RecordingPort { History = history, Refuse = { [prop] = "The prop refused." } };
        var commands = new SelectionEntityCommands(new RemovableReads(), port, history);

        var result = commands.SetVisibility([.. actors, light, prop], visible: false);

        Assert.Equal(3, result.AppliedCount);
        Assert.Equal("The prop refused.", Assert.Single(result.Items, item => !item.Result.Success).Result.Detail);
        var entry = Assert.IsType<SceneLifecyclePatch>(history.PeekUndo());
        Assert.Equal("Hide entities", entry.Description);
        Assert.True(entry.Undo());
        history.CommitUndo(entry);
        Assert.False(history.CanUndo);
        Assert.All(actors.Append(light), id => Assert.True(port.Visible[id]));
        Assert.False(port.Visible.ContainsKey(prop));
        Assert.True(entry.Redo());
        history.CommitRedo(entry);
        Assert.All(actors.Append(light), id => Assert.False(port.Visible[id]));
        Assert.False(port.Visible.ContainsKey(prop));
    }

    private sealed class RemovableReads : ICurrentSelectionEntityReads
    {
        public CurrentSelectionEntity ReadCurrent(SelectionId id) =>
            new(id, true, true, SelectionRemoval.Destroy);
    }

    private sealed class SubstitutingReads(CurrentSelectionEntity value)
        : ICurrentSelectionEntityReads
    {
        public CurrentSelectionEntity? ReadCurrent(SelectionId id) => value;
    }

    private sealed class RecordingPort : ISelectionEntityCommandPort
    {
        public List<(SelectionId Id, object Command)> Calls { get; } = [];
        public int RemovalBatchCount { get; private set; }
        public SelectionRemovalResult? BatchResult { get; init; }
        public bool? Visibility { get; set; } = true;

        public bool? ReadVisibility(SelectionId id) => Visibility;

        public TransformHistory? History { get; init; }
        public Dictionary<SelectionId, string> Refuse { get; } = new();
        public Dictionary<SelectionId, bool> Visible { get; } = new();

        public Outcome SetVisibility(SelectionId id, bool visible)
        {
            Calls.Add((id, visible));
            if (Refuse.TryGetValue(id, out var detail)) return new(false, detail);
            Visibility = visible;
            Visible[id] = visible;
            // Mirrors a value session: a landed write appends one step.
            History?.Append(new JournalStep(visible ? "Show" : "Hide",
                () => { Visible[id] = !visible; return true; },
                () => { Visible[id] = visible; return true; }) { AffectedEntities = [id] });
            return Outcome.Ok();
        }

        public Task<SelectionRemovalResult> Remove(IReadOnlyList<SelectionRemovalRequest> requests)
        {
            RemovalBatchCount++;
            foreach (var request in requests)
                Calls.Add((request.Id, request.Removal));
            return Task.FromResult(BatchResult ?? new SelectionRemovalResult(
                requests.Select(request => new SelectionRemovalItem(request.Id,
                    SelectionRemovalStatus.Removed)).ToArray()));
        }
    }
}
