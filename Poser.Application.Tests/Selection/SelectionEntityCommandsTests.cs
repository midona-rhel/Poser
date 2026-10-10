using Poser.Application.Scene;
using Poser.Application.Selection;
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
        var commands = new SelectionEntityCommands(reads, port);

        Assert.Null(commands.ReadVisibility(id));
        Assert.Equal(0, commands.SetVisibility([id], visible: false));
        Assert.Equal(0, (await commands.Remove([id])).AppliedCount);
        Assert.Equal(0, port.RemovalBatchCount);
        Assert.Empty(port.Calls);
        Assert.True(port.Visibility);
    }

    [Fact]
    public async Task Removal_dispatches_one_deduplicated_batch_and_preserves_per_item_outcomes()
    {
        var ids = Enumerable.Range(0, 4).Select(_ => SelectionId.ForProp(PropId.New())).ToArray();
        var statuses = new[]
        {
            SelectionRemovalStatus.Removed, SelectionRemovalStatus.AlreadyAbsent,
            SelectionRemovalStatus.Refused, SelectionRemovalStatus.Failed,
        };
        var expected = new SelectionRemovalResult(ids.Select((id, index) =>
            new SelectionRemovalItem(id, statuses[index], $"Detail {index}")).ToArray());
        var port = new RecordingPort { BatchResult = expected };
        var commands = new SelectionEntityCommands(new RemovableReads(), port);

        var result = await commands.Remove([ids[0], ids[1], ids[0], ids[2], ids[3]]);

        Assert.Same(expected, result);
        Assert.Equal(1, result.AppliedCount);
        Assert.Equal(1, port.RemovalBatchCount);
        Assert.Equal(ids, port.Calls.Select(call => call.Id));
        Assert.All(port.Calls, call => Assert.Equal(SelectionRemoval.Destroy, call.Command));
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

        public bool SetVisibility(SelectionId id, bool visible)
        {
            Calls.Add((id, visible));
            Visibility = visible;
            return true;
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
