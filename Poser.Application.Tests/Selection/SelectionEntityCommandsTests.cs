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
