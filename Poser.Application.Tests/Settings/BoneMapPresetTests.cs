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
    public void Unique_names_increment_case_insensitively_within_the_map_kind()
    {
        BoneMapPreset[] store = [new() { Name = "Body copy" }, new() { Name = "BODY COPY 2" },
            new() { Name = "Body copy 3", Kind = BoneMapKind.Face }];
        Assert.Equal("Body copy 3", BoneMapPresetDraft.UniqueName(store, BoneMapKind.Body, "Body copy"));
        Assert.Equal("Default 2", BoneMapPresetDraft.UniqueName(store, BoneMapKind.Body, "Default"));
    }

    [Fact]
    public void Remove_with_children_follows_hierarchy_through_unmapped_bones_and_keeps_siblings()
    {
        var actor = ActorId.New();
        var skeleton = new SkeletonId(actor, PoseSlot.Character, 0);
        var root = new BoneDescriptor(new(skeleton, 0, 0, "root"), "Root", null);
        var middle = new BoneDescriptor(new(skeleton, 0, 1, "middle"), "Middle", root.Id);
        var end = new BoneDescriptor(new(skeleton, 0, 2, "end"), "End", middle.Id);
        var sibling = new BoneDescriptor(new(skeleton, 0, 3, "sibling"), "Sibling", root.Id);
        var available = BoneMapPresetDraft.Available(new(actor, "Actor", [new(skeleton, [root, middle, end, sibling])]));
        var draft = new BoneMapPresetDraft(BoneMapKind.Body,
            [new(PortableBoneId.From(root.Id), "body", .5f, .1f),
             new(PortableBoneId.From(end.Id), "body", .5f, .2f),
             new(PortableBoneId.From(end.Id), "hands", .5f, .3f),
             new(PortableBoneId.From(sibling.Id), "body", .5f, .4f)]);
        draft.RemoveWithChildren(PortableBoneId.From(middle.Id), available);
        Assert.Equal(2, draft.Points.Count);
        Assert.False(draft.Contains(PortableBoneId.From(end.Id)));
        Assert.True(draft.Contains(PortableBoneId.From(sibling.Id)));
        draft.RemoveWithChildren(PortableBoneId.From(root.Id), available);
        Assert.Empty(draft.Points);
    }

    [Fact]
    public void Background_changes_save_without_moving_points_and_detect_concurrent_edits()
    {
        var preset = new BoneMapPreset { Name = "Face", Kind = BoneMapKind.Face, Points = [Default] };
        var store = new List<BoneMapPreset> { preset };
        var draft = new BoneMapPresetDraft(BoneMapKind.Face, [], preset) { Background = "PoseHeadHroth" };
        var stale = new BoneMapPresetDraft(BoneMapKind.Face, [], preset);
        Assert.True(draft.HasChanges);
        Assert.Null(draft.Save(store, out _));
        Assert.Equal(Default, Assert.Single(store[0].Points));
        Assert.Equal("PoseHeadHroth", BoneMapPresetDraft.Copy(store[0]).Background);
        Assert.NotNull(stale.Save(store, out _));
        Assert.NotNull(stale.Delete(store));
    }

    [Fact]
    public void Mirror_placement_reflects_about_mapped_parent_and_updates_only_its_panel()
    {
        var actor = ActorId.New();
        var skeleton = new SkeletonId(actor, PoseSlot.Character, 0);
        var parent = new BoneDescriptor(new(skeleton, 0, 0, "parent"), "Parent", null);
        var left = new BoneDescriptor(new(skeleton, 0, 1, "arm_l"), "Left", parent.Id);
        var right = new BoneDescriptor(new(skeleton, 0, 2, "arm_r"), "Right", parent.Id);
        var available = BoneMapPresetDraft.Available(new(actor, "Actor", [new(skeleton, [parent, left, right])]));
        var leftKey = PortableBoneId.From(left.Id);
        var rightKey = PortableBoneId.From(right.Id);
        var draft = new BoneMapPresetDraft(BoneMapKind.Body,
            [new(PortableBoneId.From(parent.Id), "body", .4f, .2f), new(leftKey, "body", .2f, .7f),
             new(rightKey, "hands", .9f, .9f)]);
        Assert.True(draft.PlaceMirror(leftKey, "body", available, .5f));
        var mirrored = Assert.Single(draft.Points, point => point.Bone == rightKey && point.Section == "body");
        Assert.Equal(.6f, mirrored.X, 5);
        Assert.Equal(.7f, mirrored.Y);
        Assert.Equal(.9f, Assert.Single(draft.Points, point => point.Section == "hands").X);
        draft.Move(leftKey, "body", .1f, .8f);
        Assert.True(draft.PlaceMirror(leftKey, "body", available, .5f));
        Assert.Equal(.7f, Assert.Single(draft.Points, point => point.Bone == rightKey && point.Section == "body").X, 5);
    }

    [Fact]
    public void Mirror_requires_same_slot_partial_and_a_position_inside_the_map()
    {
        var actor = ActorId.New();
        var skeleton = new SkeletonId(actor, PoseSlot.Character, 0);
        var left = new BoneDescriptor(new(skeleton, 0, 1, "arm_l"), "Left", null);
        var foreign = new BoneDescriptor(new(skeleton, 1, 2, "arm_r"), "Right", null);
        var right = foreign with { Id = foreign.Id with { PartialId = 0 } };
        var key = PortableBoneId.From(left.Id);
        var draft = new BoneMapPresetDraft(BoneMapKind.Body, [new(key, "body", .8f, .4f)]);
        var available = BoneMapPresetDraft.Available(new(actor, "Actor", [new(skeleton, [left, foreign])]));
        Assert.False(draft.PlaceMirror(key, "body", available, .5f));
        available = BoneMapPresetDraft.Available(new(actor, "Actor", [new(skeleton, [left, right])]));
        Assert.False(draft.PlaceMirror(key, "body", available, .1f));
        Assert.True(draft.PlaceMirror(key, "body", available, .5f));
        Assert.Equal(.2f, Assert.Single(draft.Points, point => point.Bone.CanonicalName == "arm_r").X, 5);
    }

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
