using System.Numerics;
using System.Reflection;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Entities;
using Poser.Services;

namespace Poser.Game.Tests.Posing;

public sealed class IkSceneTargetTests
{
    [Fact]
    public void Held_handle_translation_is_not_written_into_the_limb_before_solving()
    {
        var transform = new Transform
        {
            Position = new Vector3(2, 3, 4),
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.4f),
            Scale = new Vector3(0.1f),
        };
        var edit = new Poser.Core.BonePoseTransformInfo(Poser.Domain.Posing.TransformComponents.All, transform);
        var held = BonePosingService.HeldPoseStack(edit, true);
        Assert.Equal(Vector3.Zero, held.Transform.Position);
        Assert.Equal(transform.Rotation, held.Transform.Rotation);
        Assert.Equal(transform.Scale, held.Transform.Scale);
        Assert.Equal(edit, BonePosingService.HeldPoseStack(edit, false));
        var imported = edit with { IkTransform = Transform.Zero };
        Assert.Equal(imported, BonePosingService.HeldPoseStack(imported, true));
        Assert.Equal(transform.Position, edit.Transform.Position);
    }

    [Fact]
    public void Reads_the_live_scene_transform_and_refuses_removed_replaced_or_invalid_targets()
    {
        var lineage = Guid.NewGuid();
        var id = new LightId(lineage, 3);
        var selected = SelectionId.ForLight(id);
        var replacement = SelectionId.ForLight(new LightId(lineage, 4));
        var transform = new Transform
        {
            Position = new Vector3(1, 2, 3),
            Rotation = Quaternion.Identity,
            Scale = Vector3.One,
        };
        bool present = true;
        var bindings = Bind<ILight>(id, () => transform, () => present);

        Assert.Equal(transform.Position, BonePosingService.ResolveIkEntityTransform(bindings, selected)!.Value.Position);
        transform.Position += new Vector3(7, -1, 2);
        transform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f);
        var moved = BonePosingService.ResolveIkEntityTransform(bindings, selected)!.Value;
        Assert.Equal(transform.Position, moved.Position);
        Assert.Equal(transform.Rotation, moved.Rotation);
        Assert.Null(BonePosingService.ResolveIkEntityTransform(bindings, replacement));
        transform.Position = new Vector3(float.NaN);
        Assert.Null(BonePosingService.ResolveIkEntityTransform(bindings, selected));
        transform.Position = Vector3.Zero;
        present = false;
        Assert.Null(BonePosingService.ResolveIkEntityTransform(bindings, selected));
    }

    private static IEntityBindings Bind<T>(object id, Func<Transform> transform,
        Func<bool> present) where T : class
    {
        var live = Proxy<T>((method, _) => method.Name == "get_Transform" ? transform() : (object?)null);
        return Proxy<IEntityBindings>((method, args) =>
        {
            Assert.Equal("Resolve", method.Name);
            bool found = present() && Equals(id, args[0]);
            return new BindingResult<T>(found ? BindingStatus.Success : BindingStatus.StaleTarget,
                found ? live : null);
        });
    }

    private static T Proxy<T>(Func<MethodInfo, object?[], object?> invoke) where T : class
    {
        var proxy = DispatchProxy.Create<T, RuntimeProxy>();
        ((RuntimeProxy)(object)proxy).Handler = invoke;
        return proxy;
    }

    private class RuntimeProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Handler = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args!);
    }
}
