using Poser.History;
using Poser.Services;
using Poser.Tests.Mocks;
using Xunit;

namespace Poser.Tests.Services;

public class HistoryServiceTests
{
    private class MockHistoryAction : IHistoryAction
    {
        public string Description { get; }
        public int ExecuteCount { get; private set; }
        public int UndoCount { get; private set; }

        public MockHistoryAction(string description = "Test Action")
        {
            Description = description;
        }

        public void Execute() => ExecuteCount++;
        public void Undo() => UndoCount++;
    }

    [Fact]
    public void CanUndo_WhenEmpty_ReturnsFalse()
    {
        var gPoseService = new MockGPoseService();
        var historyService = new HistoryService(gPoseService);

        Assert.False(historyService.CanUndo);
    }

    [Fact]
    public void CanRedo_WhenEmpty_ReturnsFalse()
    {
        var gPoseService = new MockGPoseService();
        var historyService = new HistoryService(gPoseService);

        Assert.False(historyService.CanRedo);
    }

    [Fact]
    public void Push_ExecutesActionAndEnablesUndo()
    {
        var gPoseService = new MockGPoseService();
        var historyService = new HistoryService(gPoseService);
        var action = new MockHistoryAction();

        historyService.Push(action);

        Assert.Equal(1, action.ExecuteCount);
        Assert.True(historyService.CanUndo);
    }

    [Fact]
    public void Record_DoesNotExecuteAction()
    {
        var gPoseService = new MockGPoseService();
        var historyService = new HistoryService(gPoseService);
        var action = new MockHistoryAction();

        historyService.Record(action);

        Assert.Equal(0, action.ExecuteCount);
        Assert.True(historyService.CanUndo);
    }

    [Fact]
    public void Undo_CallsUndoOnAction()
    {
        var gPoseService = new MockGPoseService();
        var historyService = new HistoryService(gPoseService);
        var action = new MockHistoryAction();
        historyService.Push(action);

        historyService.Undo();

        Assert.Equal(1, action.UndoCount);
        Assert.False(historyService.CanUndo);
        Assert.True(historyService.CanRedo);
    }

    [Fact]
    public void Redo_ReExecutesAction()
    {
        var gPoseService = new MockGPoseService();
        var historyService = new HistoryService(gPoseService);
        var action = new MockHistoryAction();
        historyService.Push(action);
        historyService.Undo();

        historyService.Redo();

        Assert.Equal(2, action.ExecuteCount); // Once on Push, once on Redo
        Assert.True(historyService.CanUndo);
        Assert.False(historyService.CanRedo);
    }

    [Fact]
    public void Push_ClearsRedoStack()
    {
        var gPoseService = new MockGPoseService();
        var historyService = new HistoryService(gPoseService);
        var action1 = new MockHistoryAction("Action 1");
        var action2 = new MockHistoryAction("Action 2");
        historyService.Push(action1);
        historyService.Undo();
        Assert.True(historyService.CanRedo);

        historyService.Push(action2);

        Assert.False(historyService.CanRedo);
    }

    [Fact]
    public void UndoDescription_ReturnsTopActionDescription()
    {
        var gPoseService = new MockGPoseService();
        var historyService = new HistoryService(gPoseService);
        var action = new MockHistoryAction("Test Description");

        historyService.Push(action);

        Assert.Equal("Test Description", historyService.UndoDescription);
    }

    [Fact]
    public void RedoDescription_ReturnsUndoneActionDescription()
    {
        var gPoseService = new MockGPoseService();
        var historyService = new HistoryService(gPoseService);
        var action = new MockHistoryAction("Test Description");
        historyService.Push(action);

        historyService.Undo();

        Assert.Equal("Test Description", historyService.RedoDescription);
    }

    [Fact]
    public void Clear_RemovesAllHistory()
    {
        var gPoseService = new MockGPoseService();
        var historyService = new HistoryService(gPoseService);
        historyService.Push(new MockHistoryAction());
        historyService.Push(new MockHistoryAction());
        historyService.Undo();

        historyService.Clear();

        Assert.False(historyService.CanUndo);
        Assert.False(historyService.CanRedo);
    }

    [Fact]
    public void OnHistoryChanged_FiresOnPush()
    {
        var gPoseService = new MockGPoseService();
        var historyService = new HistoryService(gPoseService);
        int eventCount = 0;
        historyService.OnHistoryChanged += () => eventCount++;

        historyService.Push(new MockHistoryAction());

        Assert.Equal(1, eventCount);
    }

    [Fact]
    public void OnHistoryChanged_FiresOnUndo()
    {
        var gPoseService = new MockGPoseService();
        var historyService = new HistoryService(gPoseService);
        historyService.Push(new MockHistoryAction());
        int eventCount = 0;
        historyService.OnHistoryChanged += () => eventCount++;

        historyService.Undo();

        Assert.Equal(1, eventCount);
    }

    [Fact]
    public void OnHistoryChanged_FiresOnRedo()
    {
        var gPoseService = new MockGPoseService();
        var historyService = new HistoryService(gPoseService);
        historyService.Push(new MockHistoryAction());
        historyService.Undo();
        int eventCount = 0;
        historyService.OnHistoryChanged += () => eventCount++;

        historyService.Redo();

        Assert.Equal(1, eventCount);
    }

    [Fact]
    public void ExitingGPose_ClearsHistory()
    {
        var gPoseService = new MockGPoseService();
        var historyService = new HistoryService(gPoseService);
        gPoseService.SetGPoseState(true);
        historyService.Push(new MockHistoryAction());
        historyService.Push(new MockHistoryAction());

        gPoseService.SetGPoseState(false);

        Assert.False(historyService.CanUndo);
        Assert.False(historyService.CanRedo);
    }

    [Fact]
    public void MultipleUndoRedo_WorksCorrectly()
    {
        var gPoseService = new MockGPoseService();
        var historyService = new HistoryService(gPoseService);
        var action1 = new MockHistoryAction("Action 1");
        var action2 = new MockHistoryAction("Action 2");
        var action3 = new MockHistoryAction("Action 3");

        historyService.Push(action1);
        historyService.Push(action2);
        historyService.Push(action3);

        Assert.Equal("Action 3", historyService.UndoDescription);

        historyService.Undo();
        Assert.Equal("Action 2", historyService.UndoDescription);
        Assert.Equal("Action 3", historyService.RedoDescription);

        historyService.Undo();
        Assert.Equal("Action 1", historyService.UndoDescription);

        historyService.Redo();
        Assert.Equal("Action 2", historyService.UndoDescription);
    }
}
