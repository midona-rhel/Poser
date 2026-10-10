using System.Numerics;
using Poser.Domain.Collections;
using Poser.Domain.Companions;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;
using Poser.Domain.Scene;

namespace Poser.Domain.Tests;

public sealed class SceneSnapshotEqualityTests
{
    private static readonly ActorId ActorA = new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), 0);
    private static readonly ActorId ActorB = new(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), 0);
    private static readonly SkeletonId Skeleton = new(ActorA, PoseSlot.Character, 0);
    private static readonly BoneId Root = new(Skeleton, 0, 0, "n_root");
    private static readonly BoneId Hand = new(Skeleton, 0, 5, "j_hand_l");
    private static readonly LightId Light = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), 0);
    private static readonly CameraId Camera = new(Guid.Parse("22222222-2222-2222-2222-222222222222"), 0);
    private static readonly PropId Prop = new(Guid.Parse("33333333-3333-3333-3333-333333333333"), 0);
    private static readonly WorldObjectId WorldObject = new(Guid.Parse("44444444-4444-4444-4444-444444444444"), 0);
    private static readonly OverlayId Overlay = new(Guid.Parse("55555555-5555-5555-5555-555555555555"), 0);

    // Built fresh on every call, so two calls share no list instance.
    private static SceneSnapshot Baseline() => new(
        7,
        [new ActorDescriptor(
            ActorA,
            "Actor",
            [new SkeletonDescriptor(
                Skeleton,
                [
                    new BoneDescriptor(Root, "Root", null),
                    new BoneDescriptor(Hand, "Hand", Root),
                ])],
            OwnerActor: ActorB,
            AttachmentKind: CompanionKind.Companion)],
        [new LightDescriptor(Light, "Light", LightKind.Point)],
        [new CameraDescriptor(Camera, "Camera", CameraKind.Free)],
        [new PropDescriptor(Prop, "Prop")],
        new EnvironmentDescriptor(600, 12, 2),
        [new GazeDescriptor(ActorA)],
        [new OverlayDescriptor(Overlay, "Overlay", OverlayNodeKind.Talk)],
        [new WorldObjectDescriptor(WorldObject, "Tree", "bg/tree.sgb")]);

    private static SceneSnapshot Actor(SceneSnapshot s, Func<ActorDescriptor, ActorDescriptor> edit) =>
        s with { Actors = [edit(s.Actors[0])] };

    private static SceneSnapshot Skel(SceneSnapshot s, Func<SkeletonDescriptor, SkeletonDescriptor> edit) =>
        Actor(s, a => a with { Skeletons = [edit(a.Skeletons[0])] });

    private static SceneSnapshot Bone(SceneSnapshot s, Func<BoneDescriptor, BoneDescriptor> edit) =>
        Skel(s, k => k with { Bones = [k.Bones[0], edit(k.Bones[1])] });

    private static SceneSnapshot Gaze(SceneSnapshot s, Func<GazeDescriptor, GazeDescriptor> edit) =>
        s with { GazeStates = [edit(s.GazeStates[0])] };

    private static SceneSnapshot Cam(SceneSnapshot s, Func<CameraDescriptor, CameraDescriptor> edit) =>
        s with { Cameras = [edit(s.Cameras[0])] };

    private static SceneSnapshot World(SceneSnapshot s, Func<WorldObjectDescriptor, WorldObjectDescriptor> edit) =>
        s with { WorldObjects = [edit(s.WorldObjects[0])] };

    private static SceneSnapshot Env(SceneSnapshot s, Func<EnvironmentDescriptor, EnvironmentDescriptor> edit) =>
        s with { Environment = edit(s.Environment!) };

    /// <summary>One edit per descriptor field, keyed "Type.Field". The key set
    /// is checked against the records' constructors below, so a new field
    /// without an edit here fails the suite.</summary>
    private static readonly Dictionary<string, Func<SceneSnapshot, SceneSnapshot>> Edits = new()
    {
        ["BoneDescriptor.Id"] = s => Bone(s, b => b with { Id = b.Id with { CanonicalName = "j_hand_r" } }),
        ["BoneDescriptor.DisplayName"] = s => Bone(s, b => b with { DisplayName = "hand" }),
        ["BoneDescriptor.Parent"] = s => Bone(s, b => b with { Parent = null }),
        ["BoneDescriptor.IsHidden"] = s => Bone(s, b => b with { IsHidden = true }),

        ["SkeletonDescriptor.Id"] = s => Skel(s, k => k with { Id = k.Id.NextGeneration() }),
        ["SkeletonDescriptor.Bones"] = s => Skel(s, k => k with { Bones = [k.Bones[0]] }),

        ["ActorDescriptor.Id"] = s => Actor(s, a => a with { Id = a.Id.NextGeneration() }),
        ["ActorDescriptor.Name"] = s => Actor(s, a => a with { Name = "actor" }),
        ["ActorDescriptor.Skeletons"] = s => Actor(s, a => a with { Skeletons = [] }),
        ["ActorDescriptor.IsPlayer"] = s => Actor(s, a => a with { IsPlayer = true }),
        ["ActorDescriptor.IsCompanion"] = s => Actor(s, a => a with { IsCompanion = true }),
        ["ActorDescriptor.IsHidden"] = s => Actor(s, a => a with { IsHidden = true }),
        ["ActorDescriptor.OwnerActor"] = s => Actor(s, a => a with { OwnerActor = null }),
        ["ActorDescriptor.AttachmentKind"] = s => Actor(s, a => a with { AttachmentKind = CompanionKind.Mount }),
        ["ActorDescriptor.IsOwned"] = s => Actor(s, a => a with { IsOwned = false }),
        ["ActorDescriptor.IsAdopted"] = s => Actor(s, a => a with { IsAdopted = true }),

        ["LightDescriptor.Id"] = s => s with { Lights = [s.Lights[0] with { Id = Light.NextGeneration() }] },
        ["LightDescriptor.Name"] = s => s with { Lights = [s.Lights[0] with { Name = "light" }] },
        ["LightDescriptor.Kind"] = s => s with { Lights = [s.Lights[0] with { Kind = LightKind.Spot }] },
        ["LightDescriptor.IsOn"] = s => s with { Lights = [s.Lights[0] with { IsOn = false }] },
        ["LightDescriptor.Ownership"] = s => s with { Lights = [s.Lights[0] with { Ownership = LightOwnership.World }] },
        ["LightDescriptor.AttachedBone"] = s => s with { Lights = [s.Lights[0] with { AttachedBone = Hand }] },

        ["CameraDescriptor.Id"] = s => Cam(s, c => c with { Id = Camera.NextGeneration() }),
        ["CameraDescriptor.Name"] = s => Cam(s, c => c with { Name = "camera" }),
        ["CameraDescriptor.Kind"] = s => Cam(s, c => c with { Kind = CameraKind.Game }),
        ["CameraDescriptor.IsLive"] = s => Cam(s, c => c with { IsLive = true }),
        ["CameraDescriptor.IsDefault"] = s => Cam(s, c => c with { IsDefault = true }),
        ["CameraDescriptor.IsLocked"] = s => Cam(s, c => c with { IsLocked = true }),
        ["CameraDescriptor.TargetActor"] = s => Cam(s, c => c with { TargetActor = ActorA }),
        ["CameraDescriptor.TargetBone"] = s => Cam(s, c => c with { TargetBone = Hand }),
        ["CameraDescriptor.TargetOffset"] = s => Cam(s, c => c with { TargetOffset = new Vector3(0, 0.001f, 0) }),

        ["PropDescriptor.Id"] = s => s with { Props = [s.Props[0] with { Id = Prop.NextGeneration() }] },
        ["PropDescriptor.Name"] = s => s with { Props = [s.Props[0] with { Name = "prop" }] },
        ["PropDescriptor.Visible"] = s => s with { Props = [s.Props[0] with { Visible = false }] },

        ["WorldObjectDescriptor.Id"] = s => World(s, w => w with { Id = WorldObject.NextGeneration() }),
        ["WorldObjectDescriptor.Name"] = s => World(s, w => w with { Name = "tree" }),
        ["WorldObjectDescriptor.Path"] = s => World(s, w => w with { Path = "bg/Tree.sgb" }),
        ["WorldObjectDescriptor.Visible"] = s => World(s, w => w with { Visible = false }),
        ["WorldObjectDescriptor.Spawned"] = s => World(s, w => w with { Spawned = true }),
        ["WorldObjectDescriptor.VfxPaused"] = s => World(s, w => w with { VfxPaused = true }),
        ["WorldObjectDescriptor.AnimPaused"] = s => World(s, w => w with { AnimPaused = true }),
        ["WorldObjectDescriptor.Night"] = s => World(s, w => w with { Night = true }),

        ["OverlayDescriptor.Id"] = s => s with { Overlays = [s.Overlays[0] with { Id = Overlay.NextGeneration() }] },
        ["OverlayDescriptor.Name"] = s => s with { Overlays = [s.Overlays[0] with { Name = "overlay" }] },
        ["OverlayDescriptor.Kind"] = s => s with { Overlays = [s.Overlays[0] with { Kind = OverlayNodeKind.Balloon }] },
        ["OverlayDescriptor.Visible"] = s => s with { Overlays = [s.Overlays[0] with { Visible = false }] },

        ["EnvironmentDescriptor.MinuteOfDay"] = s => Env(s, e => e with { MinuteOfDay = 601 }),
        ["EnvironmentDescriptor.DayOfMonth"] = s => Env(s, e => e with { DayOfMonth = 13 }),
        ["EnvironmentDescriptor.WeatherId"] = s => Env(s, e => e with { WeatherId = 3 }),
        ["EnvironmentDescriptor.IsTimeFrozen"] = s => Env(s, e => e with { IsTimeFrozen = true }),
        ["EnvironmentDescriptor.IsWeatherOverrideEnabled"] = s => Env(s, e => e with { IsWeatherOverrideEnabled = true }),
        ["EnvironmentDescriptor.HeldSections"] = s => Env(s, e => e with { HeldSections = EnvironmentSection.Fog }),

        ["GazeDescriptor.Actor"] = s => Gaze(s, g => g with { Actor = ActorB }),
        ["GazeDescriptor.Mode"] = s => Gaze(s, g => g with { Mode = GazeMode.Camera }),
        ["GazeDescriptor.Parts"] = s => Gaze(s, g => g with { Parts = GazeParts.Eyes }),
        ["GazeDescriptor.LockedParts"] = s => Gaze(s, g => g with { LockedParts = GazeParts.Head }),
        ["GazeDescriptor.TargetActor"] = s => Gaze(s, g => g with { TargetActor = ActorB }),
        ["GazeDescriptor.Anchor"] = s => Gaze(s, g => g with { Anchor = Vector3.UnitX }),
        ["GazeDescriptor.EyesPosition"] = s => Gaze(s, g => g with { EyesPosition = Vector3.UnitX }),
        ["GazeDescriptor.HeadPosition"] = s => Gaze(s, g => g with { HeadPosition = Vector3.UnitX }),
        ["GazeDescriptor.BodyPosition"] = s => Gaze(s, g => g with { BodyPosition = Vector3.UnitX }),

        ["SceneSnapshot.Revision"] = s => s with { Revision = 8 },
        ["SceneSnapshot.Actors"] = s => s with { Actors = [] },
        ["SceneSnapshot.Lights"] = s => s with { Lights = [] },
        ["SceneSnapshot.Cameras"] = s => s with { Cameras = [] },
        ["SceneSnapshot.Props"] = s => s with { Props = [] },
        ["SceneSnapshot.Environment"] = s => s with { Environment = null },
        ["SceneSnapshot.GazeStates"] = s => s with { GazeStates = [] },
        ["SceneSnapshot.Overlays"] = s => s with { Overlays = [] },
        ["SceneSnapshot.WorldObjects"] = s => s with { WorldObjects = [] },
    };

    private static readonly Type[] DescriptorTypes =
    [
        typeof(BoneDescriptor),
        typeof(SkeletonDescriptor),
        typeof(ActorDescriptor),
        typeof(LightDescriptor),
        typeof(CameraDescriptor),
        typeof(PropDescriptor),
        typeof(WorldObjectDescriptor),
        typeof(OverlayDescriptor),
        typeof(EnvironmentDescriptor),
        typeof(GazeDescriptor),
        typeof(SceneSnapshot),
    ];

    public static TheoryData<string> EditNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in Edits.Keys)
            data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(EditNames))]
    public void Changing_any_field_breaks_content_equality(string field)
    {
        var baseline = Baseline();
        var edited = Edits[field](Baseline());

        Assert.False(baseline.ContentEquals(edited), field);
        Assert.False(edited.ContentEquals(baseline), field);
    }

    [Fact]
    public void Every_descriptor_field_has_an_edit()
    {
        var missing = DescriptorTypes
            .SelectMany(type => type.GetConstructors()
                .OrderByDescending(ctor => ctor.GetParameters().Length)
                .First()
                .GetParameters()
                .Select(parameter => $"{type.Name}.{parameter.Name}"))
            .Where(name => !Edits.ContainsKey(name))
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void Equal_content_in_different_list_instances_is_equal()
    {
        var first = Baseline();
        var second = Baseline();

        Assert.NotSame(first.Actors, second.Actors);
        Assert.NotSame(first.Actors[0].Skeletons, second.Actors[0].Skeletons);
        Assert.NotSame(first.Actors[0].Skeletons[0].Bones, second.Actors[0].Skeletons[0].Bones);
        Assert.True(first.ContentEquals(second));
        Assert.True(second.ContentEquals(first));
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void Lists_built_from_arrays_and_lists_compare_by_content()
    {
        var bones = new List<BoneDescriptor> { new(Root, "Root", null) };
        var fromList = new SkeletonDescriptor(Skeleton, bones);
        var fromArray = new SkeletonDescriptor(Skeleton, bones.ToArray());
        bones.Clear();

        Assert.Equal(fromList, fromArray);
        Assert.Single(fromList.Bones);
        Assert.Equal(fromArray, fromArray with { Bones = [new BoneDescriptor(Root, "Root", null)] });
    }

    [Fact]
    public void Content_equality_is_ordinal_and_exact()
    {
        var baseline = Baseline();

        Assert.False(baseline.ContentEquals(World(Baseline(), w => w with { Name = "TREE" })));
        Assert.False(baseline.ContentEquals(
            Gaze(Baseline(), g => g with { Anchor = new Vector3(1e-7f, 0, 0) })));
        Assert.False(baseline.ContentEquals(null));
    }

    [Fact]
    public void Value_list_is_structural_and_rejects_null()
    {
        var left = ValueList.From(new[] { 1, 2, 3 });
        var right = ValueList.From(new List<int> { 1, 2, 3 });

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.NotEqual(left, ValueList.From(new[] { 1, 2 }));
        Assert.NotEqual(left, ValueList.From(new[] { 1, 3, 2 }));
        Assert.Same(left, ValueList.From(left));
        Assert.Same(ValueList<int>.Empty, ValueList.From(Array.Empty<int>()));
        Assert.Throws<ArgumentNullException>(() => ValueList.From<int>(null!));
        Assert.Throws<ArgumentNullException>(() => new SkeletonDescriptor(Skeleton, null!));
        Assert.Throws<ArgumentNullException>(() => new ActorDescriptor(ActorA, "Actor", null!));
    }
}
