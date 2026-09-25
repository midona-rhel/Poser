using System.Numerics;
using System.Reflection;
using Dalamud.Plugin.Services;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Files;
using Poser.Game.Journal;
using Poser.Game.Lights;
using Poser.Services;

namespace Poser.Game.Tests.Lights;

public sealed class LightBoundaryTests
{
    [Fact]
    public void Drag_retains_detached_values_and_one_reversible_history_step()
    {
        var f = new Fixture();
        var before = f.Control.Read(f.Id)!;
        for (int i = 1; i <= 3; i++)
        {
            f.Journal.BeginEdit("intensity");
            Assert.True(f.Control.SetIntensity(f.Id, i).Success);
            f.Journal.EndEdit();
        }
        Assert.False(f.History.CanUndo);
        f.Control.Seal();
        Assert.Equal(0f, before.Intensity);
        var step = Assert.IsType<JournalStep>(f.History.PeekUndo());
        Assert.True(step.Undo());
        Assert.Equal(0f, f.Light.Intensity);
        f.History.CommitUndo(step);
        Assert.False(f.History.CanUndo);
        Assert.True(step.Redo());
        Assert.Equal(3f, f.Light.Intensity);
    }

    [Fact]
    public void Delayed_area_component_edit_preserves_newer_other_axis()
    {
        var f = new Fixture();
        _ = f.Control.Read(f.Id);
        f.Light.AreaAngle = new(10, 20);
        Assert.True(f.Control.SetAreaAngleX(f.Id, 30).Success);
        Assert.Equal(new Vector2(30, 20), f.Light.AreaAngle);
        var step = Assert.IsType<JournalStep>(f.History.PeekUndo());
        Assert.True(step.Undo());
        Assert.Equal(new Vector2(10, 20), f.Light.AreaAngle);
    }

    [Fact]
    public void Replaced_or_off_thread_targets_cannot_receive_deferred_picks_or_export()
    {
        var f = new Fixture();
        using var file = new TemporaryFile();
        File.WriteAllText(file.Path, "keep");
        f.CurrentId = f.Id.NextGeneration();
        Assert.Null(f.Control.Read(f.Id));
        Assert.False(f.Control.ApplyGobo(f.Id, 0).Success);
        Assert.False(f.Control.SetAttachedBone(f.Id, null).Success);
        Assert.False(f.Files.Export(f.Id, file.Path).Success);
        Assert.Equal("keep", File.ReadAllText(file.Path));
        Assert.False(f.History.CanUndo);
        f.OnThread = false;
        var reads = f.BindingReads;
        Assert.False(f.Control.SetIntensity(f.CurrentId, 10).Success);
        Assert.False(f.Files.Export(f.CurrentId, file.Path).Success);
        Assert.Equal(reads, f.BindingReads);
    }

    [Fact]
    public void Gobo_and_attachment_changes_use_the_existing_history_and_refuse_stale_bones()
    {
        var f = new Fixture();
        Assert.True(f.Control.ApplyGobo(f.Id, 0).Success);
        var goboStep = Assert.IsType<JournalStep>(f.History.PeekUndo());
        Assert.Equal("test.tex", f.Light.GoboPath);
        Assert.True(goboStep.Undo());
        Assert.Null(f.Light.GoboPath);
        Assert.True(goboStep.Redo());
        Assert.Equal("test.tex", f.Light.GoboPath);
        Assert.True(f.Control.SetAttachedBone(f.Id, f.BoneId).Success);
        var attachStep = Assert.IsType<JournalStep>(f.History.PeekUndo());
        Assert.Same(f.Bone, f.Light.AttachedBone);
        Assert.True(attachStep.Undo());
        Assert.Null(f.Light.AttachedBone);
        f.BoneAvailable = false;
        Assert.False(f.Control.SetAttachedBone(f.Id, f.BoneId).Success);
        Assert.Same(attachStep, f.History.PeekUndo());
    }

    [Fact]
    public void File_roundtrip_uses_shared_creation_with_complete_values_and_placement()
    {
        var f = new Fixture();
        using var file = new TemporaryFile();
        f.Light.Intensity = 4;
        f.Light.AreaAngle = new(12, 24);
        f.Light.Transform = new() { Position = new(10, 20, 30), Scale = Vector3.One, Rotation = Quaternion.Identity };
        Assert.True(f.Control.ApplyGobo(f.Id, 0).Success);
        Assert.True(f.Files.Export(f.Id, file.Path).Success);
        Assert.NotNull(f.Files.Import(file.Path, ObjectPlacementMode.RelativeToSelectedActor).Handle);
        var document = Assert.IsType<LightFile>(f.Imported);
        Assert.Equal(4f, document.Intensity);
        Assert.Equal(new Vector2(12, 24), document.AreaAngle);
        Assert.Equal("test.tex", document.Gobo);
        Assert.Equal(new Vector3(15, 20, 30), document.Transform.Position);
        f.Imported = null;
        File.WriteAllText(file.Path, "{");
        Assert.Null(f.Files.Import(file.Path, ObjectPlacementMode.AsSaved).Handle);
        Assert.Null(f.Imported);
    }

    private sealed class Fixture
    {
        public readonly LightId Id = LightId.New();
        public LightId CurrentId;
        public readonly BoneId BoneId = new(new(ActorId.New(), PoseSlot.Character, 0), 0, 1, "j_te_l");
        public bool BoneAvailable = true;
        public bool OnThread = true;
        public int BindingReads;
        public readonly ILight Light;
        public readonly IBone Bone;
        public readonly TransformHistory History = new();
        public readonly ValueJournal Journal;
        public readonly LightControl Control;
        public readonly LightFiles Files;
        public LightFile? Imported;

        public Fixture()
        {
            CurrentId = Id;
            var state = new Dictionary<string, object?> { ["IsValid"] = true, ["Name"] = "Test light" };
            Light = Stub<ILight>((m, a) =>
            {
                if (m.Name.StartsWith("set_")) { state[m.Name[4..]] = a![0]; return null; }
                return state.TryGetValue(m.Name[4..], out var v) ? v :
                    m.ReturnType.IsValueType ? Activator.CreateInstance(m.ReturnType) : null;
            });
            var skeleton = Stub<ISkeleton>((_, _) => BoneAvailable);
            Bone = Stub<IBone>((_, _) => skeleton);
            var bindings = Stub<IEntityBindings>((m, a) =>
            {
                BindingReads++;
                return m.Name switch
                {
                    "Resolve" when a![0] is LightId => new BindingResult<ILight>(BindingStatus.Success, Light),
                    "Resolve" => new BindingResult<IBone>(BindingStatus.Success, Bone),
                    "GetLightId" => CurrentId,
                    "GetBoneId" => BoneId,
                    _ => throw new InvalidOperationException(m.Name),
                };
            });
            var lighting = Stub<ILightingService>((m, a) =>
            {
                switch (m.Name)
                {
                    case "get_IsAvailable": return true;
                    case "get_Gobos": return new[] { new GoboEntry("test.tex", "Test") };
                    case "ApplyGobo": state["GoboPath"] = ((GoboEntry)a![1]!).Path; return true;
                    case "ClearGobo": state["GoboPath"] = null; return null;
                    default: throw new InvalidOperationException(m.Name);
                }
            });
            var framework = Stub<IFramework>((_, _) => OnThread);
            Journal = new(History);
            Control = new(bindings, framework, lighting, new LightSession(Journal, lighting));
            Files = new(framework, bindings, Stub<ISceneCreation>((_, a) =>
            {
                Imported = (LightFile)a![0]!;
                return new SceneCreationResult(new(default, SceneEntityKind.Light));
            }), Stub<IPlacementAnchorSource>((m, a) =>
            {
                if (m.Name == "TryCurrentFor")
                {
                    a![1] = new Vector3(6, 2, 3); a[2] = 0f; a[3] = null; return true;
                }
                return new PlacementAnchorData { Position = new(1, 2, 3), Yaw = 0f };
            }), Stub<IPluginLog>((_, _) => null));
        }
    }

    private sealed class TemporaryFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid():N}.xivl");
        public void Dispose() => File.Delete(Path);
    }

    private static T Stub<T>(Func<MethodInfo, object?[]?, object?> call) where T : class
    {
        var proxy = DispatchProxy.Create<T, StubProxy>();
        ((StubProxy)(object)proxy).Call = call;
        return proxy;
    }
    public class StubProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Call = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Call(method!, args);
    }
}
