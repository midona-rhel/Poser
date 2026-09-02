using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Core;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;
using Poser.Entities;
using Poser.Domain.Companions;
using Poser.Services;
using Poser.UI.Controls;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>Ktisis's bone categories: how a skeleton's bones fold into the sidebar tree.</summary>
public partial class MainWindow
{
    private static bool KtisisCategoryLabelMatches(string filter)
    {
        if (_ktisisLabels == null)
        {
            var labels = new List<string>();
            void Walk(Core.BoneInfo.KtisisBoneCategory category)
            {
                labels.Add(category.Label);
                foreach (var child in category.Children)
                    Walk(child);
            }
            foreach (var root in Core.BoneInfo.KtisisBoneCategories.Roots)
                Walk(root);
            _ktisisLabels = labels.ToArray();
        }
        foreach (var label in _ktisisLabels)
            if (MatchesSidebarFilter(filter, label))
                return true;
        return false;
    }

    /// <summary>One category, pruned to what the skeleton carries and
    /// what the filter keeps: its own present bones (all of them when the
    /// category label matched, the matching ones otherwise) and its surviving
    /// children. Null when nothing below survives.</summary>
    private sealed record BuiltCategory(
        string Id,
        string Label,
        List<BoneDescriptor> VisibleBones,
        List<BoneDescriptor> AllBones,
        List<BuiltCategory> Children);

    private BuiltCategory? BuildKtisisCategory(
        Core.BoneInfo.KtisisBoneCategory category,
        Dictionary<string, (BoneDescriptor Bone, int Ordinal)> byName,
        HashSet<string> claimed,
        string filter,
        bool filtering)
    {
        var claimedHere = new List<(BoneDescriptor Bone, int Ordinal)>();
        foreach (var name in category.Bones)
            if (byName.TryGetValue(name, out var entry) && claimed.Add(name))
                claimedHere.Add(entry);
        claimedHere.Sort(static (a, b) => a.Ordinal - b.Ordinal);
        var all = new List<BoneDescriptor>(claimedHere.Count);
        foreach (var (bone, _) in claimedHere)
            all.Add(bone);

        bool categoryMatches = filtering
            && MatchesSidebarFilter(filter, category.Label, category.Id);
        var visible = !filtering || categoryMatches
            ? all
            : all.FindAll(bone => MatchesSidebarFilter(
                filter, bone.DisplayName, bone.Id.CanonicalName));

        var children = new List<BuiltCategory>();
        foreach (var child in category.Children)
            if (BuildKtisisCategory(child, byName, claimed, filter, filtering)
                is { } present)
                children.Add(present);

        // A pruned node: nothing of this skeleton lives here, or the filter
        // kept none of it.
        if (children.Count == 0
            && (filtering ? visible.Count == 0 : all.Count == 0))
            return null;
        var built = new BuiltCategory(
            category.Id, category.Label, visible, all, children);
        RehomeWrist(built);
        return built;
    }

    /// <summary>All bone ids under a built category, for the row's overlay
    /// eye.</summary>
    private static void CollectCategoryBones(
        BuiltCategory category, List<BoneId> into)
    {
        foreach (var bone in category.AllBones)
            into.Add(bone.Id);
        foreach (var child in category.Children)
            CollectCategoryBones(child, into);
    }

    /// <summary>Returns the root bone name for a category.</summary>
    private static string? CategoryRootBone(string categoryId) => categoryId switch
    {
        "Head" => "j_kao",
        "Spine" => "j_kosi",
        "LeftArm" => "j_ude_a_l",
        "RightArm" => "j_ude_a_r",
        "LeftHand" => "j_te_l",
        "RightHand" => "j_te_r",
        "LeftLeg" => "j_asi_a_l",
        "RightLeg" => "j_asi_a_r",
        "Tail" => "n_sippo_a",
        "Hair" => "j_kami_a",
        "LeftEye" => "j_f_eye_l",
        "RightEye" => "j_f_eye_r",
        "Mouth" => "j_ago",
        _ => null,
    };

    internal static BoneDescriptor? ResolveCategoryBone(
        string categoryId,
        IReadOnlyList<BoneDescriptor> bones)
    {
        var rootName = CategoryRootBone(categoryId);
        return rootName == null
            ? null
            : bones.FirstOrDefault(
                bone => string.Equals(
                    bone.Id.CanonicalName, rootName,
                    StringComparison.Ordinal));
    }

    internal static BoneDescriptor? ResolveCharacterRootBone(
        IReadOnlyList<BoneDescriptor> bones) =>
        bones.FirstOrDefault(
            bone => string.Equals(
                bone.Id.CanonicalName, "n_hara",
                StringComparison.Ordinal));

    internal static BoneId[] NonOverlappingBoneTargets(
        IReadOnlyList<BoneDescriptor> candidates)
    {
        var parents = candidates.ToDictionary(
            bone => bone.Id, bone => bone.Parent);
        var selected = candidates.Select(bone => bone.Id).ToHashSet();
        return candidates
            .Where(bone =>
            {
                var parent = bone.Parent;
                while (parent is { } ancestor)
                {
                    if (selected.Contains(ancestor))
                        return false;
                    if (!parents.TryGetValue(ancestor, out parent))
                        break;
                }
                return true;
            })
            .Select(bone => bone.Id)
            .Distinct()
            .ToArray();
    }

    private static void RehomeWrist(BuiltCategory category)
    {
        var wristName = category.Id switch
        {
            "LeftArm" => "n_hte_l",
            "RightArm" => "n_hte_r",
            _ => null,
        };
        if (wristName == null)
            return;

        var hand = category.Children.Find(child =>
            child.Id is "LeftHand" or "RightHand");
        if (hand == null)
            return;

        MoveWrist(hand.AllBones, category.AllBones, wristName);
        MoveWrist(hand.VisibleBones, category.VisibleBones, wristName);
    }

    private static void MoveWrist(
        List<BoneDescriptor> from,
        List<BoneDescriptor> to,
        string wristName)
    {
        var wrist = from.Find(bone => string.Equals(
            bone.Id.CanonicalName, wristName, StringComparison.Ordinal));
        if (wrist == null)
            return;
        from.Remove(wrist);
        to.Add(wrist);
    }

    private static BoneId[] ResolveGroupSelectionBones(BuiltCategory category)
    {
        var candidates = new List<BoneDescriptor>();
        void Collect(BuiltCategory current)
        {
            candidates.AddRange(current.AllBones);
            foreach (var child in current.Children)
                Collect(child);
        }
        Collect(category);
        return NonOverlappingBoneTargets(candidates);
    }

    /// <summary>Removes the redundant prefix from an IVCS bone label.</summary>
    private static string PruneIvcsLead(string label) =>
        label.StartsWith("IVCS ", StringComparison.Ordinal)
            ? label["IVCS ".Length..]
            : label;

    private void EmitKtisisCategory(
        ShellSidebarSection section,
        BuiltCategory category,
        string parentKey,
        int depth,
        bool[]? lines,
        bool isLast,
        bool filtering,
        bool underIvcs = false)
    {
        // A child category under an IVCS ancestor drops its own "IVCS" too:
        // "Genitals IVCS > Penis", not "> Penis IVCS".
        string categoryLabel = underIvcs
            ? category.Label
                .Replace(" IVCS", "", StringComparison.Ordinal)
                .Replace("IVCS ", "", StringComparison.Ordinal)
            : category.Label;
        underIvcs = underIvcs
            || category.Label.Contains("IVCS", StringComparison.Ordinal);
        var catKey = parentKey + "/kcat:" + category.Id;
        if (_knownCategoryNodes.Add(catKey))
            _collapsedNodes.Add(catKey);
        bool expanded = filtering || !_collapsedNodes.Contains(catKey);
        var overlayBones = new List<BoneId>();
        CollectCategoryBones(category, overlayBones);

        var mergedBone = ResolveCategoryBone(
            category.Id, category.AllBones);
        var selectionIds = mergedBone == null
            ? ResolveGroupSelectionBones(category)
            : [];
        section.Rows.Add(new ShellSidebarRow
        {
            Label = categoryLabel,
            Count = "",
            Depth = depth,
            HasChildren = true,
            Expanded = expanded,
            IsLastChild = isLast,
            TreeLines = lines,
            Active = mergedBone != null
                ? _selection.IsSelected(SelectionId.ForBone(mergedBone.Id))
                : selectionIds.Any(id =>
                    _selection.IsSelected(SelectionId.ForBone(id))),
            Tag = mergedBone != null
                ? SelectionId.ForBone(mergedBone.Id)
                : selectionIds.Length > 0
                    ? SelectionId.ForBoneGroup(
                        selectionIds[0].Skeleton.Actor, category.Id)
                    : catKey,
            ExpandKey = catKey,
            OverlayMemoryKey = catKey,
            SelectionBones = mergedBone == null && selectionIds.Length > 0
                ? selectionIds
                : null,
            OverlayBones = overlayBones.ToArray(),
        });
        if (!expanded)
            return;

        var childLines = Descend(lines ?? [], isLast);
        var bones = mergedBone == null
            ? category.VisibleBones
            : category.VisibleBones.FindAll(
                bone => !bone.Id.Equals(mergedBone.Id));

        // Preserve category ordering from the pose builder.
        // bones (SkeletonNode.OrderByPriority), and bones bind flat in
        // skeleton index order (BindBones: SortPriority = base + BoneIndex).
        for (int c = 0; c < category.Children.Count; c++)
            EmitKtisisCategory(
                section, category.Children[c], catKey, depth + 1, childLines,
                c == category.Children.Count - 1 && bones.Count == 0,
                filtering, underIvcs);

        for (int b = 0; b < bones.Count; b++)
        {
            var boneSelectionId = SelectionId.ForBone(bones[b].Id);
            section.Rows.Add(new ShellSidebarRow
            {
                Label = underIvcs
                    ? PruneIvcsLead(bones[b].DisplayName)
                    : bones[b].DisplayName,
                Count = "",
                Depth = depth + 1,
                IsLastChild = b == bones.Count - 1,
                TreeLines = childLines,
                Active = _selection.IsSelected(boneSelectionId),
                Tag = boneSelectionId,
                OverlayBones = new[] { bones[b].Id },
            });
        }
    }
}
