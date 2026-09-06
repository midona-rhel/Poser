using System.Numerics;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Game.Lighting;
using Xunit;

namespace Poser.Game.Tests.Lighting;

public unsafe class BorrowedLightStateTests
{
    [Fact]
    public void Borrowing_keeps_the_original_and_restores_all_editable_native_values()
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
        var light = new Light(&native, "Borrowed", LightOwnership.World);
        Assert.True(light.NativePtr == &native);
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
        light.IsOn = false;
        Assert.Equal(0f, render.Intensity);
        Assert.Equal((byte)17, native.VisibilityFlags);
        light.IsOn = true;
        Assert.Equal(1f, render.Intensity);
        Assert.Equal((byte)17, native.VisibilityFlags);
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

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Original_gobo_reference_is_retained_and_transferred_back_once(int edit)
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
        if (edit != 0)
        {
            // Native replacement already relinquished its original reference.
            native.ProjectedCubemapTexture = (FFXIVClientStructs.FFXIV.Client.System.Resource.Handle.TextureResourceHandle*)(edit == 1 ? 789 : 0);
            render.Texture = (void*)(edit == 1 ? 987 : 0);
        }
        Assert.True(state.Restore(&native));
        state.Dispose();
        Assert.Equal(edit == 2 ? Array.Empty<nint>() : new nint[] { edit == 1 ? 789 : 123 }, released);
        Assert.Equal((nint)123, (nint)native.ProjectedCubemapTexture);
        Assert.Equal((nint)456, (nint)render.Texture);
    }

    [Fact]
    public void Native_departure_drops_only_the_retained_resource_and_disables_the_old_wrapper()
    {
        LightRenderObject render = default;
        GameLight native = default;
        native.LightRenderObject = &render;
        render.Color = new(2, 3, 4);
        native.ProjectedCubemapTexture = (FFXIVClientStructs.FFXIV.Client.System.Resource.Handle.TextureResourceHandle*)123;
        var released = new List<nint>();
        var state = new BorrowedLightState(&native, _ => { }, released.Add);
        bool sameLifetime = true;
        var light = new Light(&native, "Borrowed", LightOwnership.World, () => sameLifetime);
        sameLifetime = false;
        Assert.False(light.IsValid);
        Assert.True(light.NativePtr == null);
        light.Color = Vector3.Zero;
        light.Transform = Poser.Transform.Identity;
        Assert.Equal(new Vector3(2, 3, 4), render.Color);
        state.Dispose();
        state.Dispose();
        Assert.Equal(new nint[] { 123 }, released);
        Assert.False(state.Restore(&native));
    }
}
