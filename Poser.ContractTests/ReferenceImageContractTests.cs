extern alias ProductionPoser;

using System.Numerics;
using Newtonsoft.Json;
using Poser.Config;
using ProductionPoser::Poser.UI;

namespace Poser.ContractTests;

/// <summary>
/// The reference-image feature's four invariants, each of which a reference
/// implementation gets wrong:
///
/// <list type="bullet">
/// <item><b>Aspect lock.</b> The window's ratio IS the picture's, and the axis
/// the pointer actually moved drives the resolve — Ktisis always derives
/// height from width, so its windows cannot be resized by their bottom edge.
/// </item>
/// <item><b>Opacity floor.</b> A reference window can be made translucent but
/// never invisible; Ktisis' slider reaches zero, which leaves an
/// unclosable pointer-eating rectangle over the game.</item>
/// <item><b>Minted identity.</b> Brio derives its entity id from
/// <c>path.GetHashCode()</c>, so adding the same file twice collides.</item>
/// <item><b>Persistence.</b> The roster is config, so it survives GPose exit
/// and a plugin reload; Brio drops every picture on GPose exit.</item>
/// </list>
/// </summary>
public sealed class ReferenceImageContractTests
{
    // ── aspect lock ──────────────────────────────────────────────────────

    [Fact]
    public void A_conformant_size_resolves_to_itself()
    {
        var size = new Vector2(800f, 400f);
        Assert.Equal(
            size,
            ReferenceImageGeometry.ResolveAspect(size, size, 2f));
    }

    [Fact]
    public void A_width_drag_drives_the_height()
    {
        var resolved = ReferenceImageGeometry.ResolveAspect(
            previous: new Vector2(800f, 400f),
            requested: new Vector2(1000f, 400f),
            aspect: 2f);

        Assert.Equal(1000f, resolved.X, 3);
        Assert.Equal(500f, resolved.Y, 3);
    }

    /// <summary>The slice Ktisis' callback cannot express: its
    /// <c>DesiredSize.Y = DesiredSize.X / Ratio</c> pins height to width, so a
    /// bottom-edge drag is discarded.</summary>
    [Fact]
    public void A_height_drag_drives_the_width()
    {
        var resolved = ReferenceImageGeometry.ResolveAspect(
            previous: new Vector2(800f, 400f),
            requested: new Vector2(800f, 300f),
            aspect: 2f);

        Assert.Equal(600f, resolved.X, 3);
        Assert.Equal(300f, resolved.Y, 3);
    }

    [Fact]
    public void The_axis_that_moved_further_wins_a_corner_drag()
    {
        var previous = new Vector2(800f, 400f);
        // Height moved 150, width moved 20: the height leads.
        var resolved = ReferenceImageGeometry.ResolveAspect(
            previous, new Vector2(820f, 550f), 2f);

        Assert.Equal(1100f, resolved.X, 3);
        Assert.Equal(550f, resolved.Y, 3);
    }

    [Fact]
    public void The_floor_never_breaks_the_ratio()
    {
        var resolved = ReferenceImageGeometry.ResolveAspect(
            previous: new Vector2(800f, 400f),
            requested: new Vector2(4f, 400f),
            aspect: 2f,
            minimumSide: 100f);

        Assert.Equal(200f, resolved.X, 3);
        Assert.Equal(100f, resolved.Y, 3);
        Assert.Equal(2f, resolved.X / resolved.Y, 3);
    }

    /// <summary>A tall picture floors on its own short side — the width —
    /// rather than on whichever axis the caller happened to state.</summary>
    [Fact]
    public void The_floor_applies_to_the_short_side()
    {
        var resolved = ReferenceImageGeometry.ResolveAspect(
            previous: new Vector2(200f, 800f),
            requested: new Vector2(20f, 800f),
            aspect: 0.25f,
            minimumSide: 100f);

        Assert.Equal(100f, resolved.X, 3);
        Assert.Equal(400f, resolved.Y, 3);
    }

    /// <summary>No picture has resolved yet: the requested size passes through
    /// rather than collapsing to zero or dividing by it.</summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    public void An_unresolved_picture_leaves_the_size_alone(float aspect)
    {
        var requested = new Vector2(640f, 111f);
        Assert.Equal(
            requested,
            ReferenceImageGeometry.ResolveAspect(
                new Vector2(200f, 200f), requested, aspect));
    }

    [Fact]
    public void A_picture_larger_than_its_share_of_the_viewport_shrinks_to_fit()
    {
        var seated = ReferenceImageGeometry.InitialSize(
            pixels: new Vector2(4000f, 2000f),
            viewport: new Vector2(2000f, 1000f),
            viewportShare: 0.5f);

        Assert.Equal(1000f, seated.X, 3);
        Assert.Equal(500f, seated.Y, 3);
    }

    [Fact]
    public void A_small_picture_is_seated_at_its_own_pixels_and_never_grown()
    {
        var seated = ReferenceImageGeometry.InitialSize(
            pixels: new Vector2(320f, 200f),
            viewport: new Vector2(2560f, 1440f));

        Assert.Equal(320f, seated.X, 3);
        Assert.Equal(200f, seated.Y, 3);
    }

    // ── opacity floor ────────────────────────────────────────────────────

    [Theory]
    [InlineData(0f)]
    [InlineData(-4f)]
    [InlineData(0.01f)]
    [InlineData(0.2499f)]
    public void Opacity_can_never_go_under_the_floor(float requested)
    {
        Assert.Equal(
            ReferenceImageConfiguration.MinimumOpacity,
            ReferenceImageConfiguration.ClampOpacity(requested));
    }

    [Fact]
    public void Opacity_between_the_floor_and_one_is_kept_verbatim()
    {
        Assert.Equal(
            0.6f, ReferenceImageConfiguration.ClampOpacity(0.6f), 5);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(4f)]
    public void Opacity_never_exceeds_one(float requested)
    {
        Assert.Equal(1f, ReferenceImageConfiguration.ClampOpacity(requested));
    }

    [Fact]
    public void A_stored_opacity_under_the_floor_is_raised_on_read()
    {
        // What a config hand-edited, or written by a build without the floor,
        // deserialises to.
        Assert.Equal(
            ReferenceImageConfiguration.MinimumOpacity,
            ReferenceImageConfiguration.ClampOpacity(0f));
        Assert.Equal(
            1f, ReferenceImageConfiguration.ClampOpacity(float.NaN));
    }

    // ── minted identity ──────────────────────────────────────────────────

    /// <summary>Brio's <c>ref_image_{path.GetHashCode()}</c> collides here;
    /// the same sheet twice is a legitimate pair of references.</summary>
    [Fact]
    public void The_same_file_added_twice_is_two_pictures()
    {
        var roster = new ReferenceImageConfiguration();
        var first = roster.Add(@"C:\refs\pose.png");
        var second = roster.Add(@"C:\refs\pose.png");

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, roster.Images.Count);
        Assert.Equal(first.FilePath, second.FilePath);
    }

    [Fact]
    public void A_closed_picture_never_frees_its_id_for_reuse()
    {
        var roster = new ReferenceImageConfiguration();
        var first = roster.Add(@"C:\refs\a.png");
        var second = roster.Add(@"C:\refs\b.png");
        Assert.True(roster.Remove(second.Id));

        var third = roster.Add(@"C:\refs\c.png");
        Assert.NotEqual(first.Id, third.Id);
        Assert.NotEqual(second.Id, third.Id);
    }

    /// <summary>A roster whose counter lagged behind its entries — a config
    /// hand-edited, or merged — still mints something no entry holds.
    /// </summary>
    [Fact]
    public void A_lagging_counter_cannot_mint_a_live_id()
    {
        var roster = new ReferenceImageConfiguration
        {
            NextId = 1,
            Images =
            {
                new ReferenceImageEntry { Id = 7, FilePath = @"C:\a.png" },
                new ReferenceImageEntry { Id = 3, FilePath = @"C:\b.png" },
            },
        };

        Assert.Equal(8, roster.Add(@"C:\c.png").Id);
    }

    [Fact]
    public void A_picture_is_named_for_its_file()
    {
        Assert.Equal(
            "front-sheet",
            new ReferenceImageConfiguration()
                .Add(@"C:\refs\front-sheet.PNG").Name);
        Assert.Equal(
            "Reference", ReferenceImageConfiguration.NameFor(string.Empty));
    }

    // ── persistence ──────────────────────────────────────────────────────

    /// <summary>
    /// The whole roster survives the plugin config's own serializer — the
    /// scope Ktisis uses and Brio has none of. Placement rides along, which
    /// Ktisis leaves to ImGui's ini and therefore loses.
    /// </summary>
    [Fact]
    public void The_roster_survives_a_config_round_trip()
    {
        var before = new PoserConfiguration();
        var entry = before.ReferenceImages.Add(@"C:\refs\front.png");
        entry.Opacity = 0.4f;
        entry.X = 120f;
        entry.Y = 64f;
        entry.Width = 800f;
        entry.Height = 450f;
        before.ReferenceImages.Add(@"C:\refs\front.png");

        var after = JsonConvert.DeserializeObject<PoserConfiguration>(
            JsonConvert.SerializeObject(before));

        Assert.NotNull(after);
        var roster = after!.ReferenceImages;
        Assert.Equal(2, roster.Images.Count);
        Assert.Equal(before.ReferenceImages.NextId, roster.NextId);

        var restored = roster.Images[0];
        Assert.Equal(entry.Id, restored.Id);
        Assert.Equal(@"C:\refs\front.png", restored.FilePath);
        Assert.Equal("front", restored.Name);
        Assert.Equal(0.4f, restored.Opacity, 5);
        Assert.Equal(120f, restored.X, 5);
        Assert.Equal(64f, restored.Y, 5);
        Assert.Equal(800f, restored.Width, 5);
        Assert.Equal(450f, restored.Height, 5);
        Assert.NotEqual(roster.Images[0].Id, roster.Images[1].Id);
    }

    /// <summary>A config written before the feature existed deserialises to an
    /// empty roster rather than null — which is why the addition needs no
    /// migration step.</summary>
    [Fact]
    public void A_config_written_before_the_feature_restores_an_empty_roster()
    {
        var after = JsonConvert.DeserializeObject<PoserConfiguration>(
            "{\"Version\":3}");

        Assert.NotNull(after);
        Assert.NotNull(after!.ReferenceImages);
        Assert.Empty(after.ReferenceImages.Images);
        Assert.Equal(1, after.ReferenceImages.Add(@"C:\a.png").Id);
    }
}
