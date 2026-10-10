using System.Numerics;
using System.Reflection;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Application.Transforms;
using Poser.Application.Viewport;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;
using Poser.Services;

namespace Poser.Application.Tests.Transforms;

public sealed class SelectionPlacementTests
{
    [Fact]
    public void Context_reset_and_move_target_the_clicked_entity_not_current_selection()
    {
        var f = new Fixture();
        var before = f.Values[f.B];
        var untouched = f.Values[f.A];
        Assert.True(f.Control.ResetComponent(f.B.ToSelectionId(), TransformOperation.Rotate).Success);
        Assert.Equal(before with { Rotation = Quaternion.Identity }, f.Values[f.B]);
        Assert.Equal(untouched, f.Values[f.A]);
        Assert.Equal(f.A.ToSelectionId(), Assert.Single(f.Selection.Selected));
        Assert.Equal(1, f.Writes);
        Assert.True(f.Control.ResetComponent(f.B.ToSelectionId(), TransformOperation.Scale).Success);
        Assert.Equal(before.Position, f.Values[f.B].Position);
        Assert.Equal(Vector3.One, f.Values[f.B].Scale);

        // Move to camera also targets exactly what was clicked.
        Assert.True(f.Control.MoveToCamera([f.B.ToSelectionId()]).Success);
        Assert.Equal(f.B, Assert.Single(f.GestureTargets));
        Assert.Equal(new Vector3(10, 0, 2.5f) - f.Values[f.B].Position, f.Delta.Translation);
        Assert.Equal(1, f.Commits);
        Assert.Equal(f.A.ToSelectionId(), Assert.Single(f.Selection.Selected));
    }

    [Fact]
    public void Locked_or_stale_targets_and_unsupported_components_refuse_before_write()
    {
        var f = new Fixture();
        f.Groups.Create("Locked", [f.A.ToSelectionId(), f.B.ToSelectionId()])!.Locked = true;
        Assert.False(f.Control.CanPlace(f.B.ToSelectionId()));
        Assert.False(f.Control.ResetComponent(f.B.ToSelectionId(), TransformOperation.Rotate).Success);
        Assert.False(f.Control.ResetComponent(SelectionId.ForProp(PropId.New()), TransformOperation.Scale).Success);
        Assert.Equal(0, f.Writes);
        var unlocked = new Fixture();
        Assert.False(unlocked.Control.ResetComponent(unlocked.B.ToSelectionId(), TransformOperation.Translate).Success);
        Assert.Equal(0, unlocked.Writes);
    }

    private sealed class Fixture
    {
        public readonly SelectionSession Selection = new();
        public readonly SceneGroups Groups = new();
        public readonly TransformTargetId A = TransformTargetId.ForProp(PropId.New());
        public readonly TransformTargetId B = TransformTargetId.ForProp(PropId.New());
        public readonly Dictionary<TransformTargetId, PoseTransform> Values = [];
        public SelectionPlacement Control { get; }
        public int Writes, Commits;
        public IReadOnlyList<TransformTargetId> GestureTargets = [];
        public TransformDelta Delta;
        public Fixture()
        {
            var scene = new SceneSession(Selection);
            scene.Refresh(new(1, [], [], [], [new(A.Prop!.Value, "Selected"), new(B.Prop!.Value, "Clicked")]));
            Selection.Select(A.ToSelectionId());
            Values[A] = new(new(3, 4, 5), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .5f), new(2));
            Values[B] = new(new(6, 7, 8), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .8f), new(3));
            var viewport = Proxy<IViewportReads>((name, args) => name == "GetModelTransform"
                ? Values.GetValueOrDefault((TransformTargetId)args[0]!) : null);
            var camera = Proxy<ICameraProjection>((name, _) => name switch
            {
                "GetCameraPosition" => new Vector3(10, 0, 0), "GetLookDirection" => Vector3.UnitZ,
                _ => throw new NotSupportedException(name),
            });
            var transforms = Proxy<ITransformFacade>((name, args) =>
            {
                switch (name)
                {
                    case "SetAbsolute": Values[(TransformTargetId)args[0]!] = (PoseTransform)args[1]!; Writes++; return GestureResult.Ok();
                    case "Begin": GestureTargets = (IReadOnlyList<TransformTargetId>)args[0]!; return GestureResult.Ok(TransformGestureId.New());
                    case "Update": Delta = (TransformDelta)args[1]!; return GestureResult.Ok();
                    case "Commit": Commits++; return GestureResult.Ok();
                    default: throw new NotSupportedException(name);
                }
            });
            Control = new(Selection, scene, Groups, viewport, camera, transforms);
        }
    }

    private static T Proxy<T>(Func<string, object?[], object?> call) where T : class
    {
        var result = DispatchProxy.Create<T, Port>();
        ((Port)(object)result).Call = call;
        return result;
    }
    public class Port : DispatchProxy
    {
        public Func<string, object?[], object?> Call = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Call(method!.Name, args!);
    }
}
