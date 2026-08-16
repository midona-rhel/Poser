using Poser.UI;
using Dalamud.Interface.ManagedFontAtlas;
using NSubstitute;

namespace Poser.ContractTests;

public sealed class ThemeActivationContractTests : IDisposable
{
    public void Dispose()
    {
        FontRegistry.Dispose();
        Crystarium.UseTheme(Theme.PictoDark);
    }

    [Fact]
    public void Pending_atlas_keeps_the_previous_theme_visible_on_every_frame()
    {
        var activation = new ThemeActivation(Theme.PictoDark);
        bool atlasReady = false;

        activation.Request(Theme.PictoLight);

        var visibleFrames = new List<Theme>();
        for (int frame = 0; frame < 3; frame++)
        {
            activation.Advance(_ => atlasReady);
            visibleFrames.Add(activation.Active);
        }

        Assert.True(activation.HasPending);
        Assert.All(visibleFrames, theme => Assert.False(theme.IsLight));

        atlasReady = true;
        activation.Advance(_ => atlasReady);

        Assert.False(activation.HasPending);
        Assert.True(activation.Active.IsLight);
    }

    [Fact]
    public void Latest_pending_theme_activates_with_its_ready_atlas()
    {
        var activation = new ThemeActivation(Theme.PictoDark);
        bool atlasReady = false;

        activation.Request(Theme.PictoLight);
        activation.Request(Theme.PictoLightGray);
        atlasReady = true;
        activation.Advance(_ => atlasReady);

        Assert.False(activation.HasPending);
        Assert.Equal(Theme.PictoLightGray.Surface, activation.Active.Surface);
    }

    [Fact]
    public void Ready_request_waits_for_the_next_frame_boundary()
    {
        var activation = new ThemeActivation(Theme.PictoDark);

        activation.Request(Theme.PictoLight);

        Assert.False(activation.Active.IsLight);
        Assert.True(activation.HasPending);

        activation.Advance(_ => true);

        Assert.True(activation.Active.IsLight);
        Assert.False(activation.HasPending);
    }

    [Fact]
    public void Initial_async_registration_recovers_when_handles_become_ready()
    {
        FontRegistry.Dispose();
        Crystarium.UseTheme(Theme.PictoDark);
        bool handlesReady = false;
        var atlas = Substitute.For<IFontAtlas>();
        atlas.NewDelegateFontHandle(Arg.Any<FontAtlasBuildStepDelegate>())
            .Returns(_ => Handle(() => handlesReady));

        FontRegistry.Register(atlas);
        Assert.False(FontRegistry.Ready);

        handlesReady = true;
        Assert.True(FontRegistry.Ready);
    }

    [Fact]
    public void Ready_standby_swaps_without_rebuilding_and_disposes_both_sets()
    {
        FontRegistry.Dispose();
        Crystarium.UseTheme(Theme.PictoDark);
        var handles = new List<IFontHandle>();
        var atlas = Substitute.For<IFontAtlas>();
        atlas.NewDelegateFontHandle(Arg.Any<FontAtlasBuildStepDelegate>())
            .Returns(_ =>
            {
                var handle = Handle(() => true);
                handles.Add(handle);
                return handle;
            });

        FontRegistry.Register(atlas);
        Assert.True(FontRegistry.Ready);
        int warmHandleCount = handles.Count;

        Crystarium.UseTheme(Theme.PictoLight);
        Assert.False(Crystarium.ActiveTheme.IsLight);
        Assert.True(Crystarium.AdvanceTheme());
        Assert.True(Crystarium.ActiveTheme.IsLight);
        Assert.Equal(warmHandleCount, handles.Count);

        FontRegistry.Dispose();
        Assert.All(handles, handle => handle.Received(1).Dispose());
    }

    [Fact]
    public void Failed_handle_is_ready_for_the_existing_default_font_fallback()
    {
        FontRegistry.Dispose();
        Crystarium.UseTheme(Theme.PictoDark);
        bool first = true;
        var atlas = Substitute.For<IFontAtlas>();
        atlas.NewDelegateFontHandle(Arg.Any<FontAtlasBuildStepDelegate>())
            .Returns(_ =>
            {
                if (!first)
                    return Handle(() => true);
                first = false;
                return FailedHandle();
            });

        FontRegistry.Register(atlas);

        Assert.True(FontRegistry.Ready);
    }

    private static IFontHandle Handle(Func<bool> available)
    {
        var handle = Substitute.For<IFontHandle>();
        handle.Available.Returns(_ => available());
        return handle;
    }

    private static IFontHandle FailedHandle()
    {
        var handle = Substitute.For<IFontHandle>();
        handle.Available.Returns(false);
        handle.LoadException.Returns(new InvalidOperationException("font fallback"));
        return handle;
    }
}
