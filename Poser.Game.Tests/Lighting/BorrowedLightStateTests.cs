using System.Numerics;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Game.Lighting;
using Xunit;

namespace Poser.Game.Tests.Lighting;

public unsafe class BorrowedLightStateTests
{
    [Fact]
    public void Replacement_edits_leave_source_properties_alone_and_toggle_preserves_intensity()
    {
        LightRenderObject render = default;
        GameLight native = default;
        native.LightRenderObject = &render;
        native.Transform.Position = new(1, 2, 3);
        native.Transform.Rotation = Quaternion.CreateFromYawPitchRoll(.2f, .4f, .6f);
        native.Transform.Scale = new(2, 3, 4);
        native.VisibilityFlags = 17;
        render.LightFlags = (LightFlags)0x95;
        render.EmissionType = LightType.FlatLight;
        render.ColorIntensity = new(.2f, .3f, .4f, 7);
        render.ShadowPlaneNear = .2f; render.ShadowPlaneFar = 42;
        render.FalloffType = FalloffType.Cubic; render.AreaAngle = new(.3f, .7f);
        render.Falloff = .6f; render.LightAngle = 90; render.FalloffAngle = 25;
        render.Range = 65; render.CharacterShadowRange = 70;
        var originalTransform = native.Transform;
        var originalRender = render;
        using var state = new BorrowedLightState(&native);
        LightRenderObject copyRender = default;
        GameLight copy = default;
        copy.LightRenderObject = &copyRender;
        state.CopyTo(&copy);
        var light = new Light(&copy, "Borrowed", LightOwnership.World);
        Assert.True(light.NativePtr == &copy);
        Assert.True(copyRender.Transform == &copy.Transform);
        Assert.Equal(originalRender.ColorIntensity, copyRender.ColorIntensity);
        Assert.Equal(originalRender.AreaAngle, copyRender.AreaAngle);
        Assert.Equal(originalRender.LightFlags, copyRender.LightFlags);
        Assert.Equal(originalRender.ColorIntensity, render.ColorIntensity);
        Assert.Equal((byte)17, native.VisibilityFlags);
        light.Transform = Poser.Transform.Identity;
        light.Kind = LightKind.Spot;
        light.Color = Vector3.One;
        light.Intensity = 10;
        light.Range = 10;
        light.Falloff = 1;
        light.FalloffType = LightFalloffType.Linear;
        light.AreaAngle = new(20, 30);
        light.SpotAngle = 20;
        light.FalloffAngle = 10;
        light.CharacterShadowRange = 15;
        light.ShadowPlaneNear = 1;
        light.ShadowPlaneFar = 5;
        light.HasReflection = false;
        light.CastsObjectShadow = true;
        state.Suppress(&native);
        Assert.False(native.IsVisible);
        light.IsOn = false;
        Assert.False(light.IsOn);
        Assert.Equal(10f, copyRender.Intensity);
        Assert.Equal(7f, render.Intensity);
        light.Intensity = 12;
        light.IsOn = true;
        Assert.True(light.IsOn);
        Assert.Equal(12f, copyRender.Intensity);
        Assert.Equal(7f, render.Intensity);
        Assert.True(state.Restore(&native));
        Assert.Equal(originalTransform.Position, native.Transform.Position);
        Assert.Equal(originalTransform.Rotation, native.Transform.Rotation);
        Assert.Equal(originalTransform.Scale, native.Transform.Scale);
        Assert.Equal(originalRender.LightFlags, render.LightFlags);
        Assert.Equal(originalRender.EmissionType, render.EmissionType);
        Assert.Equal(originalRender.ColorIntensity, render.ColorIntensity);
        Assert.Equal(originalRender.AreaAngle, render.AreaAngle);
        Assert.Equal(originalRender.FalloffType, render.FalloffType);
        Assert.Equal(originalRender.Falloff, render.Falloff);
        Assert.Equal(originalRender.LightAngle, render.LightAngle);
        Assert.Equal(originalRender.FalloffAngle, render.FalloffAngle);
        Assert.Equal(originalRender.Range, render.Range);
        Assert.Equal(originalRender.CharacterShadowRange, render.CharacterShadowRange);
        Assert.Equal(originalRender.ShadowPlaneNear, render.ShadowPlaneNear);
        Assert.Equal(originalRender.ShadowPlaneFar, render.ShadowPlaneFar);
        native.Transform.Position = new(9, 8, 7);
        Assert.False(state.Restore(&native));
        Assert.Equal(new Vector3(9, 8, 7), (Vector3)native.Transform.Position);
    }

    [Fact]
    public void Replacement_gets_its_own_gobo_reference_without_taking_the_originals()
    {
        LightRenderObject render = default;
        GameLight native = default;
        native.LightRenderObject = &render;
        native.ProjectedCubemapTexture = (FFXIVClientStructs.FFXIV.Client.System.Resource.Handle.TextureResourceHandle*)123;
        render.Texture = (void*)456;
        var retained = new List<nint>();
        var released = new List<nint>();
        var state = new BorrowedLightState(&native, retained.Add, released.Add);
        Assert.Equal(new nint[] { 123 }, retained);
        LightRenderObject copyRender = default;
        GameLight copy = default;
        copy.LightRenderObject = &copyRender;
        state.CopyTo(&copy);
        Assert.Equal((nint)123, (nint)copy.ProjectedCubemapTexture);
        Assert.Equal((nint)456, (nint)copyRender.Texture);
        Assert.True(state.Restore(&native));
        state.Dispose();
        // The copy owns the retained reference now; its normal native cleanup
        // releases it. Restoring the source must not take that reference back.
        Assert.Empty(released);
        Assert.Equal((nint)123, (nint)native.ProjectedCubemapTexture);
        Assert.Equal((nint)456, (nint)render.Texture);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public void Release_restores_original_visibility_without_rewinding_game_updates(byte visibility)
    {
        LightRenderObject render = default;
        GameLight native = default;
        native.LightRenderObject = &render;
        native.VisibilityFlags = visibility;
        using var state = new BorrowedLightState(&native);
        state.Suppress(&native);
        render.Intensity = 4;
        native.Transform.Position = new(9, 8, 7);
        Assert.True(state.Restore(&native));
        Assert.Equal(visibility, native.VisibilityFlags);
        Assert.Equal(4, render.Intensity);
        Assert.Equal(new Vector3(9, 8, 7), (Vector3)native.Transform.Position);
    }

    [Fact]
    public void Failed_capture_releases_the_pending_texture_reference_once_without_touching_source()
    {
        LightRenderObject render = default;
        GameLight native = default;
        native.LightRenderObject = &render;
        render.Color = new(2, 3, 4);
        native.ProjectedCubemapTexture = (FFXIVClientStructs.FFXIV.Client.System.Resource.Handle.TextureResourceHandle*)123;
        var released = new List<nint>();
        var state = new BorrowedLightState(&native, _ => { }, released.Add);
        state.Dispose();
        state.Dispose();
        Assert.Equal(new Vector3(2, 3, 4), render.Color);
        Assert.Equal((nint)123, (nint)native.ProjectedCubemapTexture);
        Assert.Equal(new nint[] { 123 }, released);
        Assert.False(state.Restore(&native));
    }
}
