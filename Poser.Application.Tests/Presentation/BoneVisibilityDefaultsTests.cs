using Newtonsoft.Json;
using Poser.Application.Presentation;
using Poser.Config;
using Poser.Domain.Identity;

namespace Poser.Application.Tests.Presentation;

public sealed class BoneVisibilityDefaultsTests
{
    private static BoneId[] Bones()
    {
        var skeleton = new SkeletonId(ActorId.New(), PoseSlot.Character, 1);
        return [new(skeleton, 0, 0, "head"), new(skeleton, 1, 1, "eye"), new(skeleton, 0, 2, "custom")];
    }

    [Fact]
    public void Unconfigured_defaults_have_no_matches_and_cannot_apply()
    {
        var scope = Bones();
        Assert.Empty(BoneVisibilityDefaults.Resolve(new(), scope));
        Assert.False(BoneVisibilityDefaults.CanApply(new(), scope));
    }

    [Fact]
    public void Defaults_use_union_of_checked_stock_and_custom_sets_in_scope()
    {
        var scope = Bones();
        var settings = new SkeletonConfiguration
        {
            BoneVisibilityPresets = [
                new() { Name = "Head", Bones = ["head"] },
                new() { Name = "Face", Bones = ["eye"], ShowByDefault = false },
                new() { Name = "Custom", Bones = ["head", "custom", "absent"] },
            ],
        };
        Assert.Equal(new[] { scope[0], scope[2] }, BoneVisibilityDefaults.Resolve(settings, scope));
        Assert.Empty(BoneVisibilityDefaults.Resolve(settings, [scope[1]]));
        Assert.True(BoneVisibilityDefaults.CanApply(settings, scope));
        Assert.False(BoneVisibilityDefaults.CanApply(settings, [scope[1]]));
    }

    [Fact]
    public void No_checked_presets_does_not_fall_back_to_showing_everything()
    {
        var settings = new SkeletonConfiguration();
        Assert.Empty(BoneVisibilityDefaults.Resolve(settings, Bones()));
        Assert.False(BoneVisibilityDefaults.CanApply(settings, Bones()));
        settings.BoneVisibilityPresets.Add(new() { Name = "Head", Bones = ["head"], ShowByDefault = false });
        Assert.Empty(BoneVisibilityDefaults.Resolve(settings, Bones()));
    }

    [Fact]
    public void Legacy_automatic_option_is_ignored_and_checkbox_choices_survive_serialization()
    {
        var settings = JsonConvert.DeserializeObject<SkeletonConfiguration>("{\"UseDefaultBonePresetsOnShow\":true,\"BoneVisibilityPresets\":[{\"Name\":\"Head\",\"Bones\":[\"head\"]}]}")!;
        Assert.True(settings.BoneVisibilityPresets[0].ShowByDefault);
        settings.BoneVisibilityPresets[0].ShowByDefault = false;
        var json = JsonConvert.SerializeObject(settings);
        Assert.DoesNotContain("UseDefaultBonePresetsOnShow", json);
        var restored = JsonConvert.DeserializeObject<SkeletonConfiguration>(json)!;
        Assert.False(restored.BoneVisibilityPresets[0].ShowByDefault);
    }
}
