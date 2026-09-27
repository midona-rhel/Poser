using Poser.Config;

namespace Poser.Tests.Files;

public sealed class DefaultBonePresetsTests
{
    [Fact]
    public void Head_and_face_can_be_toggled_independently()
    {
        var presets = DefaultBonePresets.Build();
        var head = presets.Single(p => p.Name == "Head").Bones;
        var face = presets.Single(p => p.Name == "Face").Bones;
        Assert.Contains("j_kao", head);
        Assert.Contains("j_kubi", head);
        Assert.Contains("j_kami_a", head);
        Assert.Contains("j_mimi_l", head);
        Assert.Contains("j_f_eye_l", face);
        Assert.Contains("j_f_ulip_a", face);
        Assert.Empty(head.Intersect(face));
        Assert.All(presets, p => Assert.Equal(p.Bones.Count, p.Bones.Distinct().Count()));
    }
}
