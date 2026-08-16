extern alias ProductionPoser;

using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Poser.ContractTests.Fixtures;
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
/// <see cref="PoseImportOptions"/> participates, and the whole settable
/// surface is held to it by reflection rather than by a hand-written list:
/// booleans are swept and flipped one at a time, and every option of any other
/// shape must be named by a fact of its own. A field added to the options
/// without being added to the compare fails this suite instead of silently
/// freezing the preview.</para>
/// </summary>
public sealed class PosePreviewRebuildContractTests
{
    private const string First = @"C:\poses\alpha.pose";
    private const string Second = @"C:\poses\beta.pose";



    [Fact]
    public void Selecting_another_pose_owes_a_rebuild()
    {
        var options = new PoseImportOptions();
        using var app = new PoseImportCaptureHarness();
        int sceneActors = app.Scene.Snapshot.Actors.Count;
        var preview = app.AddPreviewBody();

        Assert.Equal(sceneActors, app.Scene.Snapshot.Actors.Count);
        Assert.NotEqual(app.ActorId, preview.ActorId);
        Assert.True(PosePreviewBinder.NeedsRebuild(
            First, options, Second, options));
        Assert.False(PosePreviewBinder.NeedsRebuild(
            First, options, First, options));
    }





    /// <summary>
    /// The other half of "every field participates". The sweep above can only
    /// flip booleans; a property of any other shape has to be covered by a
    /// hand-written fact, so each one is named here. A NEW non-boolean property
    /// fails this until someone adds both the compare and its test — which is
    /// the whole point, since the sweep alone would have said nothing about it.
    /// </summary>




    private static ISet<(PoseSlot Slot, string Name)> Filter(
        params (string Name, PoseSlot Slot)[] bones)
    {
        var set = new HashSet<(PoseSlot Slot, string Name)>();
        foreach (var (name, slot) in bones)
            set.Add((slot, name));
        return set;
    }

    /// <summary>Every option a build can actually set — the surface
    /// <c>SameOptions</c> claims to compare in full. The static presets
    /// (Default, RestPose, …) are read-only and excluded by CanWrite.</summary>
    private static IEnumerable<PropertyInfo> SettableOptions() =>
        typeof(PoseImportOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite);

    private static IEnumerable<PropertyInfo> BooleanOptions() =>
        SettableOptions().Where(p => p.PropertyType == typeof(bool));
}
