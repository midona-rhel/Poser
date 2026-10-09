using Newtonsoft.Json;
using Poser.Application.Presentation;
using Poser.Config;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Scene;

namespace Poser.Application.Tests.Settings;

public sealed class BoneMapPresetTests
{
    [Theory]
    [InlineData(BoneMapKind.Body)]
    [InlineData(BoneMapKind.Face)]
    public void Built_in_default_name_is_reserved_and_new_copy_does_not_mutate_its_source(BoneMapKind kind)
    {
        var points = new[] { Default };
        var copy = new BoneMapPresetDraft(kind, points, initial: points);
        Assert.False(copy.HasChanges);
        copy.Name = "Default";
        Assert.NotNull(copy.Save(new List<BoneMapPreset>(), out _));
        copy.Name = "My body";
        copy.Move(Bone, "body", 0.7f, 0.8f);
        Assert.Equal(Default, points[0]);
        Assert.True(copy.HasChanges);
        Assert.Null(copy.Save(new List<BoneMapPreset>(), out _));
    }

    [Fact]
    public void Delete_removes_only_the_selected_custom_preset_and_rejects_stale_edits()
    {
        var body = new BoneMapPreset { Name = "Body", Points = [Default] };
        var face = new BoneMapPreset { Kind = BoneMapKind.Face, Name = "Face", Points = [Default] };
        var store = new List<BoneMapPreset> { body, face };
        var draft = new BoneMapPresetDraft(BoneMapKind.Body, [Default], body);
        var other = new BoneMapPresetDraft(BoneMapKind.Body, [Default], body) { Name = "Changed" };
        Assert.Null(other.Save(store, out _));
        Assert.NotNull(draft.Delete(store));
        Assert.Equal(2, store.Count);
        draft = new(BoneMapKind.Body, [Default], store[0]);
        Assert.Null(draft.Delete(store));
        Assert.Same(face, Assert.Single(store));
        Assert.NotNull(new BoneMapPresetDraft(BoneMapKind.Face, [Default]).Delete(store));
        Assert.Same(face, Assert.Single(store));
    }
    private static readonly PortableBoneId Bone = new(PoseSlot.Character, 0, "j_kubi");
    private static readonly BoneMapPoint Default = new(Bone, "body", 0.2f, 0.3f);

    [Fact]
    public void Draft_is_detached_and_body_face_names_are_independent()
    {
        var body = new BoneMapPreset { Name = "My layout", Points = [Default] };
        var store = new List<BoneMapPreset> { body };
        var draft = new BoneMapPresetDraft(BoneMapKind.Body, [Default], body);
        draft.Move(Bone, "body", 0.8f, 0.7f);
        Assert.Equal(Default, Assert.Single(body.Points));
        Assert.Null(draft.Save(store, out var id));
        Assert.Equal(body.Id, id);
        var face = new BoneMapPresetDraft(BoneMapKind.Face, []) { Name = "My layout" };
        Assert.Null(face.Save(store, out _));
        Assert.Equal(2, store.Count);
        var duplicate = new BoneMapPresetDraft(BoneMapKind.Body, []) { Name = "my layout" };
        Assert.NotNull(duplicate.Save(store, out _));
    }

    [Fact]
    public void Reset_and_default_have_distinct_baselines_and_removed_bones_can_return()
    {
        var initial = Default with { X = 0.6f };
        var draft = new BoneMapPresetDraft(BoneMapKind.Body, [Default],
            new() { Name = "Body", Points = [initial] });
        draft.Move(Bone, "body", 0.9f, 0.9f);
        draft.Reset(Bone, "body", toDefault: false);
        Assert.Equal(initial, Assert.Single(draft.Points));
        draft.Reset(Bone, "body", toDefault: true);
        Assert.Equal(Default, Assert.Single(draft.Points));
        draft.Remove(Bone);
        Assert.Empty(draft.Points);
        draft.Add(Bone);
        draft.Add(Bone);
        Assert.Equal(Default, Assert.Single(draft.Points));
    }

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

    [Fact]
    public void Slot_and_partial_are_required_and_ambiguous_native_indices_are_not_resolved()
    {
        var actor = ActorId.New();
        var character = new SkeletonId(actor, PoseSlot.Character, 0);
        var weapon = new SkeletonId(actor, PoseSlot.MainHand, 0);
        BoneDescriptor Make(SkeletonId skeleton, int partial, int index) =>
            new(new(skeleton, partial, index, Bone.CanonicalName), "Neck", null);
        var available = BoneMapPresetDraft.Available(new(actor, "Actor",
            [new(character, [Make(character, 0, 1), Make(character, 0, 2), Make(character, 1, 3)]),
             new(weapon, [Make(weapon, 0, 0)])]));
        Assert.DoesNotContain(Bone, available.Keys);
        Assert.Contains(Bone with { PartialId = 1 }, available.Keys);
        Assert.Contains(Bone with { Slot = PoseSlot.MainHand }, available.Keys);
    }
}
