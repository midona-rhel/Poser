using Newtonsoft.Json;
using Poser.Application.Presentation;
using Poser.Config;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Scene;

namespace Poser.Application.Tests.Settings;

public sealed class BoneMapPresetTests
{
    private static readonly PortableBoneId Bone = new(PoseSlot.Character, 0, "j_kubi");
    private static readonly BoneMapPoint Default = new(Bone, "body", 0.2f, 0.3f);

    [Fact]
    public void Portable_entries_survive_serialization_and_missing_actor_bones()
    {
        var settings = new SkeletonConfiguration
        {
            BoneMapPresets = [new() { Name = "Body", Points = [Default] }],
        };
        var loaded = JsonConvert.DeserializeObject<SkeletonConfiguration>(JsonConvert.SerializeObject(settings))!;
        var preset = Assert.Single(loaded.BoneMapPresets);
        Assert.Equal(Default, Assert.Single(preset.Points));
        Assert.Empty(BoneMapPresetDraft.Available(new(ActorId.New(), "Missing", [])));
        Assert.Equal(Default, Assert.Single(preset.Points));
        var actor = ActorId.New();
        var skeleton = new SkeletonId(actor, PoseSlot.Character, 3);
        var bone = new BoneDescriptor(new(skeleton, 0, 12, Bone.CanonicalName), "Neck", null);
        var available = BoneMapPresetDraft.Available(new(actor, "Present", [new(skeleton, [bone])]));
        Assert.Equal(bone.Id, available[Bone].Id);
    }

    [Fact]
    public void Concurrent_edits_cannot_overwrite_a_changed_or_deleted_preset()
    {
        var preset = new BoneMapPreset { Name = "Body", Points = [Default] };
        var store = new List<BoneMapPreset> { preset };
        var first = new BoneMapPresetDraft(BoneMapKind.Body, [Default], preset);
        var second = new BoneMapPresetDraft(BoneMapKind.Body, [Default], preset);
        first.Move(Bone, "body", 0.8f, 0.8f);
        Assert.Null(first.Save(store, out _));
        Assert.NotNull(second.Save(store, out _));
        store.Clear();
        Assert.NotNull(second.Save(store, out _));
    }
}
