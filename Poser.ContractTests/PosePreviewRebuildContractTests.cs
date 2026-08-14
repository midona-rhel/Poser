extern alias ProductionPoser;

using System.Collections.Generic;
using System.Reflection;
using Poser.Domain.Identity;
using Poser.Files;
using ProductionPoser::Poser.UI;

namespace Poser.ContractTests;

/// <summary>
/// Characterization of the pose preview's change detection —
/// <c>PosePreviewBinder.NeedsRebuild</c>, which is the whole of
/// <c>Begin</c>'s verdict. There is ONE CharaView and a rebuild is a file
/// read plus two imports, so the compare has to be exact in BOTH directions:
/// under-reporting strands the preview on a pose the user has moved off
/// (the regression this suite exists for), and over-reporting re-imports
/// every frame, which supersedes the running sequence before it can land and
/// strands it just the same.
///
/// <para>The compare's stated invariant is that EVERY field of
/// <see cref="PoseImportOptions"/> participates. That is enforced here by
/// reflection rather than by a hand-written list, so a field added to the
/// options without being added to the compare fails this suite instead of
/// silently freezing the preview.</para>
/// </summary>
public sealed class PosePreviewRebuildContractTests
{
    private const string First = @"C:\poses\alpha.pose";
    private const string Second = @"C:\poses\beta.pose";

    [Fact]
    public void Nothing_shown_yet_owes_a_rebuild()
    {
        Assert.True(PosePreviewBinder.NeedsRebuild(
            shownPath: null,
            shownCandidate: null,
            First,
            new PoseImportOptions()));
    }

    [Fact]
    public void A_shown_path_with_no_candidate_owes_a_rebuild()
    {
        // The pair is written together and read together; half of it is not a
        // statement, and treating it as one would leave the body standing.
        Assert.True(PosePreviewBinder.NeedsRebuild(
            shownPath: First,
            shownCandidate: null,
            First,
            new PoseImportOptions()));
    }

    [Fact]
    public void Selecting_another_pose_owes_a_rebuild()
    {
        var options = new PoseImportOptions();
        Assert.True(PosePreviewBinder.NeedsRebuild(
            First, options, Second, options));
    }

    [Fact]
    public void Selecting_another_pose_owes_a_rebuild_under_equal_options()
    {
        // The options are rebuilt every frame, so the candidate arriving with
        // a new selection is a fresh instance whose CONTENT is identical. The
        // path alone has to carry the verdict.
        Assert.True(PosePreviewBinder.NeedsRebuild(
            First, new PoseImportOptions(), Second, new PoseImportOptions()));
    }

    [Fact]
    public void An_unmoved_pose_under_an_equal_rebuilt_candidate_owes_nothing()
    {
        // The poll runs every frame off a fresh build. If instance identity
        // leaked into the compare this would re-import at frame rate.
        Assert.False(PosePreviewBinder.NeedsRebuild(
            First, new PoseImportOptions(), First, new PoseImportOptions()));
    }

    [Fact]
    public void Path_compare_is_ordinal()
    {
        Assert.True(PosePreviewBinder.NeedsRebuild(
            First, new PoseImportOptions(),
            First.ToUpperInvariant(), new PoseImportOptions()));
    }

    [Fact]
    public void Every_boolean_option_moves_the_verdict()
    {
        foreach (var property in BooleanOptions())
        {
            var shown = new PoseImportOptions();
            var candidate = new PoseImportOptions();
            property.SetValue(
                candidate, !(bool)property.GetValue(shown)!);

            Assert.True(
                PosePreviewBinder.NeedsRebuild(First, shown, First, candidate),
                $"PoseImportOptions.{property.Name} is not compared, so the "
                + "preview cannot see it change.");
        }
    }

    [Fact]
    public void The_bone_filter_is_compared_by_content()
    {
        var shown = new PoseImportOptions
        {
            BoneFilter = Filter(("j_kosi", PoseSlot.Character)),
        };
        var equal = new PoseImportOptions
        {
            BoneFilter = Filter(("j_kosi", PoseSlot.Character)),
        };
        var moved = new PoseImportOptions
        {
            BoneFilter = Filter(("j_sebo_a", PoseSlot.Character)),
        };
        var widened = new PoseImportOptions
        {
            BoneFilter = Filter(
                ("j_kosi", PoseSlot.Character), ("j_sebo_a", PoseSlot.Character)),
        };

        Assert.False(PosePreviewBinder.NeedsRebuild(First, shown, First, equal));
        Assert.True(PosePreviewBinder.NeedsRebuild(First, shown, First, moved));
        Assert.True(PosePreviewBinder.NeedsRebuild(First, shown, First, widened));
        Assert.True(PosePreviewBinder.NeedsRebuild(
            First, shown, First, new PoseImportOptions()));
    }

    [Fact]
    public void The_excluded_prefixes_are_compared_by_content()
    {
        var shown = new PoseImportOptions
        {
            ExcludedBonePrefixes = new HashSet<string> { "j_ex_" },
        };
        var equal = new PoseImportOptions
        {
            ExcludedBonePrefixes = new HashSet<string> { "j_ex_" },
        };
        var moved = new PoseImportOptions
        {
            ExcludedBonePrefixes = new HashSet<string> { "j_kao" },
        };

        Assert.False(PosePreviewBinder.NeedsRebuild(First, shown, First, equal));
        Assert.True(PosePreviewBinder.NeedsRebuild(First, shown, First, moved));
    }

    [Fact]
    public void A_cloned_candidate_says_the_same_thing_as_its_source()
    {
        // Clone is the other total-field surface. A field it carries but the
        // compare ignores would strand the preview; a field it drops would
        // re-import forever.
        var shown = new PoseImportOptions
        {
            ApplyPosition = true,
            ApplyScale = true,
            ResetBeforeImport = true,
            FilterIncludesDescendants = true,
            AnchorSelectedPositions = true,
            ExcludeUncategorizedBones = true,
            BoneFilter = Filter(("j_kosi", PoseSlot.Character)),
            ExcludedBonePrefixes = new HashSet<string> { "j_ex_" },
        };

        Assert.False(
            PosePreviewBinder.NeedsRebuild(First, shown, First, shown.Clone()));
    }

    private static ISet<(PoseSlot Slot, string Name)> Filter(
        params (string Name, PoseSlot Slot)[] bones)
    {
        var set = new HashSet<(PoseSlot Slot, string Name)>();
        foreach (var (name, slot) in bones)
            set.Add((slot, name));
        return set;
    }

    private static IEnumerable<PropertyInfo> BooleanOptions()
    {
        foreach (var property in typeof(PoseImportOptions).GetProperties(
                     BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.PropertyType == typeof(bool)
                && property.CanRead
                && property.CanWrite)
                yield return property;
        }
    }
}
