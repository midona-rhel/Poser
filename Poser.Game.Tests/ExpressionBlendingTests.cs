using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using NSubstitute;
using Poser.Application.Transforms;
using Poser.Core;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Entities;
using Poser.Game;
using Poser.Game.Posing;
using Poser.Application.Posing;
using Poser.Services;
using Xunit;

namespace Poser.Tests;

public sealed class ExpressionBlendingTests
{
    [Fact]
    public void Linked_gesture_restores_both_independent_values_in_one_step()
    {
        using var f = new Fixture();
        f.Service.SetWeight(f.Actor, "SmileL", -.2f);
        f.Service.SetWeight(f.Actor, "SmileR", .4f);
        var history = new TransformHistory();
        int appended = 0;
        history.Appended += _ => appended++;
        var journal = new ValueJournal(history);
        var bindings = Substitute.For<IEntityBindings>();
        var id = new ActorId(Guid.NewGuid(), 1);
        bindings.GetActorId(f.Actor).Returns(id);
        bindings.Resolve(id).Returns(new BindingResult<IActor>(BindingStatus.Success, f.Actor));
        var session = new ExpressionSession(journal,
            new ExpressionRuntimePort(bindings, f.Service));
        journal.BeginEdit("pair");
        session.SetPair(id, "SmileL", "SmileR", .5f);
        session.SetPair(id, "SmileL", "SmileR", 1.4f);
        journal.EndEdit();
        journal.CommitEdit("pair");
        Assert.Equal(1, appended);
        var step = Assert.IsType<JournalStep>(history.PeekUndo());
        Assert.True(step.Undo());
        Assert.Equal(-.2f, f.Service.GetWeight(f.Actor, "SmileL"));
        Assert.Equal(.4f, f.Service.GetWeight(f.Actor, "SmileR"));
        Assert.True(step.Redo());
        Assert.Equal(1.4f, f.Service.GetWeight(f.Actor, "SmileL"));
        Assert.Equal(1.4f, f.Service.GetWeight(f.Actor, "SmileR"));
        session.Reset(id);
        var reset = Assert.IsType<JournalStep>(history.PeekUndo());
        Assert.True(reset.Undo());
        Assert.Equal(1.4f, f.Service.GetWeight(f.Actor, "SmileL"));
    }

    [Theory]
    [InlineData(-.5f)]
    public void New_weights_use_signed_position_rotation_and_scale(float weight)
    {
        var source = new Transform(new Vector3(.01f, .02f, -.03f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .3f), new Vector3(.8f, 1.1f, 1));
        var weighted = PoseMath.WeightExpressionDelta(source, weight);
        Assert.Equal(source.Position * weight, weighted.Position);
        Assert.Equal((source.Scale - Vector3.One) * weight, weighted.Scale);
        var expected = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .3f * weight);
        Assert.True(MathF.Abs(Quaternion.Dot(expected, weighted.Rotation)) > .99999f);
    }

    [Fact]
    public void Parent_frame_projects_the_delta_without_baking_in_head_position()
    {
        var parent = Quaternion.CreateFromYawPitchRoll(.7f, -.4f, 1.2f);
        var delta = new Transform(new Vector3(.01f, 0, .02f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .2f), Vector3.Zero);
        var localBone = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .3f);
        var projected = PoseMath.ProjectExpressionDelta(delta, parent);
        var initial = parent * localBone;
        var actual = initial * projected.Rotation;
        // Literal Ktisis ApplyBlend expression with a fresh (identity) last delta.
        var expected = (initial / parent) * (parent * delta.Rotation);
        Assert.True(MathF.Abs(Quaternion.Dot(expected, actual)) > .99999f);
        Assert.True(Vector3.Distance(Vector3.Transform(projected.Position, Quaternion.Inverse(parent)), delta.Position) < .00001f);
    }

    private sealed unsafe class Fixture : IDisposable
    {
        private readonly Character* _character = (Character*)NativeMemory.AllocZeroed((nuint)sizeof(Character));
        public IActor Actor { get; } = Substitute.For<IActor>();
        public SkeletonPoseInfo Pose { get; } = new();
        public ExpressionService Service { get; }

        public Fixture()
        {
            _character->DrawData.CustomizeData.Race = 1;
            _character->DrawData.CustomizeData.Tribe = 1;
            _character->DrawData.CustomizeData.Sex = 1;
            Face(1);
            Actor.Address.Returns((nint)_character);
            Actor.Id.Returns(new EntityId("expression-test"));
            var skeleton = Substitute.For<ISkeleton>();
            skeleton.IsValid.Returns(true);
            using var stream = typeof(Poser.Files.PoseFileService).Assembly.GetManifestResourceStream("Poser.Data.Expressions.Hyur_Feminine_Midlander.json")!;
            using var json = JsonDocument.Parse(stream);
            var names = json.RootElement.GetProperty("Groups")[0].GetProperty("Units").EnumerateArray()
                .SelectMany(unit => unit.GetProperty("Bones").EnumerateObject().Select(b => b.Name)
                    .Concat(unit.GetProperty("Faces").EnumerateObject().SelectMany(face => face.Value.EnumerateObject().Select(b => b.Name))))
                .Distinct().ToArray();
            var bones = names.Select(name =>
            {
                var bone = Substitute.For<IBone>();
                bone.BoneName.Returns(name);
                bone.PartialId.Returns(1);
                return bone;
            }).ToArray();
            skeleton.Bones.Returns(bones);
            var skeletons = Substitute.For<ISkeletonService>();
            skeletons.GetSkeleton(Actor).Returns(skeleton);
            var posing = Substitute.For<IBonePosingService>();
            posing.GetPoseInfo(skeleton).Returns(Pose);
            Service = new ExpressionService(Substitute.For<IPluginLog>(), skeletons, posing, Substitute.For<IEventBus>());
        }

        public void Face(byte face) => _character->DrawData.CustomizeData.Face = face;
        public void Dispose() => NativeMemory.Free(_character);
    }
}
