using System.Text.Json;
using Poser.Application.Diagnostics;
using Poser.Application.Transforms;

namespace Poser.Application.Tests.Diagnostics;

public sealed class DiagnosticValueTests
{
    // A live object whose graph shares nodes, like a camera whose target
    // actor reaches the same bones through several parents (#491).
    private sealed class LiveNode
    {
        public List<LiveNode> Children { get; } = new();
        public string Name { get; init; } = "";
    }

    private sealed record Plain(string Label, int Count, Plain? Next, IReadOnlyList<int> Values, LiveNode? Live);

    private static LiveNode SharedGraph(int layers)
    {
        var shared = new LiveNode { Name = "leaf" };
        for (int i = 0; i < layers; i++)
        {
            var parent = new LiveNode { Name = $"layer {i}" };
            parent.Children.Add(shared);
            parent.Children.Add(shared);
            shared = parent;
        }
        return shared;
    }

    [Fact]
    public void A_recorded_live_object_is_named_not_walked()
    {
        var history = new EditHistory();
        var journal = new ValueJournal(history);
        using var recorder = new ActionRecorder(history);
        LiveNode? current = null;
        var live = SharedGraph(40);

        journal.Record("cameras", "Switch camera", current, live,
            ValueWrites.Unchecked<LiveNode?>(next => current = next));

        var record = Assert.Single(recorder.Snapshot());
        Assert.Null(record.Before);
        Assert.Equal("<LiveNode>", record.After);
        Assert.True(JsonSerializer.Serialize(recorder.Snapshot()).Length < 2_000);
    }

    [Fact]
    public void Plain_data_is_copied_but_stays_bounded()
    {
        Plain? chain = null;
        for (int i = 0; i < 100; i++)
            chain = new Plain($"node {i}", i, chain, Enumerable.Range(0, 1000).ToArray(), SharedGraph(3));

        var copy = DiagnosticValue.From(chain, text => text.Replace("node", "n"));

        var top = Assert.IsType<SortedDictionary<string, object?>>(copy);
        Assert.Equal("n 99", top["Label"]);
        Assert.Equal(99, top["Count"]);
        Assert.Equal("<LiveNode>", top["Live"]);
        var values = Assert.IsType<List<object?>>(top["Values"]);
        Assert.Equal(DiagnosticValue.MaxItems + 1, values.Count);
        Assert.True(JsonSerializer.Serialize(copy).Length < 20_000);
    }
}
