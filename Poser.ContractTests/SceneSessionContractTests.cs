using System.Numerics;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.ContractTests;

public sealed class SceneSessionContractTests
{
    private static readonly Guid ActorLineage =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid CompanionLineage =
        Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid OtherActorLineage =
        Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    [Fact]
    public void Camera_admission_accepts_empty_and_virtual_camera_topologies()
    {
        var session = new SceneSession(new SelectionSession());

        Assert.Equal(
            SceneRefreshOutcome.Applied,
            session.TryRefresh(Scene(1)).Outcome);

        var defaultCamera = Camera(
            generation: 0,
            kind: CameraKind.Game,
            isLive: true,
            isDefault: true,
            logicalId: CameraLineage(1));
        Assert.Equal(
            SceneRefreshOutcome.Applied,
            session.TryRefresh(Scene(2, cameras: [defaultCamera])).Outcome);

        var parkedDefault = Camera(
            generation: 1,
            kind: CameraKind.Game,
            isLive: false,
            isDefault: true,
            logicalId: CameraLineage(1));
        var liveFree = Camera(
            generation: 0,
            kind: CameraKind.Free,
            isLive: true,
            isDefault: false,
            logicalId: CameraLineage(2));
        Assert.Equal(
            SceneRefreshOutcome.Applied,
            session.TryRefresh(Scene(
                3,
                cameras: [parkedDefault, liveFree])).Outcome);
    }

    [Fact]
    public void Camera_admission_rejects_zero_live_multiple_default_and_invalid_default_transactionally()
    {
        var selection = new SelectionSession();
        var session = new SceneSession(selection);
        var events = new List<SceneSnapshot>();
        session.SceneChanged += events.Add;
        var baseline = Scene(1);
        Assert.Equal(SceneRefreshOutcome.Applied, session.TryRefresh(baseline).Outcome);

        var invalidCandidates = new[]
        {
            Scene(
                2,
                cameras:
                [
                    Camera(
                        generation: 0,
                        kind: CameraKind.Game,
                        isLive: false,
                        isDefault: true,
                        logicalId: CameraLineage(3)),
                ]),
            Scene(
                3,
                cameras:
                [
                    Camera(
                        generation: 0,
                        kind: CameraKind.Game,
                        isLive: true,
                        isDefault: true,
                        logicalId: CameraLineage(4)),
                    Camera(
                        generation: 0,
                        kind: CameraKind.Game,
                        isLive: false,
                        isDefault: true,
                        logicalId: CameraLineage(5)),
                ]),
            Scene(
                4,
                cameras:
                [
                    Camera(
                        generation: 0,
                        kind: CameraKind.Free,
                        isLive: true,
                        isDefault: true,
                        logicalId: CameraLineage(6)),
                ]),
            Scene(
                5,
                cameras:
                [
                    Camera(
                        generation: 0,
                        kind: CameraKind.Game,
                        isLive: true,
                        isDefault: false,
                        logicalId: CameraLineage(7)),
                ]),
        };

        foreach (var candidate in invalidCandidates)
        {
            var result = session.TryRefresh(candidate);

            Assert.Equal(SceneRefreshOutcome.RejectedInvalidCandidate, result.Outcome);
            Assert.Same(baseline, session.Snapshot);
            Assert.Equal(1UL, session.Revision);
            Assert.Empty(selection.Selected);
            Assert.Single(events);
        }
    }

    [Fact]
    public void Malformed_relationships_and_environment_are_rejected_transactionally()
    {
        var actor = ActorIdFor(0);
        var baseline = Scene(1, actors: [Actor(actor)]);
        var selection = new SelectionSession();
        var session = new SceneSession(selection);
        var events = new List<SceneSnapshot>();
        session.SceneChanged += events.Add;
        Assert.Equal(SceneRefreshOutcome.Applied, session.TryRefresh(baseline).Outcome);
        var selected = SelectionId.ForActor(actor);
        selection.Select(selected);

        var skeleton = new SkeletonId(actor, PoseSlot.Character, 0);
        var first = new BoneId(skeleton, 0, 0, "first");
        var second = new BoneId(skeleton, 0, 1, "second");
        var missingParent = new BoneId(skeleton, 0, 99, "missing");
        var otherActor = ActorIdFor(1, OtherActorLineage);
        var otherSkeleton = new SkeletonId(otherActor, PoseSlot.Character, 0);
        var otherBone = new BoneId(otherSkeleton, 0, 0, "other");

        var invalidCandidates = new[]
        {
            Scene(
                2,
                actors:
                [
                    Actor(
                        actor,
                        new SkeletonDescriptor(
                            skeleton,
                            [
                                new BoneDescriptor(first, "first", null),
                                new BoneDescriptor(
                                    new BoneId(skeleton, 0, 0, "conflict"),
                                    "conflict",
                                    null),
                            ])),
                ]),
            Scene(
                3,
                actors:
                [
                    Actor(
                        actor,
                        new SkeletonDescriptor(
                            skeleton,
                            [new BoneDescriptor(first, "first", missingParent)])),
                ]),
            Scene(
                4,
                actors:
                [
                    Actor(
                        actor,
                        new SkeletonDescriptor(
                            skeleton,
                            [
                                new BoneDescriptor(first, "first", second),
                                new BoneDescriptor(second, "second", first),
                            ])),
                ]),
            Scene(
                5,
                actors:
                [
                    new ActorDescriptor(
                        ActorIdFor(2, CompanionLineage),
                        "Companion",
                        [],
                        IsCompanion: true,
                        OwnerActor: actor),
                ]),
            Scene(
                6,
                actors: [Actor(actor)],
                lights:
                [
                    new LightDescriptor(
                        LightIdFor(0),
                        "Attached",
                        LightKind.Point,
                        AttachedBone: first),
                ]),
            Scene(
                7,
                actors:
                [
                    Actor(
                        actor,
                        new SkeletonDescriptor(
                            skeleton,
                            [new BoneDescriptor(first, "first", null)])),
                    Actor(
                        otherActor,
                        new SkeletonDescriptor(
                            otherSkeleton,
                            [new BoneDescriptor(otherBone, "other", null)])),
                ],
                cameras:
                [
                    Camera(
                        generation: 0,
                        kind: CameraKind.Game,
                        isLive: true,
                        isDefault: true,
                        targetActor: actor,
                        targetBone: otherBone,
                        logicalId: CameraLineage(8)),
                ]),
            Scene(
                8,
                actors: [Actor(actor)],
                gaze:
                [new GazeDescriptor(
                    ActorIdFor(3, OtherActorLineage),
                    GazeMode.Position,
                    GazeParts.All)]),
            Scene(
                9,
                actors: [Actor(actor)],
                environment: new EnvironmentDescriptor(
                    MinuteOfDay: 1440,
                    DayOfMonth: 1,
                    WeatherId: 0)),
            Scene(
                10,
                actors: [Actor(actor)],
                environment: new EnvironmentDescriptor(
                    MinuteOfDay: 1,
                    DayOfMonth: 1,
                    WeatherId: 0,
                    HeldSections: (EnvironmentSection)256)),
            Scene(
                11,
                actors:
                [
                    Actor(actor),
                    Actor(ActorIdFor(1)),
                ]),
            Scene(
                12,
                actors:
                [
                    Actor(
                        actor,
                        new SkeletonDescriptor(
                            new SkeletonId(otherActor, PoseSlot.Character, 0),
                            [])),
                ]),
            Scene(
                13,
                actors:
                [new ActorDescriptor(
                    actor,
                    "Actor",
                    [],
                    OwnerActor: actor)]),
            Scene(
                14,
                actors:
                [new ActorDescriptor(
                    ActorIdFor(2, CompanionLineage),
                    "Companion",
                    [],
                    IsCompanion: true,
                    OwnerActor: ActorIdFor(2, CompanionLineage))]),
            Scene(
                15,
                actors: [Actor(actor)],
                gaze:
                [new GazeDescriptor(
                    actor,
                    GazeMode.Actor,
                    GazeParts.All,
                    TargetActor: actor)]),
            Scene(
                16,
                actors: [Actor(actor)],
                gaze:
                [new GazeDescriptor(
                    actor,
                    GazeMode.Position,
                    GazeParts.Eyes,
                    LockedParts: GazeParts.Head)]),
        };

        foreach (var candidate in invalidCandidates)
        {
            var result = session.TryRefresh(candidate);

            Assert.Equal(SceneRefreshOutcome.RejectedInvalidCandidate, result.Outcome);
            Assert.Same(baseline, session.Snapshot);
            Assert.Equal(1UL, session.Revision);
            Assert.Equal(selected, selection.Primary);
            Assert.Single(events);
            Assert.True(session.Contains(TransformTargetId.ForActor(actor)));
        }
    }

    [Fact]
    public void Equal_structural_replay_is_no_change_but_equal_revision_content_change_applies()
    {
        var session = new SceneSession(new SelectionSession());
        var events = new List<SceneSnapshot>();
        session.SceneChanged += events.Add;
        var first = Scene(5, actors: [Actor(ActorIdFor(0), name: "First")]);

        Assert.Equal(SceneRefreshOutcome.Applied, session.TryRefresh(first).Outcome);
        var replay = Scene(5, actors: [Actor(ActorIdFor(0), name: "First")]);
        var replayResult = session.TryRefresh(replay);

        Assert.Equal(SceneRefreshOutcome.NoChange, replayResult.Outcome);
        Assert.Same(first, session.Snapshot);
        Assert.Single(events);

        var changed = Scene(5, actors: [Actor(ActorIdFor(0), name: "Changed")]);
        var changedResult = session.TryRefresh(changed);

        Assert.Equal(SceneRefreshOutcome.Applied, changedResult.Outcome);
        Assert.Same(changed, session.Snapshot);
        Assert.Equal("Changed", session.Snapshot.Actors[0].Name);
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void Generation_floors_survive_removal_reappearance_and_older_revision_rejection()
    {
        var session = new SceneSession(new SelectionSession());
        var actor = ActorIdFor(1);
        Assert.Equal(
            SceneRefreshOutcome.Applied,
            session.TryRefresh(Scene(1, actors: [Actor(actor)])).Outcome);
        Assert.Equal(
            SceneRefreshOutcome.Applied,
            session.TryRefresh(Scene(2)).Outcome);

        var reappeared = Scene(3, actors: [Actor(actor)]);
        Assert.Equal(SceneRefreshOutcome.Applied, session.TryRefresh(reappeared).Outcome);

        var staleGeneration = Scene(4, actors: [Actor(ActorIdFor(0))]);
        var staleResult = session.TryRefresh(staleGeneration);
        Assert.Equal(SceneRefreshOutcome.RejectedInvalidCandidate, staleResult.Outcome);
        Assert.Same(reappeared, session.Snapshot);

        var olderRevision = Scene(2, actors: [Actor(actor)]);
        var olderResult = session.TryRefresh(olderRevision);
        Assert.Equal(SceneRefreshOutcome.RejectedOlderRevision, olderResult.Outcome);
        Assert.Same(reappeared, session.Snapshot);
    }

    [Fact]
    public void Generation_floors_are_independent_by_slot_and_object()
    {
        var actor = ActorIdFor(0);
        var character = new SkeletonId(actor, PoseSlot.Character, 2);
        var weapon = new SkeletonId(actor, PoseSlot.MainHand, 5);
        var session = new SceneSession(new SelectionSession());
        var baseline = Scene(
            1,
            actors: [Actor(actor, new SkeletonDescriptor(character, []), new SkeletonDescriptor(weapon, []))],
            lights: [new LightDescriptor(LightIdFor(3), "Light", LightKind.Point)],
            cameras:
            [
                Camera(
                    generation: 4,
                    kind: CameraKind.Game,
                    isLive: true,
                    isDefault: true,
                    logicalId: CameraLineage(9)),
            ],
            props: [new PropDescriptor(PropIdFor(6), "Prop")]);
        Assert.Equal(SceneRefreshOutcome.Applied, session.TryRefresh(baseline).Outcome);
        Assert.Equal(SceneRefreshOutcome.Applied, session.TryRefresh(Scene(2)).Outcome);

        var upgraded = Scene(
            3,
            actors:
            [Actor(
                actor,
                new SkeletonDescriptor(
                    new SkeletonId(actor, PoseSlot.Character, 2),
                    []),
                new SkeletonDescriptor(
                    new SkeletonId(actor, PoseSlot.MainHand, 6),
                    []))],
            lights: [new LightDescriptor(LightIdFor(4), "Light", LightKind.Point)],
            cameras:
            [
                Camera(
                    generation: 5,
                    kind: CameraKind.Game,
                    isLive: true,
                    isDefault: true,
                    logicalId: CameraLineage(9)),
            ],
            props: [new PropDescriptor(PropIdFor(7), "Prop")]);
        Assert.Equal(SceneRefreshOutcome.Applied, session.TryRefresh(upgraded).Outcome);

        var staleSlot = upgraded with
        {
            Revision = 4,
            Actors =
            [Actor(
                actor,
                new SkeletonDescriptor(
                    new SkeletonId(actor, PoseSlot.Character, 1),
                    []),
                new SkeletonDescriptor(
                    new SkeletonId(actor, PoseSlot.MainHand, 6),
                    []))],
        };
        Assert.Equal(
            SceneRefreshOutcome.RejectedInvalidCandidate,
            session.TryRefresh(staleSlot).Outcome);
        Assert.Same(upgraded, session.Snapshot);

        var staleObject = upgraded with
        {
            Revision = 5,
            Lights = [new LightDescriptor(LightIdFor(3), "Light", LightKind.Point)],
        };
        Assert.Equal(
            SceneRefreshOutcome.RejectedInvalidCandidate,
            session.TryRefresh(staleObject).Outcome);
        Assert.Same(upgraded, session.Snapshot);
    }

    [Fact]
    public void Gaze_target_resolution_requires_position_mode_and_a_valid_parts_mask()
    {
        var actor = ActorIdFor(0);
        var selection = new SelectionSession();
        var session = new SceneSession(selection);
        var anchor = SelectionId.ForGazeTarget(actor);

        Assert.Equal(SceneRefreshOutcome.Applied, session.TryRefresh(Scene(
            1,
            actors: [Actor(actor)])).Outcome);
        Assert.Null(session.Resolve(anchor));

        var position = Scene(
            2,
            actors: [Actor(actor)],
            gaze:
            [new GazeDescriptor(
                actor,
                GazeMode.Position,
                GazeParts.Eyes | GazeParts.Head)]);
        Assert.Equal(SceneRefreshOutcome.Applied, session.TryRefresh(position).Outcome);
        Assert.Equal(anchor, session.Resolve(anchor));
        Assert.Equal(
            SelectionId.ForGazeTarget(actor, GazePart.Eyes),
            session.Resolve(SelectionId.ForGazeTarget(actor, GazePart.Eyes)));
        Assert.Equal(
            SelectionId.ForGazeTarget(actor, GazePart.Head),
            session.Resolve(SelectionId.ForGazeTarget(actor, GazePart.Head)));
        Assert.Null(session.Resolve(SelectionId.ForGazeTarget(actor, GazePart.Body)));
        Assert.Null(session.Resolve(
            SelectionId.ForGazeTarget(actor, (GazePart)99)));

        var forward = Scene(
            3,
            actors: [Actor(actor)],
            gaze:
            [new GazeDescriptor(
                actor,
                GazeMode.Forward,
                GazeParts.All)]);
        Assert.Equal(SceneRefreshOutcome.Applied, session.TryRefresh(forward).Outcome);
        Assert.Null(session.Resolve(anchor));
    }

    [Fact]
    public void Gaze_target_selection_repairs_only_to_a_current_exact_actor_with_position_state()
    {
        var oldActor = ActorIdFor(0);
        var newActor = ActorIdFor(1);
        var selection = new SelectionSession();
        var session = new SceneSession(selection);
        var oldTransformTarget = TransformTargetId.ForActor(oldActor);
        var newTransformTarget = TransformTargetId.ForActor(newActor);
        var oldGaze = SelectionId.ForGazeTarget(oldActor, GazePart.Eyes);

        Assert.Equal(
            SceneRefreshOutcome.Applied,
            session.TryRefresh(Scene(
                1,
                actors: [Actor(oldActor)],
                gaze: [new GazeDescriptor(oldActor, GazeMode.Position, GazeParts.Eyes)])).Outcome);
        selection.Select(oldGaze);
        Assert.True(session.Contains(oldTransformTarget));

        Assert.Equal(
            SceneRefreshOutcome.Applied,
            session.TryRefresh(Scene(
                2,
                actors: [Actor(newActor)],
                gaze: [new GazeDescriptor(newActor, GazeMode.Position, GazeParts.Eyes)])).Outcome);
        Assert.Equal(
            SelectionId.ForGazeTarget(newActor, GazePart.Eyes),
            selection.Primary);
        Assert.False(session.Contains(oldTransformTarget));
        Assert.True(session.Contains(newTransformTarget));

        Assert.Equal(
            SceneRefreshOutcome.Applied,
            session.TryRefresh(Scene(3, actors: [Actor(newActor)])).Outcome);
        Assert.Null(selection.Primary);
        Assert.Null(session.Resolve(oldGaze));
    }

    [Fact]
    public void Reentrant_refresh_is_rejected_and_observer_failures_are_isolated()
    {
        var session = new SceneSession(new SelectionSession());
        var first = Scene(1, actors: [Actor(ActorIdFor(0))]);
        Assert.Equal(SceneRefreshOutcome.Applied, session.TryRefresh(first).Outcome);

        SceneRefreshResult? nested = null;
        var secondObserverCalls = 0;
        Action<SceneSnapshot> throwingObserver = _ =>
        {
            nested = session.TryRefresh(Scene(3, actors: [Actor(ActorIdFor(0))]));
            throw new InvalidOperationException("observer failure");
        };
        session.SceneChanged += throwingObserver;
        session.SceneChanged += _ => secondObserverCalls++;

        var result = session.TryRefresh(Scene(2, actors: [Actor(ActorIdFor(0))]));

        Assert.Equal(SceneRefreshOutcome.AppliedWithNotificationFailures, result.Outcome);
        Assert.Single(result.NotificationFailures);
        Assert.NotNull(nested);
        Assert.Equal(SceneRefreshOutcome.RejectedReentrant, nested!.Outcome);
        Assert.Equal(1, secondObserverCalls);
        Assert.Equal(2UL, session.Revision);

        session.SceneChanged -= throwingObserver;
        Assert.Equal(
            SceneRefreshOutcome.Applied,
            session.TryRefresh(Scene(3, actors: [Actor(ActorIdFor(0))])).Outcome);
        Assert.Equal(3UL, session.Revision);
    }

    private static SceneSnapshot Scene(
        ulong revision,
        IReadOnlyList<ActorDescriptor>? actors = null,
        IReadOnlyList<LightDescriptor>? lights = null,
        IReadOnlyList<CameraDescriptor>? cameras = null,
        IReadOnlyList<PropDescriptor>? props = null,
        EnvironmentDescriptor? environment = null,
        IReadOnlyList<GazeDescriptor>? gaze = null) =>
        new(
            revision,
            actors ?? Array.Empty<ActorDescriptor>(),
            lights ?? Array.Empty<LightDescriptor>(),
            cameras ?? Array.Empty<CameraDescriptor>(),
            props ?? Array.Empty<PropDescriptor>(),
            environment,
            gaze ?? Array.Empty<GazeDescriptor>());

    private static ActorDescriptor Actor(
        ActorId id,
        params SkeletonDescriptor[] skeletons) =>
        Actor(id, "Actor", skeletons);

    private static ActorDescriptor Actor(
        ActorId id,
        string name,
        params SkeletonDescriptor[] skeletons) =>
        new(id, name, skeletons);

    private static CameraDescriptor Camera(
        uint generation,
        CameraKind kind,
        bool isLive,
        bool isDefault,
        Guid logicalId,
        ActorId? targetActor = null,
        BoneId? targetBone = null) =>
        new(
            new CameraId(logicalId, generation),
            "Camera",
            kind,
            isLive,
            isDefault,
            TargetActor: targetActor,
            TargetBone: targetBone);

    private static ActorId ActorIdFor(
        uint generation,
        Guid? logicalId = null) =>
        new(logicalId ?? ActorLineage, generation);

    private static Guid CameraLineage(int index) =>
        Guid.Parse($"{index + 1:00000000}-dddd-dddd-dddd-dddddddddddd");

    private static LightId LightIdFor(uint generation) =>
        new(Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"), generation);

    private static PropId PropIdFor(uint generation) =>
        new(Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"), generation);
}
