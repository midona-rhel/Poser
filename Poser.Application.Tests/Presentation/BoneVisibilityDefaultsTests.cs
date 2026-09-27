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
    public void Disabled_option_keeps_every_requested_bone()
    {
        var scope = Bones();
        Assert.Equal(scope, BoneVisibilityDefaults.Resolve(new(), scope));
    }

    [Fact]
    public void Enabled_option_uses_union_of_checked_stock_and_custom_sets_in_scope()
    {
        var scope = Bones();
        var settings = new SkeletonConfiguration
        {
            UseDefaultBonePresetsOnShow = true,
            BoneVisibilityPresets = [
                new() { Name = "Head", Bones = ["head"] },
                new() { Name = "Face", Bones = ["eye"], ShowByDefault = false },
                new() { Name = "Custom", Bones = ["head", "custom", "absent"] },
            ],
        };
        Assert.Equal(new[] { scope[0], scope[2] }, BoneVisibilityDefaults.Resolve(settings, scope));
        Assert.Empty(BoneVisibilityDefaults.Resolve(settings, [scope[1]]));
    }

    [Fact]
    public void No_checked_presets_does_not_fall_back_to_showing_everything()
    {
        var settings = new SkeletonConfiguration { UseDefaultBonePresetsOnShow = true };
        Assert.Empty(BoneVisibilityDefaults.Resolve(settings, Bones()));
        settings.BoneVisibilityPresets.Add(new() { Name = "Head", Bones = ["head"], ShowByDefault = false });
        Assert.Empty(BoneVisibilityDefaults.Resolve(settings, Bones()));
    }

    [Fact]
    public void Legacy_config_is_opt_in_and_checkbox_choices_survive_serialization()
    {
        var settings = JsonConvert.DeserializeObject<SkeletonConfiguration>("{\"BoneVisibilityPresets\":[{\"Name\":\"Head\",\"Bones\":[\"head\"]}]}")!;
        Assert.False(settings.UseDefaultBonePresetsOnShow);
        Assert.True(settings.BoneVisibilityPresets[0].ShowByDefault);
        settings.UseDefaultBonePresetsOnShow = true;
        settings.BoneVisibilityPresets[0].ShowByDefault = false;
        var restored = JsonConvert.DeserializeObject<SkeletonConfiguration>(JsonConvert.SerializeObject(settings))!;
        Assert.True(restored.UseDefaultBonePresetsOnShow);
        Assert.False(restored.BoneVisibilityPresets[0].ShowByDefault);
    }
}
