using System.Numerics;
using System.Reflection;
using Dalamud.Plugin.Services;
using Poser.Application.Scene;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Files;
using Poser.Game.Cameras;
using Poser.Services;

namespace Poser.Game.Tests.Cameras;

public sealed class CameraFilesTests
{
    [Theory]
    [InlineData(CameraKind.Game)]
    [InlineData(CameraKind.Free)]
    public void Export_import_preserves_framing_and_anchors_without_exporting_session_lock(CameraKind kind)
    {
        var f = new Fixture();
        using var file = new TemporaryFile();
        f.Values["Kind"] = kind;
        f.Values["IsLocked"] = true;
        f.Values["Angle"] = new Vector2(.5f, -.2f);
        f.Values["Pan"] = new Vector2(.3f, .4f);
        f.Values["Position"] = new Vector3(12, 3, 45);
        f.Values["Orthographic"] = true;
        f.Values["OrthographicZoom"] = 7f;
        Assert.True(f.Files.Export(f.Id, file.Path).Success);
        Assert.NotNull(f.Files.Import(file.Path).Handle);
        var loaded = Assert.IsType<CameraFile>(f.Imported);
        Assert.Equal(kind, loaded.Kind);
        Assert.Equal(new Vector2(.5f, -.2f), loaded.Angle);
        Assert.Equal(new Vector2(.3f, .4f), loaded.Pan);
        Assert.Equal(new Vector3(12, 3, 45), loaded.Position);
        Assert.True(loaded.Orthographic);
        Assert.Equal(7f, loaded.OrthographicZoom);
        Assert.Equal(f.Anchor.Position, loaded.CameraAnchor!.Position);
        Assert.Equal(f.Anchor.Yaw, loaded.ActorAnchor!.Yaw);
        Assert.DoesNotContain("IsLocked", File.ReadAllText(file.Path));
    }

    [Fact]
    public void Stale_export_does_not_overwrite_an_existing_file()
    {
        var f = new Fixture();
        using var file = new TemporaryFile();
        File.WriteAllText(file.Path, "existing content");
        f.CurrentId = new(f.Id.LogicalId, f.Id.Generation + 1);
        Assert.False(f.Files.Export(f.Id, file.Path).Success);
        Assert.Equal("existing content", File.ReadAllText(file.Path));
    }

    [Fact]
    public void Unreadable_import_creates_nothing()
    {
        var f = new Fixture();
        using var file = new TemporaryFile();
        File.WriteAllText(file.Path, "{");
        Assert.Null(f.Files.Import(file.Path).Handle);
        Assert.Null(f.Imported);
    }

    private sealed class Fixture
    {
        public readonly CameraId Id = new(Guid.NewGuid(), 0);
        public CameraId CurrentId;
        public readonly Dictionary<string, object?> Values = new() { ["IsValid"] = true, ["Name"] = "Saved view" };
        public readonly PlacementAnchorData Anchor = new() { Position = new(1, 2, 3), Yaw = .4f };
        public CameraFile? Imported;
        public readonly CameraFiles Files;

        public Fixture()
        {
            CurrentId = Id;
            var camera = Stub<IVirtualCamera>((m, _) =>
                Values.TryGetValue(m.Name[4..], out var v) ? v :
                m.ReturnType.IsValueType ? Activator.CreateInstance(m.ReturnType) : null);
            Files = new(
                Stub<IFramework>((_, _) => true),
                Stub<IEntityBindings>((m, _) => m.Name == "Resolve"
                    ? new BindingResult<IVirtualCamera>(BindingStatus.Success, camera) : CurrentId),
                Stub<ISceneCreation>((_, a) =>
                {
                    Imported = (CameraFile)a![0]!;
                    return new SceneCreationResult(new(default, SceneEntityKind.Camera));
                }),
                Stub<IPlacementAnchorSource>((_, _) => Anchor),
                Stub<IPluginLog>((_, _) => null));
        }
    }

    private sealed class TemporaryFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid():N}.xivc");
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
