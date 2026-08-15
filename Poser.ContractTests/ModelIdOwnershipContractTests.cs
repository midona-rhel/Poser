using Poser.Application.Appearance;
using Poser.Application.Presentation;
using Poser.Domain.Appearance;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.ContractTests;

/// <summary>
/// The Model ID ownership contract (execution brief §6.5), stated at the
/// service boundary: the incoming model id is captured ONCE per exact
/// actor generation before the first successful write (the vendor-baseline
/// idiom — Brio ActorAppearanceCapability.cs:326, Poser
/// ActorPresentationSession); Apply and Reset restore exactly; a replaced
/// or vanished exact generation is dropped without writes; and the search
/// catalog filters by name, kind, and the ModelChara id itself.
/// </summary>
public sealed class ModelIdOwnershipContractTests
{
    // ── capture-once ─────────────────────────────────────────────────────

    [Fact]
    public void First_successful_apply_captures_the_incoming_id_once()
    {
        var port = new FakePort { Current = 0 };
        var session = new ActorModelIdSession(port);
        var actor = ActorId.New();

        Assert.True(session.Apply(actor, 878).Success);
        Assert.True(session.Apply(actor, 1234).Success);

        Assert.True(session.IsOwned(actor));
        Assert.Equal(0, session.CaptureFor(actor));
        Assert.Equal(new[] { 878, 1234 }, port.Writes);
    }

    [Fact]
    public void Failed_apply_owns_nothing_and_the_next_apply_captures_fresh()
    {
        var port = new FakePort { Current = 0, FailWrites = true };
        var session = new ActorModelIdSession(port);
        var actor = ActorId.New();

        Assert.False(session.Apply(actor, 878).Success);
        Assert.False(session.IsOwned(actor));
        Assert.Empty(port.Writes);

        // The actor's incoming id has legitimately moved on before the next
        // attempt; the capture is whatever is incoming THEN.
        port.FailWrites = false;
        port.Current = 5;
        Assert.True(session.Apply(actor, 7).Success);
        Assert.Equal(5, session.CaptureFor(actor));
    }

    [Fact]
    public void Negative_id_is_refused_before_the_port_is_touched()
    {
        var port = new FakePort { Current = 0 };
        var session = new ActorModelIdSession(port);

        Assert.False(session.Apply(ActorId.New(), -1).Success);
        Assert.Empty(port.Writes);
    }

    // ── exact restore ────────────────────────────────────────────────────

    [Fact]
    public void Reset_writes_exactly_the_capture_back_and_releases()
    {
        var port = new FakePort { Current = 3 };
        var session = new ActorModelIdSession(port);
        var actor = ActorId.New();

        Assert.True(session.Apply(actor, 878).Success);
        Assert.True(session.Reset(actor).Success);

        Assert.Equal(new[] { 878, 3 }, port.Writes);
        Assert.False(session.IsOwned(actor));

        // Released means released: another reset has nothing to write.
        Assert.True(session.Reset(actor).Success);
        Assert.Equal(2, port.Writes.Count);
    }

    [Fact]
    public void Failed_restore_stays_owned_and_the_next_reset_retries()
    {
        var port = new FakePort { Current = 3 };
        var session = new ActorModelIdSession(port);
        var actor = ActorId.New();
        Assert.True(session.Apply(actor, 878).Success);

        port.FailWrites = true;
        Assert.False(session.Reset(actor).Success);
        Assert.True(session.IsOwned(actor));

        port.FailWrites = false;
        Assert.True(session.Reset(actor).Success);
        Assert.Equal(3, port.Writes[^1]);
        Assert.False(session.IsOwned(actor));
    }

    // ── replacement refusal ──────────────────────────────────────────────

    [Fact]
    public void Reset_after_replacement_drops_the_capture_without_writing()
    {
        var port = new FakePort { Current = 0 };
        var session = new ActorModelIdSession(port);
        var actor = ActorId.New();
        Assert.True(session.Apply(actor, 878).Success);

        // The exact generation no longer resolves: the actor was removed or
        // replaced, and the replacement must never receive the old capture.
        port.Current = null;

        Assert.True(session.Reset(actor).Success);
        Assert.False(session.IsOwned(actor));
        Assert.Equal(new[] { 878 }, port.Writes);
    }

    [Fact]
    public void Reconcile_restores_a_departed_resolvable_actor_and_keeps_present_ones()
    {
        var port = new FakePort { Current = 0 };
        var session = new ActorModelIdSession(port);
        var departed = ActorId.New();
        var present = ActorId.New();
        Assert.True(session.Apply(departed, 878).Success);
        Assert.True(session.Apply(present, 55).Success);

        session.Reconcile(SnapshotWith(present));

        Assert.False(session.IsOwned(departed));
        Assert.True(session.IsOwned(present));
        // The departed-but-resolvable actor was restored through the port.
        Assert.Contains(878, port.Writes);
    }

    [Fact]
    public void Reconcile_drops_an_unresolvable_departed_actor_without_writes()
    {
        var port = new FakePort { Current = 0 };
        var session = new ActorModelIdSession(port);
        var gone = ActorId.New();
        Assert.True(session.Apply(gone, 878).Success);
        port.Current = null;

        session.Reconcile(SnapshotWith());

        Assert.False(session.IsOwned(gone));
        Assert.Equal(new[] { 878 }, port.Writes);
    }

    // ── search filtering ─────────────────────────────────────────────────

    [Fact]
    public void Search_matches_names_case_insensitively_within_the_kind_filter()
    {
        var catalog = LoadedCatalog();

        var byName = catalog.Search("ruby");
        Assert.Single(byName);
        Assert.Equal("Ruby Carbuncle", byName[0].Name);

        var mountsOnly = catalog.Search("c", ModelCatalogKind.Mount);
        Assert.All(mountsOnly, entry => Assert.Equal(
            ModelCatalogKind.Mount, entry.Kind));
    }

    [Fact]
    public void Search_finds_rows_by_their_model_chara_id()
    {
        var catalog = LoadedCatalog();

        var byId = catalog.Search("878");
        Assert.Single(byId);
        Assert.Equal(878, byId[0].ModelCharaId);
    }

    [Fact]
    public void Search_is_bounded_by_the_limit()
    {
        var catalog = LoadedCatalog();
        Assert.Equal(2, catalog.Search("", limit: 2).Count);
    }

    [Fact]
    public void Unloaded_catalog_answers_empty_not_null()
    {
        var catalog = new ModelCatalog();
        Assert.False(catalog.IsLoaded);
        Assert.Empty(catalog.Search("anything"));
    }

    // ── fixtures ─────────────────────────────────────────────────────────

    private static ModelCatalog LoadedCatalog()
    {
        var catalog = new ModelCatalog();
        catalog.Publish(new[]
        {
            new ModelCatalogEntry(ModelCatalogKind.EventNpc, 1000001, "Alphinaud", 0, 1494),
            new ModelCatalogEntry(ModelCatalogKind.Minion, 17, "Ruby Carbuncle", 4401, 878),
            new ModelCatalogEntry(ModelCatalogKind.Mount, 1, "Company Chocobo", 4001, 1),
            new ModelCatalogEntry(ModelCatalogKind.Ornament, 2, "Chocobo Umbrella", 4002, 2325),
        });
        return catalog;
    }

    private static SceneSnapshot SnapshotWith(params ActorId[] actors) => new(
        Revision: 1,
        Actors: actors
            .Select(id => new ActorDescriptor(
                id, "Actor", Array.Empty<SkeletonDescriptor>()))
            .ToList(),
        Lights: Array.Empty<LightDescriptor>(),
        Cameras: Array.Empty<CameraDescriptor>(),
        Props: Array.Empty<PropDescriptor>());

    private sealed class FakePort : IModelIdRuntimePort
    {
        public int? Current;
        public bool FailWrites;
        public readonly List<int> Writes = new();

        public int? Read(ActorId actor) => Current;

        public PresentationPortResult Write(ActorId actor, int modelCharaId)
        {
            if (Current == null)
                return PresentationPortResult.Fail("The actor is no longer available.");
            if (FailWrites)
                return PresentationPortResult.Fail("The write was refused.");
            Writes.Add(modelCharaId);
            Current = modelCharaId;
            return PresentationPortResult.Ok();
        }
    }
}
