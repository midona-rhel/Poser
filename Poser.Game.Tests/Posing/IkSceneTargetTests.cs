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

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Reads_each_live_scene_transform_and_refuses_removed_or_replaced_targets(int kind)
    {
        var lineage = Guid.NewGuid();
        var selected = Target(kind, lineage, 3);
        var replacement = Target(kind, lineage, 4);
        var transform = new Transform
        {
            Position = new Vector3(1, 2, 3),
            Rotation = Quaternion.Identity,
            Scale = Vector3.One,
        };
        bool present = true;
        var requested = new List<object>();
        var bindings = kind switch
        {
            0 => Bind<IPropHandle>(selected.Prop!.Value, () => transform, () => present, requested),
            1 => Bind<ILight>(selected.Light!.Value, () => transform, () => present, requested),
            _ => Bind<IWorldObject>(selected.WorldObject!.Value, () => transform, () => present, requested),
        };

        Assert.Equal(transform.Position, BonePosingService.ResolveIkEntityTransform(bindings, selected)!.Value.Position);
        transform.Position += new Vector3(7, -1, 2);
        transform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f);
        var moved = BonePosingService.ResolveIkEntityTransform(bindings, selected)!.Value;
        Assert.Equal(transform.Position, moved.Position);
        Assert.Equal(transform.Rotation, moved.Rotation);
        Assert.Null(BonePosingService.ResolveIkEntityTransform(bindings, replacement));
        present = false;
        Assert.Null(BonePosingService.ResolveIkEntityTransform(bindings, selected));
        Assert.Equal(4, requested.Count);
    }

    [Fact]
    public void Rejects_nonspatial_targets_without_resolving_any_native_object()
    {
        var bindings = Proxy<IEntityBindings>((_, _) => throw new InvalidOperationException("Unexpected lookup"));
        Assert.Null(BonePosingService.ResolveIkEntityTransform(bindings,
            SelectionId.ForCamera(new CameraId(Guid.NewGuid(), 1))));
    }

    [Fact]
    public void Rejects_invalid_native_transform()
    {
        var id = new LightId(Guid.NewGuid(), 1);
        var invalid = new Transform { Position = new Vector3(float.NaN), Rotation = Quaternion.Identity };
        var bindings = Bind<ILight>(id, () => invalid, () => true, new());
        Assert.Null(BonePosingService.ResolveIkEntityTransform(bindings, SelectionId.ForLight(id)));
        invalid.Position = Vector3.Zero;
        invalid.Rotation = default;
        Assert.Null(BonePosingService.ResolveIkEntityTransform(bindings, SelectionId.ForLight(id)));
    }

    [Theory]
    [InlineData(IkTargetMode.Actor)]
    [InlineData(IkTargetMode.World)]
    [InlineData(IkTargetMode.Bone)]
    [InlineData(IkTargetMode.Entity)]
    public void Target_modes_preserve_solver_configuration(IkTargetMode mode)
    {
        var config = IkChainConfig.DefaultsForChain(true) with { TargetMode = mode };
        Assert.Null(config.ValidateUndeclared());
        Assert.Equal(mode, config.Normalized().TargetMode);
    }

    private static SelectionId Target(int kind, Guid lineage, uint generation) => kind switch
    {
        0 => SelectionId.ForProp(new PropId(lineage, generation)),
        1 => SelectionId.ForLight(new LightId(lineage, generation)),
        _ => SelectionId.ForWorldObject(new WorldObjectId(lineage, generation)),
    };

    private static IEntityBindings Bind<T>(object id, Func<Transform> transform,
        Func<bool> present, List<object> requested) where T : class
    {
        var live = Proxy<T>((method, _) => method.Name == "get_Transform" ? transform() : null);
        return Proxy<IEntityBindings>((method, args) =>
        {
            Assert.Equal("Resolve", method.Name);
            requested.Add(args[0]!);
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
