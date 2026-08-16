extern alias ProductionPoser;

using Poser.Config;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using ProductionPoser::Poser.UI;

namespace Poser.ContractTests;

/// <summary>
/// The bone-visibility preset contract.
///
/// <para>A preset is a NAMED SET OF BONE NAMES in the persisted config; it is
/// applied to whichever actor the user picks, and whether it reads as applied
/// is derived from the overlay presentation mask rather than stored beside it.
/// The two halves characterized here are the store round-trip — save what an
/// actor shows, persist it, and get the same bones back through a service
/// built fresh over that config — and the derived-state rules that make the
/// menu's checkmarks incapable of disagreeing with the overlay.</para>
/// </summary>
public sealed class BoneVisibilityPresetContractTests
{
    [Fact]
    public void Saving_what_an_actor_shows_round_trips_through_the_persisted_store()
    {
        var config = new PoserConfiguration();
        var presentation = new SkeletonOverlayPresentation();
        var actor = Actor("j_kao", "j_kubi", "j_ude_a_l");
        var service = Service(presentation, config);

        presentation.SetVisible(Bones(actor, "j_kao", "j_kubi"), true);
        Assert.Null(service.SaveCurrent("Head", actor));

        var stored = Assert.Single(config.Skeleton.BoneVisibilityPresets);
        Assert.Equal("Head", stored.Name);
        Assert.Equal(new[] { "j_kao", "j_kubi" }, stored.Bones);

        // A service built fresh over the SAME config — the shape a reload
        // produces — applies the stored names onto a different actor.
        var reloaded = Service(presentation, config);
        var other = Actor("j_kao", "j_kubi", "j_ude_a_l");
        Assert.False(reloaded.IsApplied(other, "Head"));
        reloaded.Toggle(other, "Head");
        Assert.True(reloaded.IsApplied(other, "Head"));
        Assert.False(presentation.IsVisible(Bones(other, "j_ude_a_l")[0]));
    }

    [Fact]
    public void A_preset_reads_as_applied_only_when_every_bone_it_covers_is_shown()
    {
        var config = Store(("Head", new[] { "j_kao", "j_kubi" }));
        var presentation = new SkeletonOverlayPresentation();
        var service = Service(presentation, config);
        var actor = Actor("j_kao", "j_kubi");

        presentation.SetVisible(Bones(actor, "j_kao"), true);
        Assert.False(service.IsApplied(actor, "Head"));

        presentation.SetVisible(Bones(actor, "j_kubi"), true);
        Assert.True(service.IsApplied(actor, "Head"));

        // Toggling with no explicit state flips whatever it reads as.
        service.Toggle(actor, "Head");
        Assert.False(service.IsApplied(actor, "Head"));
    }

    [Fact]
    public void A_preset_whose_bones_this_actor_lacks_never_reads_as_applied()
    {
        var config = Store(("Tail", new[] { "n_sippo_a" }));
        var presentation = new SkeletonOverlayPresentation();
        var service = Service(presentation, config);
        var actor = Actor("j_kao");

        // An empty match must not read as satisfied — otherwise every preset
        // would show checked on a skeleton that carries none of it.
        Assert.False(service.IsApplied(actor, "Tail"));
        service.Toggle(actor, "Tail", true);
        Assert.False(service.IsApplied(actor, "Tail"));
    }

    [Fact]
    public void Showing_the_bones_no_preset_covers_hides_every_covered_bone()
    {
        var config = Store(("Head", new[] { "j_kao" }), ("Arm", new[] { "j_ude_a_l" }));
        var presentation = new SkeletonOverlayPresentation();
        var service = Service(presentation, config);
        var actor = Actor("j_kao", "j_ude_a_l", "n_sippo_a");

        presentation.SetVisible(Bones(actor, "j_kao", "j_ude_a_l"), true);
        service.ToggleOther(actor);

        Assert.False(presentation.IsVisible(Bones(actor, "j_kao")[0]));
        Assert.False(presentation.IsVisible(Bones(actor, "j_ude_a_l")[0]));
        Assert.True(presentation.IsVisible(Bones(actor, "n_sippo_a")[0]));
    }

    [Fact]
    public void Clearing_hides_every_bone_the_actor_carries_on_every_slot()
    {
        var presentation = new SkeletonOverlayPresentation();
        var service = Service(presentation, new PoserConfiguration());
        var actor = ActorWithWeapon("j_kao", "n_buki_ridge_a_l");

        presentation.SetVisible(Bones(actor, "j_kao", "n_buki_ridge_a_l"), true);
        service.Clear(actor);

        Assert.False(presentation.AnyVisible);
    }

    [Fact]
    public void Saving_refuses_a_blank_name_a_duplicate_and_an_empty_overlay()
    {
        var config = new PoserConfiguration();
        var presentation = new SkeletonOverlayPresentation();
        var service = Service(presentation, config);
        var actor = Actor("j_kao");

        Assert.NotNull(service.SaveCurrent("Head", actor));
        Assert.Empty(config.Skeleton.BoneVisibilityPresets);

        presentation.SetVisible(Bones(actor, "j_kao"), true);
        Assert.NotNull(service.SaveCurrent("   ", actor));
        Assert.Null(service.SaveCurrent("Head", actor));
        // Case is not a distinction: two presets differing only in case would
        // be indistinguishable in the menu.
        Assert.NotNull(service.SaveCurrent("head", actor));
        Assert.Single(config.Skeleton.BoneVisibilityPresets);
    }

    [Fact]
    public void Deleting_a_preset_removes_it_from_the_persisted_store()
    {
        var config = Store(("Head", new[] { "j_kao" }));
        var service = Service(new SkeletonOverlayPresentation(), config);

        Assert.False(service.Delete("Nothing"));
        Assert.True(service.Delete("HEAD"));
        Assert.Empty(config.Skeleton.BoneVisibilityPresets);
    }

    [Fact]
    public void Saved_presets_stay_sorted_so_the_persisted_file_is_stable()
    {
        var config = new PoserConfiguration();
        var presentation = new SkeletonOverlayPresentation();
        var service = Service(presentation, config);
        var actor = Actor("j_kao");
        presentation.SetVisible(Bones(actor, "j_kao"), true);

        Assert.Null(service.SaveCurrent("Tail", actor));
        Assert.Null(service.SaveCurrent("Arms", actor));

        Assert.Equal(
            new[] { "Arms", "Tail" },
            config.Skeleton.BoneVisibilityPresets.Select(preset => preset.Name));
    }

    [Fact]
    public void Skeleton_eye_restores_only_the_exact_subset_and_drops_old_generation()
    {
        Assert.True(new PoserConfiguration().Skeleton.HideSkeletonOnActorSelection);
        var presentation = new SkeletonOverlayPresentation();
        var actor = Actor("j_kao", "j_kubi", "j_ude_a_l");
        var bones = Bones(actor, "j_kao", "j_kubi", "j_ude_a_l");
        presentation.SetVisible(bones[..2], true);

        presentation.ToggleVisibleWithMemory("actor/skeleton", bones);
        Assert.False(presentation.AnyVisible);

        presentation.ToggleVisibleWithMemory("actor/skeleton", bones);
        Assert.True(presentation.IsVisible(bones[0]));
        Assert.True(presentation.IsVisible(bones[1]));
        Assert.False(presentation.IsVisible(bones[2]));

        var replacement = ActorWithId(
            actor.Id.NextGeneration(), "j_kao", "j_kubi", "j_ude_a_l");
        presentation.Reconcile(Scene(
            replacement,
            Bones(replacement, "j_kao", "j_kubi", "j_ude_a_l")));
        var current = Bones(replacement, "j_kao", "j_kubi", "j_ude_a_l");
        presentation.ToggleVisibleWithMemory("actor/skeleton", current);
        Assert.True(presentation.IsVisible(current[0]));
        Assert.True(presentation.IsVisible(current[1]));
        Assert.True(presentation.IsVisible(current[2]));
    }

    [Fact]
    public void Bone_rows_use_real_roots_without_duplicate_labels_or_targets()
    {
        var actor = ActorId.New();
        var head = new BoneDescriptor(
            new BoneId(
                new SkeletonId(actor, PoseSlot.Character, 0),
                0, 1, "j_head"),
            "Head",
            Parent: null);
        var headRoot = new BoneDescriptor(
            new BoneId(
                new SkeletonId(actor, PoseSlot.Character, 0),
                0, 2, "j_kao"),
            "Face bone",
            Parent: null);
        var leftArm = new BoneDescriptor(
            new BoneId(
                new SkeletonId(actor, PoseSlot.Character, 0),
                0, 3, "j_ude_a_l"),
            "Arm Left",
            Parent: null);

        Assert.Equal(
            head.Id,
            ProductionPoser::Poser.UI.MainWindow.ResolveCategoryBone(
                "Head", "Head", new[] { head, headRoot })!.Id);
        Assert.Equal(
            leftArm.Id,
            ProductionPoser::Poser.UI.MainWindow.ResolveCategoryBone(
                "LeftArm", "Left Arm", new[] { leftArm })!.Id);

        var abdomen = new BoneDescriptor(
            new BoneId(
                new SkeletonId(actor, PoseSlot.Character, 0),
                0, 4, "n_hara"),
            "Abdomen",
            Parent: null);
        Assert.Equal(
            abdomen.Id,
            ProductionPoser::Poser.UI.MainWindow.ResolveCharacterRootBone(
                new[] { abdomen })!.Id);
        Assert.Null(
            ProductionPoser::Poser.UI.MainWindow.ResolveCharacterRootBone(
                new[] { leftArm }));

        var child = new BoneDescriptor(
            new BoneId(
                new SkeletonId(actor, PoseSlot.Character, 0),
                0, 5, "j_ude_b_l"),
            "Forearm Left",
            Parent: leftArm.Id);
        Assert.Equal(
            new[] { leftArm.Id },
            ProductionPoser::Poser.UI.MainWindow.NonOverlappingBoneTargets(
                new[] { leftArm, child }));
    }

    // ── fixtures ────────────────────────────────────────────────────────────

    private static BoneVisibilityPresetService Service(
        SkeletonOverlayPresentation presentation,
        PoserConfiguration config) =>
        new(presentation, () => config, () => { });

    private static PoserConfiguration Store(
        params (string Name, string[] Bones)[] presets)
    {
        var config = new PoserConfiguration();
        foreach (var (name, bones) in presets)
            config.Skeleton.BoneVisibilityPresets.Add(
                new BoneVisibilityPreset { Name = name, Bones = bones.ToList() });
        return config;
    }

    private static ActorDescriptor Actor(params string[] bones) =>
        ActorWithId(ActorId.New(), bones);

    private static ActorDescriptor ActorWithId(
        ActorId actor, params string[] bones) =>
        new(
            actor,
            "Fixture",
            new[] { Skeleton(actor, PoseSlot.Character, bones) });

    private static ActorDescriptor ActorWithWeapon(string body, string weapon)
    {
        var actor = ActorId.New();
        return new(
            actor,
            "Fixture",
            new[]
            {
                Skeleton(actor, PoseSlot.Character, new[] { body }),
                Skeleton(actor, PoseSlot.MainHand, new[] { weapon }),
            });
    }

    private static SkeletonDescriptor Skeleton(
        ActorId actor, PoseSlot slot, string[] bones)
    {
        var id = new SkeletonId(actor, slot, 0);
        var descriptors = new List<BoneDescriptor>(bones.Length);
        for (int i = 0; i < bones.Length; i++)
            descriptors.Add(new BoneDescriptor(
                new BoneId(id, 0, i, bones[i]), bones[i], null));
        return new SkeletonDescriptor(id, descriptors);
    }

    private static BoneId[] Bones(ActorDescriptor actor, params string[] names) =>
        actor.Skeletons
            .SelectMany(skeleton => skeleton.Bones)
            .Where(bone => names.Contains(bone.Id.CanonicalName))
            .Select(bone => bone.Id)
            .ToArray();

    private static SceneSnapshot Scene(
        ActorDescriptor actor, BoneId[] bones) =>
        new(
            Revision: actor.Id.Generation + 1,
            Actors: new[]
            {
                actor with
                {
                    Skeletons = new[]
                    {
                        new SkeletonDescriptor(
                            bones[0].Skeleton,
                            bones.Select(bone => new BoneDescriptor(
                                bone, bone.CanonicalName, null)).ToArray()),
                    },
                },
            },
            Lights: Array.Empty<LightDescriptor>(),
            Cameras: Array.Empty<CameraDescriptor>(),
            Props: Array.Empty<PropDescriptor>());
}
