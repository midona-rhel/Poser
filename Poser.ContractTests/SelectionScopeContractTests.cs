using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.ContractTests;

public sealed class SelectionScopeContractTests
{
    [Fact]
    public void Explicit_scopes_are_independent_without_ambient_redirection()
    {
        var actor = Actor(0);
        var first = SelectionId.ForBone(Bone(actor, 1, "root"));
        var second = SelectionId.ForBone(Bone(actor, 2, "spine"));
        var third = SelectionId.ForBone(Bone(actor, 3, "arm"));
        var live = new SelectionSession();
        var firstScope = new SelectionScope(first);
        var secondScope = new SelectionScope(second);

        live.Live.Select(SelectionId.ForActor(actor));
        firstScope.SelectRange(first, third, new[] { first, second, third });
        firstScope.Promote(second);
        secondScope.Add(first);

        Assert.Equal(
            new[] { SelectionId.ForActor(actor) },
            live.Live.Selected);
        Assert.Equal(new[] { second, first, third }, firstScope.Selected);
        Assert.Equal(second, firstScope.Primary);
        Assert.Equal(second, firstScope.Anchor);
        Assert.Equal(new[] { second, first }, secondScope.Selected);
        Assert.Equal(second, secondScope.Primary);
        Assert.Equal(first, secondScope.Anchor);
    }

    [Fact]
    public void Compatibility_adapter_restores_nested_scopes_and_is_exception_safe()
    {
        var actor = Actor(0);
        var liveId = SelectionId.ForActor(actor);
        var outerId = SelectionId.ForBone(Bone(actor, 1, "outer"));
        var outerAdded = SelectionId.ForBone(Bone(actor, 2, "outer-added"));
        var innerId = SelectionId.ForBone(Bone(actor, 3, "inner"));
        var innerAdded = SelectionId.ForBone(Bone(actor, 4, "inner-added"));
        var session = new SelectionSession();
        var outer = new SelectionScope(outerId);
        var inner = new SelectionScope(innerId);
        var notificationCount = 0;

        session.SelectionChanged += _ => notificationCount++;
        session.Live.Select(liveId);

        using (session.BeginScope(outer))
        {
            session.Add(outerAdded);

            try
            {
                using (session.BeginScope(inner))
                {
                    session.Add(innerAdded);
                    throw new InvalidOperationException("scope body failed");
                }
            }
            catch (InvalidOperationException)
            {
                // The using token must restore the outer adapter scope.
            }

            Assert.Equal(new[] { outerId, outerAdded }, session.Selected);
            Assert.Equal(outerId, session.Primary);
        }

        Assert.Equal(new[] { liveId }, session.Selected);
        Assert.Equal(liveId, session.Primary);
        Assert.Equal(new[] { innerId, innerAdded }, inner.Selected);
        Assert.Equal(1, notificationCount);
    }

    [Fact]
    public void Live_notifications_remain_live_only_and_reconcile_all_retained_scopes()
    {
        var oldActor = Actor(0);
        var currentActor = Actor(1);
        var validOld = SelectionId.ForBone(Bone(oldActor, 1, "root"));
        var missingOld = SelectionId.ForBone(Bone(oldActor, 9, "missing"));
        var missingOldAgain = SelectionId.ForBone(Bone(oldActor, 10, "also-missing"));
        var validCurrent = SelectionId.ForBone(Bone(currentActor, 1, "root"));
        var currentActorSelection = SelectionId.ForActor(currentActor);
        var session = new SelectionSession();
        var scene = new SceneSession(session);
        var notifications = new List<IReadOnlyList<SelectionId>>();
        var firstScope = new SelectionScope(validOld);
        var secondScope = new SelectionScope(missingOld);

        session.SelectionChanged += ids => notifications.Add(ids.ToArray());
        session.Live.Select(validOld);
        session.Live.Add(missingOld);
        firstScope.Add(missingOldAgain);
        secondScope.Add(missingOldAgain);
        session.TrackScope(firstScope);
        session.TrackScope(secondScope);

        scene.Refresh(SceneWithBone(currentActor, validCurrent.Bone!.Value));

        Assert.Equal(3, notifications.Count);
        Assert.Equal(new[] { validOld }, notifications[0]);
        Assert.Equal(new[] { validOld, missingOld }, notifications[1]);
        Assert.Equal(new[] { validCurrent }, notifications[2]);

        Assert.Equal(new[] { validCurrent }, session.Live.Selected);
        Assert.Equal(validCurrent, session.Live.Primary);
        Assert.Equal(validCurrent, session.Live.Anchor);

        Assert.Equal(new[] { validCurrent }, firstScope.Selected);
        Assert.Equal(validCurrent, firstScope.Primary);
        Assert.Equal(validCurrent, firstScope.Anchor);

        Assert.Equal(new[] { currentActorSelection }, secondScope.Selected);
        Assert.Equal(currentActorSelection, secondScope.Primary);
        Assert.Equal(currentActorSelection, secondScope.Anchor);
    }

    [Fact]
    public void Scope_mutation_rules_preserve_order_primary_and_homogeneous_ranges()
    {
        var actor = Actor(0);
        var otherActor = Actor(1, "22222222-2222-2222-2222-222222222222");
        var first = SelectionId.ForBone(Bone(actor, 1, "root"));
        var second = SelectionId.ForBone(Bone(actor, 2, "spine"));
        var third = SelectionId.ForBone(Bone(actor, 3, "arm"));
        var incompatible = SelectionId.ForBone(Bone(otherActor, 4, "other"));
        var scope = new SelectionScope(first);

        scope.SelectRange(first, third, new[] { first, second, third });
        scope.Add(incompatible);

        Assert.Equal(new[] { incompatible }, scope.Selected);
        Assert.Equal(incompatible, scope.Primary);
        Assert.Equal(incompatible, scope.Anchor);

        scope.SelectRange(incompatible, third, new[] { incompatible, second, third });

        Assert.Equal(new[] { third }, scope.Selected);
        Assert.Equal(third, scope.Primary);
        Assert.Equal(third, scope.Anchor);
    }

    private static ActorId Actor(uint generation, string lineage = "11111111-1111-1111-1111-111111111111") =>
        new(Guid.Parse(lineage), generation);

    private static BoneId Bone(ActorId actor, int index, string name) =>
        new(
            new SkeletonId(actor, PoseSlot.Character, 0),
            PartialId: 0,
            BoneIndex: index,
            CanonicalName: name);

    private static SceneSnapshot SceneWithBone(ActorId actor, BoneId bone) =>
        new(
            Revision: actor.Generation + 1,
            Actors: new[]
            {
                new ActorDescriptor(
                    actor,
                    "Test actor",
                    new[]
                    {
                        new SkeletonDescriptor(
                            bone.Skeleton,
                            new[] { new BoneDescriptor(bone, bone.CanonicalName, Parent: null) }),
                    }),
            },
            Lights: Array.Empty<LightDescriptor>(),
            Cameras: Array.Empty<CameraDescriptor>(),
            Props: Array.Empty<PropDescriptor>());
}
