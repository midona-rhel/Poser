using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Application.Tests.Selection;

public sealed class SelectionEntityCommandsTests
{
    [Fact]
    public void Current_read_is_exact_and_reports_ownership_capabilities()
    {
        var actor = ActorId.New();
        var spawnedLight = LightId.New();
        var borrowedLight = LightId.New();
        var camera = CameraId.New();
        var defaultCamera = CameraId.New();
        var world = WorldObjectId.New();
        var scene = new SceneSession(new SelectionSession());
        var admitted = scene.TryRefresh(new SceneSnapshot(
            1,
            [new ActorDescriptor(actor, "Actor", [], IsAdopted: true)],
            [
                new LightDescriptor(spawnedLight, "Spawned", LightKind.Point),
                new LightDescriptor(borrowedLight, "Borrowed", LightKind.Point,
                    Ownership: LightOwnership.World),
            ],
            [
                new CameraDescriptor(camera, "Camera", CameraKind.Free),
                new CameraDescriptor(defaultCamera, "Default", CameraKind.Game,
                    IsLive: true, IsDefault: true),
            ],
            [],
            WorldObjects: [new WorldObjectDescriptor(world, "Scenery", "map/path") ]));
        Assert.True(admitted.Accepted, admitted.Detail);

        Assert.Equal(SelectionRemoval.Release,
            scene.ReadCurrent(SelectionId.ForActor(actor))?.Removal);
        Assert.Equal(SelectionRemoval.Destroy,
            scene.ReadCurrent(SelectionId.ForLight(spawnedLight))?.Removal);
        Assert.Equal(SelectionRemoval.Release,
            scene.ReadCurrent(SelectionId.ForLight(borrowedLight))?.Removal);
        Assert.Equal(SelectionRemoval.None,
            scene.ReadCurrent(SelectionId.ForCamera(defaultCamera))?.Removal);
        Assert.Equal(SelectionRemoval.Destroy,
            scene.ReadCurrent(SelectionId.ForCamera(camera))?.Removal);
        Assert.Equal(SelectionRemoval.Release,
            scene.ReadCurrent(SelectionId.ForWorldObject(world))?.Removal);
        Assert.Null(scene.ReadCurrent(
            SelectionId.ForActor(actor.NextGeneration())));
    }

    [Fact]
    public async Task Commands_deduplicate_ids_and_skip_unsupported_verbs()
    {
        var id = SelectionId.ForProp(PropId.New());
        var reads = new UnsupportedReads(id);
        var port = new RecordingPort();
        var commands = new SelectionEntityCommands(reads, port);

        Assert.Equal(0, commands.SetVisibility([id, id], visible: false));
        Assert.Equal(0, await commands.Remove([id, id]));
        Assert.Equal(2, reads.Count);
        Assert.Empty(port.Calls);
    }

    [Fact]
    public async Task Commands_dispatch_only_current_supported_verbs_once_per_id()
    {
        var id = SelectionId.ForProp(PropId.New());
        var reads = new StableReads(new CurrentSelectionEntity(
            id, CanChangeVisibility: true, IsVisible: true,
            SelectionRemoval.Destroy));
        var port = new RecordingPort();
        var commands = new SelectionEntityCommands(reads, port);

        Assert.Equal(1, commands.SetVisibility([id, id], visible: false));
        Assert.Equal(1, await commands.Remove([id, id]));
        Assert.Equal(2, port.Calls.Count);
        Assert.Equal((id, false), port.Calls[0]);
        Assert.Equal((id, SelectionRemoval.Destroy), port.Calls[1]);
    }

    [Fact]
    public async Task Removal_refusal_is_not_reported_as_applied()
    {
        var id = SelectionId.ForProp(PropId.New());
        var reads = new StableReads(new CurrentSelectionEntity(
            id, CanChangeVisibility: true, IsVisible: true,
            SelectionRemoval.Destroy));
        var port = new RecordingPort { RemovalResult = false };
        var commands = new SelectionEntityCommands(reads, port);

        Assert.Equal(0, await commands.Remove([id]));
        Assert.Single(port.Calls);
    }

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
        Assert.Equal(0, await commands.Remove([id]));
        Assert.Empty(port.Calls);
        Assert.True(port.Visibility);
    }

    [Fact]
    public void Rapid_visibility_toggles_read_live_state_when_snapshot_is_stale()
    {
        var id = SelectionId.ForProp(PropId.New());
        var reads = new StableReads(new CurrentSelectionEntity(
            id, CanChangeVisibility: true, IsVisible: true,
            SelectionRemoval.Destroy));
        var port = new RecordingPort { Visibility = false };
        var commands = new SelectionEntityCommands(reads, port);

        Assert.False(commands.ReadVisibility(id));
        Assert.Equal(1, commands.SetVisibility(
            [id], !commands.ReadVisibility(id)!.Value));
        Assert.True(commands.ReadVisibility(id));
        Assert.Equal(1, commands.SetVisibility(
            [id], !commands.ReadVisibility(id)!.Value));
        Assert.False(commands.ReadVisibility(id));
        Assert.Equal((id, true), port.Calls[0]);
        Assert.Equal((id, false), port.Calls[1]);
    }

    private sealed class SubstitutingReads(CurrentSelectionEntity value)
        : ICurrentSelectionEntityReads
    {
        public CurrentSelectionEntity? ReadCurrent(SelectionId id) => value;
    }

    private sealed class StableReads(CurrentSelectionEntity value)
        : ICurrentSelectionEntityReads
    {
        public CurrentSelectionEntity? ReadCurrent(SelectionId id) =>
            id == value.Id ? value : null;
    }

    private sealed class UnsupportedReads(SelectionId id)
        : ICurrentSelectionEntityReads
    {
        public int Count { get; private set; }

        public CurrentSelectionEntity? ReadCurrent(SelectionId requested)
        {
            Count++;
            return requested == id
                ? new(id, false, true, SelectionRemoval.None)
                : null;
        }
    }

    private sealed class RecordingPort : ISelectionEntityCommandPort
    {
        public List<(SelectionId Id, object Command)> Calls { get; } = [];
        public bool RemovalResult { get; init; } = true;
        public bool? Visibility { get; set; } = true;

        public bool? ReadVisibility(SelectionId id) => Visibility;

        public bool SetVisibility(SelectionId id, bool visible)
        {
            Calls.Add((id, visible));
            Visibility = visible;
            return true;
        }

        public Task<bool> Remove(SelectionId id, SelectionRemoval removal)
        {
            Calls.Add((id, removal));
            return Task.FromResult(RemovalResult);
        }
    }
}
