using Newtonsoft.Json;
using Poser.Application.Presentation;
using Poser.Domain.Identity;
using Poser.Documents.Config;

namespace Poser.Application.Tests.Presentation;

public sealed class BoneVisibilityDefaultsTests
{
    private static BoneId[] Bones()
    {
        var skeleton = new SkeletonId(ActorId.New(), PoseSlot.Character, 1);
        return [new(skeleton, 0, 0, "head"), new(skeleton, 1, 1, "eye"), new(skeleton, 0, 2, "custom")];
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
