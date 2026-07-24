using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Poser.Core.BoneInfo;
using Poser.Files;
using Poser.Entities;
using Poser.Services;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>
/// Builds the Anamnesis-style bone matrix from a live skeleton. Instead of a
/// hand-transcribed bone table (which would be wrong for Viera ears, tails,
/// IVCS, modded skeletons), rows are DERIVED from the skeleton's actual bones
/// via the curated BoneInfoService data: bones cluster on their base name
/// (side/number suffixes stripped), the row label is the shared curated
/// translation, pills are the concrete bones (L/R/numbered). Sections follow
/// the curated category/subcategory grouping — same information as Anamnesis
/// PoseMatrixView, generated instead of transcribed.
/// </summary>
public static class BoneMatrixBuilder
{
    // j_f_mabup_02out_l → base "j_f_mabup", num "02", side "l"
    // j_sippo_a         → base "j_sippo", letter "a"
    private static readonly Regex Suffix = new(
        @"^(?<base>.+?)(?:_(?<num>\d+)(?<numsub>[a-z]*))?(?:_(?<let>[a-e]))?(?:_(?<side>[lr]))?$",
        RegexOptions.Compiled);

    private static readonly Regex LabelTail = new(@"\s+([A-E]|\d+)$", RegexOptions.Compiled);

    private sealed record PillInfo(IBone Bone, string Side, string Ordinal);

    public static BoneMatrixViewModel Build(
        ISkeleton skeleton,
        Action<IBone, bool, bool> onBone,
        Action<IReadOnlyList<IBone>, bool> onGroup,
        string? filter = null)
    {
        var vm = new BoneMatrixViewModel
        {
            OnPill = (pill, additive, range) => onBone((IBone)pill.Tag!, additive, range),
            OnSection = (section, additive) => onGroup(
                section.Rows.SelectMany(row => row.Pills)
                    .Select(pill => (IBone)pill.Tag!)
                    .ToList(),
                additive),
        };

        // ── authoritative layout: the Anamnesis PoseMatrixView table.
        // Aliases resolve through the ported name converter; rows whose bones
        // aren't in THIS skeleton vanish (race variants dedupe naturally).
        var covered = new HashSet<(string BoneName, int PartialId)>();
        BoneMatrixSection? section = null;
        foreach (var row in AnamnesisMatrixTable.Rows)
        {
            var pills = new List<BoneMatrixPill>();
            foreach (var (name, pillLabel) in row.Pills)
            {
                var gameName = AnamnesisBoneNameConverter.ToGame(name);
                var matches = skeleton.Bones.Where(bone =>
                    !bone.IsHiddenBone &&
                    (bone.BoneName == gameName || bone.BoneName == name));
                foreach (var bone in matches)
                {
                    covered.Add((bone.BoneName, bone.PartialId));
                    string label = pillLabel;
                    if (pills.Any(pill => pill.Label == label))
                        label = label.Length == 0 ? $"P{bone.PartialId}" : $"{label}{bone.PartialId}";
                    pills.Add(new BoneMatrixPill { Label = label, Selected = bone.IsSelected, Tag = bone });
                }
            }
            if (pills.Count == 0)
                continue;

            if (section == null || section.Title != row.Section.ToUpperInvariant())
            {
                section = new BoneMatrixSection { Title = row.Section.ToUpperInvariant() };
                vm.Sections.Add(section);
            }
            var matrixRow = new BoneMatrixRow { Label = row.Label };
            matrixRow.Pills.AddRange(pills);
            section.Rows.Add(matrixRow);
        }

        // ── trailing fallback: curated-but-uncovered bones, generated clustering
        AppendGenerated(vm, skeleton, covered);
        ApplyFilter(vm, filter);
        return vm;
    }

    private static void AppendGenerated(
        BoneMatrixViewModel vm,
        ISkeleton skeleton,
        HashSet<(string BoneName, int PartialId)> covered)
    {
        // cluster: (category, subcategory, base name) → pills
        var clusters = new Dictionary<(BoneCategory, BoneSubcategory, string), (string Label, List<PillInfo> Pills)>();
        var clusterOrder = new List<(BoneCategory, BoneSubcategory, string)>();

        foreach (var bone in skeleton.Bones)
        {
            if (bone.IsHiddenBone || covered.Contains((bone.BoneName, bone.PartialId))) continue;

            var data = BoneInfoService.GetBoneData(bone.BoneName);
            var category = data?.Category ?? BoneCategory.Other;
            var subcategory = data?.Subcategory ?? BoneSubcategory.None;

            var m = Suffix.Match(bone.BoneName);
            string baseName = m.Success ? m.Groups["base"].Value : bone.BoneName;
            string side = m.Groups["side"].Value;
            string ordinal = m.Groups["num"].Value.TrimStart('0');
            if (ordinal.Length == 0 && m.Groups["let"].Success)
                ordinal = (m.Groups["let"].Value[0] - 'a' + 1).ToString();

            string label = data != null
                ? LabelTail.Replace(data.Value.Translation, "")
                : bone.Name;

            var key = (category, subcategory, baseName);
            if (!clusters.TryGetValue(key, out var cluster))
            {
                cluster = (label, new List<PillInfo>());
                clusters[key] = cluster;
                clusterOrder.Add(key);
            }
            cluster.Pills.Add(new PillInfo(bone, side, ordinal));
        }

        // sections in curated order
        var sections = new Dictionary<(BoneCategory, BoneSubcategory), BoneMatrixSection>();
        foreach (var key in clusterOrder
                     .OrderBy(k => (int)k.Item1)
                     .ThenBy(k => (int)k.Item2))
        {
            var (category, subcategory, _) = key;
            var sectionKey = (category, subcategory);
            if (!sections.TryGetValue(sectionKey, out var section))
            {
                string title = "MORE — " + (subcategory != BoneSubcategory.None
                    ? BoneInfoService.GetSubcategoryDisplayName(subcategory)
                    : BoneInfoService.GetCategoryDisplayName(category));
                section = new BoneMatrixSection { Title = title.ToUpperInvariant() };
                sections[sectionKey] = section;
                vm.Sections.Add(section);
            }

            var (label, pills) = clusters[key];
            var row = new BoneMatrixRow { Label = label };
            foreach (var pill in pills
                         .OrderBy(p => p.Side == "l" ? 0 : p.Side == "r" ? 1 : 2)
                         .ThenBy(p => p.Ordinal.Length == 0 ? 0 : int.Parse(p.Ordinal)))
            {
                row.Pills.Add(new BoneMatrixPill
                {
                    Label = PillLabel(pill, pills),
                    Selected = pill.Bone.IsSelected,
                    Tag = pill.Bone,
                });
            }
            section.Rows.Add(row);
        }
    }

    public static IReadOnlyList<IBone> EnumerateBones(BoneMatrixViewModel vm) =>
        vm.Sections.SelectMany(section => section.Rows)
            .SelectMany(row => row.Pills)
            .Select(pill => (IBone)pill.Tag!)
            .ToList();

    private static void ApplyFilter(BoneMatrixViewModel vm, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return;

        filter = filter.Trim();
        foreach (var section in vm.Sections.ToArray())
        {
            foreach (var row in section.Rows.ToArray())
            {
                bool rowMatches = row.Label.Contains(filter, StringComparison.OrdinalIgnoreCase);
                if (!rowMatches)
                {
                    row.Pills.RemoveAll(pill =>
                        pill.Tag is not IBone bone ||
                        (!bone.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                         !bone.BoneName.Contains(filter, StringComparison.OrdinalIgnoreCase)));
                }

                if (row.Pills.Count == 0)
                    section.Rows.Remove(row);
            }

            if (section.Rows.Count == 0)
                vm.Sections.Remove(section);
        }
    }

    /// <summary>Refreshes only the Selected flags (cheap per-frame sync).</summary>
    public static void SyncSelection(BoneMatrixViewModel vm)
    {
        foreach (var section in vm.Sections)
            foreach (var row in section.Rows)
                foreach (var pill in row.Pills)
                    pill.Selected = pill.Tag is IBone { IsSelected: true };
    }

    private static string PillLabel(PillInfo pill, List<PillInfo> cluster)
    {
        bool anySide = cluster.Any(p => p.Side.Length > 0);
        bool anyOrdinal = cluster.Any(p => p.Ordinal.Length > 0);

        if (cluster.Count == 1) return "";
        string side = pill.Side.ToUpperInvariant();
        if (anySide && anyOrdinal) return side + pill.Ordinal;
        if (anySide) return side;
        return pill.Ordinal;
    }
}
