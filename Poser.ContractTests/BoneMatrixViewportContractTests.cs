extern alias ProductionPoser;

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin.Services;
using NSubstitute;
using global::Poser.Application.Selection;
using global::Poser.Core.BoneInfo;
using global::Poser.Domain.Identity;
using global::Poser.Domain.Scene;
using ProductionPoser::Poser.UI;
using ProductionPoser::Poser.UI.Views;

namespace Poser.ContractTests;

/// <summary>
/// Stateful production-path coverage for Matrix viewport culling. The mutable
/// scene builds real descriptors and lets BoneMatrixBuilder.Build create the
/// retained model used by the live pane; DrawForTesting then invokes the same
/// retained layout/emission traversal as BoneMatrixView.Draw.
/// </summary>
public sealed class BoneMatrixViewportContractTests
{
    static BoneMatrixViewportContractTests() =>
        BoneInfoService.Initialize(Substitute.For<IPluginLog>());

    [Fact]
    public void Stateful_viewport_scroll_culls_emission_but_keeps_extent_and_identity()
    {
        var scene = new DummyMatrixScene();
        var full = Draw(scene, FullClip);
        Assert.Equal(1, scene.RebuildCount);

        int fullGeometry = full.Emissions.Count;
        Assert.True(fullGeometry > 12);
        Assert.Equal(fullGeometry, full.Emissions.Select(item => item.SemanticKey).Distinct().Count());

        var rowStarts = full.Emissions
            .Where(item => item.Kind == "row")
            .Select(item => item.Geometry.Min.Y)
            .Distinct()
            .OrderBy(value => value)
            .ToArray();
        Assert.True(rowStarts.Length >= 3);
        float firstBoundary = rowStarts[rowStarts.Length / 3];
        float secondBoundary = rowStarts[(rowStarts.Length * 2) / 3];
        Assert.True(firstBoundary < secondBoundary);

        var top = Draw(scene, Clip(-100f, firstBoundary));
        var middle = Draw(scene, Clip(firstBoundary, secondBoundary));
        var bottom = Draw(scene, Clip(secondBoundary, 10_000f));

        Assert.Equal(1, scene.RebuildCount);
        Assert.Equal(full.Height, top.Height);
        Assert.Equal(full.Height, middle.Height);
        Assert.Equal(full.Height, bottom.Height);
        Assert.All(
            top.Emissions.Concat(middle.Emissions).Concat(bottom.Emissions),
            item => Assert.True(item.Geometry.Intersects(item.Clip)));
        Assert.All(
            new[] { top, middle, bottom },
            partition => Assert.True(partition.Emissions.Count < fullGeometry));

        var fullKeys = full.Emissions.Select(item => item.SemanticKey).ToHashSet();
        var partitionItems = top.Emissions.Concat(middle.Emissions).Concat(bottom.Emissions).ToList();
        Assert.Equal(partitionItems.Count, partitionItems.Select(item => item.SemanticKey).Distinct().Count());
        Assert.Equal(fullKeys, partitionItems.Select(item => item.SemanticKey).ToHashSet());

        // Adjacent clips use half-open ownership. Geometry beginning at a
        // boundary belongs to the following clip, never both clips.
        var firstBoundaryRow = full.Emissions.First(item =>
            item.Kind == "row" && item.Geometry.Min.Y == firstBoundary);
        Assert.DoesNotContain(firstBoundaryRow.SemanticKey,
            top.Emissions.Select(item => item.SemanticKey));
        Assert.Contains(firstBoundaryRow.SemanticKey,
            middle.Emissions.Select(item => item.SemanticKey));
        var secondBoundaryRow = full.Emissions.First(item =>
            item.Kind == "row" && item.Geometry.Min.Y == secondBoundary);
        Assert.DoesNotContain(secondBoundaryRow.SemanticKey,
            middle.Emissions.Select(item => item.SemanticKey));
        Assert.Contains(secondBoundaryRow.SemanticKey,
            bottom.Emissions.Select(item => item.SemanticKey));

        foreach (var partition in new[] { top, middle, bottom })
        {
            var emitted = partition.Emissions
                .Select(item => item.SemanticKey)
                .ToHashSet();
            foreach (var item in full.Emissions)
                if (!item.Geometry.Intersects(partition.Clip))
                    Assert.DoesNotContain(item.SemanticKey, emitted);
        }
    }

    [Fact]
    public void Stateful_builder_mutations_rebuild_only_when_needed_and_reject_stale_identity()
    {
        var scene = new DummyMatrixScene();
        var initial = Draw(scene, FullClip);
        Assert.Equal(1, scene.RebuildCount);

        // These two distinct exact BoneIds intentionally share partial/index.
        Assert.Equal(initial.PillIds.Count, initial.PillIds.Distinct().Count());
        var oldPill = initial.Pill("customBone0");
        var nsfwPill = initial.Pill("iv_c_mune_l");

        scene.Select(oldPill.SelectionId!.Value);
        var selected = Draw(scene, FullClip);
        Assert.Equal(1, scene.RebuildCount);
        Assert.Contains(oldPill.Id, selected.SelectedPillIds);

        // The live Matrix surface consumes only the primary descriptor.
        var primaryIds = selected.PillIds.ToHashSet();
        scene.ReplaceIndependentSlotGeneration(2);
        var independentSlot = Draw(scene, FullClip);
        Assert.Equal(1, scene.RebuildCount);
        Assert.Equal(primaryIds, independentSlot.PillIds.ToHashSet());

        scene.ReplacePrimarySlotGeneration(2);
        var slotReplacement = Draw(scene, FullClip);
        Assert.Equal(2, scene.RebuildCount);
        var currentSameBone = slotReplacement.Pill(oldPill.CanonicalName);
        Assert.NotEqual(oldPill.Id, currentSameBone.Id);
        Assert.DoesNotContain(oldPill.Id, slotReplacement.PillIds);
        Assert.DoesNotContain(oldPill.Id, slotReplacement.SelectedPillIds);
        Assert.Equal(2u, currentSameBone.SelectionId!.Value.Bone!.Value.Skeleton.Generation);

        scene.HideBone(currentSameBone.CanonicalName);
        var hidden = Draw(scene, FullClip);
        Assert.Equal(3, scene.RebuildCount);
        Assert.DoesNotContain(currentSameBone.Id, hidden.PillIds);

        scene.ToggleNsfw(false);
        var nsfwOff = Draw(scene, FullClip);
        Assert.Equal(4, scene.RebuildCount);
        Assert.DoesNotContain(nsfwPill.CanonicalName,
            nsfwOff.Emissions.Where(item => item.Kind == "pill")
                .Select(item => item.CanonicalName));

        scene.ToggleNsfw(true);
        var nsfwOn = Draw(scene, FullClip);
        Assert.Equal(5, scene.RebuildCount);
        Assert.Contains(nsfwPill.CanonicalName,
            nsfwOn.Emissions.Where(item => item.Kind == "pill")
                .Select(item => item.CanonicalName));

        scene.ReplaceGenerations(actor: 2, skeleton: 2);
        scene.Select(oldPill.SelectionId!.Value); // stale old exact identity
        var replacement = Draw(scene, FullClip);
        Assert.Equal(6, scene.RebuildCount);
        Assert.DoesNotContain(oldPill.Id, replacement.PillIds);
        Assert.DoesNotContain(oldPill.Id, replacement.SelectedPillIds);
        Assert.All(
            replacement.Emissions.Where(item => item.Kind == "pill"),
            item => Assert.Equal(2u, item.SelectionId!.Value.Bone!.Value.Skeleton.Actor.Generation));

        var current = replacement.Emissions.First(item => item.Kind == "pill");
        scene.Select(current.SelectionId!.Value);
        var selectedCurrent = Draw(scene, FullClip);
        Assert.Equal(6, scene.RebuildCount);
        Assert.Contains(current.Id, selectedCurrent.SelectedPillIds);

        scene.ReplaceGenerations(actor: 3, skeleton: 3);
        var recovered = Draw(scene, FullClip);
        Assert.Equal(7, scene.RebuildCount);
        Assert.NotEmpty(recovered.PillIds);
        Assert.All(
            recovered.Emissions.Where(item => item.Kind == "pill"),
            item => Assert.Equal(3u, item.SelectionId!.Value.Bone!.Value.Skeleton.Actor.Generation));
    }

    private static readonly BoneMatrixClipRect FullClip =
        new(new Vector2(-100f, -100f), new Vector2(10_000f, 10_000f));

    private static BoneMatrixClipRect Clip(float minY, float maxY) =>
        new(new Vector2(-100f, minY), new Vector2(10_000f, maxY));

    private static DrawResult Draw(DummyMatrixScene scene, BoneMatrixClipRect clip)
    {
        var sink = new CountingSink(clip);
        float height = scene.Draw(clip, ref sink);
        return new DrawResult(
            height,
            clip,
            sink.Emissions,
            sink.Emissions.Where(item => item.Kind == "pill").Select(item => item.Id).ToList(),
            sink.Emissions.Where(item => item.Kind == "pill" && item.Selected)
                .Select(item => item.Id).ToList());
    }

    private sealed class DummyMatrixScene
    {
        private static readonly string[] Names =
        {
            "customBone0", "customBone1", "customBone2", "customBone3",
            "customBone4", "customBone5", "customBone6", "customBone7",
            "customBone8", "customBone9", "customBone10", "customBone11",
            "customBone12", "customBone13", "customBone14", "customBone15",
            "customBone16", "customBone17", "customBone18", "customBone19",
            "customBone20", "customBone21", "customBone22", "customBone23",
            "customBone24", "customBone25", "customBone26", "customBone27",
            "customBone28", "customBone29", "iv_c_mune_l",
        };

        private static readonly Guid ActorLineage =
            Guid.Parse("7a3b7b42-7f67-4e49-9cc5-2d74c8aa6a01");

        private readonly SelectionSession _selection = new();
        private readonly HashSet<string> _hidden = new();
        private uint _actorGeneration = 1;
        private uint _primarySlotGeneration = 1;
        private uint _independentSlotGeneration = 1;
        private bool _showNsfw = true;
        private bool _dirty = true;
        private BoneMatrixViewModel? _vm;

        public int RebuildCount { get; private set; }

        public float Draw<TSink>(BoneMatrixClipRect clip, ref TSink sink)
            where TSink : struct, IBoneMatrixDrawSink
        {
            if (_dirty || _vm == null)
            {
                _vm = Build();
                RebuildCount++;
                _dirty = false;
            }

            BoneMatrixBuilder.SyncSelection(_vm, _selection);
            return BoneMatrixView.DrawForTesting(
                _vm, Vector2.Zero, 240f, 1f, clip, ref sink);
        }

        public void Select(SelectionId id) => _selection.Select(id);

        public void HideBone(string canonicalName)
        {
            _hidden.Add(canonicalName);
            _dirty = true;
        }

        public void ToggleNsfw(bool show)
        {
            _showNsfw = show;
            _dirty = true;
        }

        public void ReplaceIndependentSlotGeneration(uint generation) =>
            _independentSlotGeneration = generation;

        public void ReplacePrimarySlotGeneration(uint generation)
        {
            _primarySlotGeneration = generation;
            _dirty = true;
        }

        public void ReplaceGenerations(uint actor, uint skeleton)
        {
            _actorGeneration = actor;
            _primarySlotGeneration = skeleton;
            _dirty = true;
        }

        private BoneMatrixViewModel Build()
        {
            var primary = MakeSkeleton(PoseSlot.Character, _primarySlotGeneration);
            _ = MakeSkeleton(PoseSlot.MainHand, _independentSlotGeneration);
            return BoneMatrixBuilder.Build(
                primary,
                _selection,
                static (_, _, _) => { },
                static (_, _) => { },
                showNsfwBones: _showNsfw);
        }

        private SkeletonDescriptor MakeSkeleton(PoseSlot slot, uint generation)
        {
            var actor = new ActorId(ActorLineage, _actorGeneration);
            var skeleton = new SkeletonId(actor, slot, generation);
            var bones = Names.Select((name, index) =>
            {
                int partial = index < 2 ? 0 : index;
                int boneIndex = index < 2 ? 0 : index;
                return new BoneDescriptor(
                    new BoneId(skeleton, partial, boneIndex, name),
                    name,
                    Parent: null,
                    IsHidden: _hidden.Contains(name));
            }).ToList();
            return new SkeletonDescriptor(skeleton, bones);
        }
    }

    private readonly record struct DrawResult(
        float Height,
        BoneMatrixClipRect Clip,
        List<Emission> Emissions,
        List<string> PillIds,
        List<string> SelectedPillIds)
    {
        public Emission Pill(string canonicalName) =>
            Emissions.Single(item => item.Kind == "pill" && item.CanonicalName == canonicalName);
    }

    private readonly record struct Emission(
        string Kind,
        string Id,
        BoneMatrixGeometry Geometry,
        BoneMatrixClipRect Clip,
        bool Selected = false,
        string CanonicalName = "",
        SelectionId? SelectionId = null)
    {
        public string SemanticKey => Kind == "divider"
            ? $"divider:{Geometry.Min.Y:R}"
            : $"{Kind}:{Id}";
    }

    private struct CountingSink : IBoneMatrixDrawSink
    {
        public CountingSink(BoneMatrixClipRect clip)
        {
            Clip = clip;
            Emissions = new List<Emission>();
        }

        public BoneMatrixClipRect Clip { get; }
        public List<Emission> Emissions { get; }

        public void DrawSection(BoneMatrixSection section, BoneMatrixGeometry geometry) =>
            Emissions.Add(new Emission("section", section.Id, geometry, Clip));

        public void DrawDivider(BoneMatrixGeometry geometry) =>
            Emissions.Add(new Emission("divider", "divider", geometry, Clip));

        public void DrawRow(BoneMatrixRow row, BoneMatrixGeometry geometry) =>
            Emissions.Add(new Emission("row", row.Label, geometry, Clip));

        public void DrawPill(BoneMatrixPill pill, BoneMatrixGeometry geometry)
        {
            var tag = (BoneMatrixBuilder.MatrixPillTag)pill.Tag!;
            Emissions.Add(new Emission(
                "pill", pill.Id, geometry, Clip, pill.Selected,
                tag.CanonicalName, tag.Id));
        }
    }
}
