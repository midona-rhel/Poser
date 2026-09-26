using Poser.Application.Scene;
using Poser.Domain.Identity;
using Poser.Domain.Operations;
using Poser.Game.Scene;

namespace Poser.Game.Tests.Scene;

public sealed class SceneRuntimeHandlesTests
{
    [Fact]
    public void Receipts_only_resolve_the_exact_instance_in_the_issuing_runtime()
    {
        var session = SessionGeneration.New();
        var runtime = new SceneRuntimeHandles(() => session);
        var other = new SceneRuntimeHandles(() => session);
        var first = new object();
        var replacement = new object();
        var original = runtime.Track(SceneEntityKind.Actor, first);
        Assert.Same(first, runtime.Require<object>(original, SceneEntityKind.Actor));
        Assert.Null(other.Resolve(original));
        Assert.Null(runtime.Resolve(new SceneEntityHandle(session, SceneEntityKind.Actor)));
        Assert.Null(runtime.Resolve<object>(original, SceneEntityKind.Camera));

        runtime.Forget(original);
        var restored = runtime.Track(SceneEntityKind.Actor, replacement);
        Assert.Null(runtime.Resolve(original));
        Assert.Same(replacement, runtime.Require<object>(restored, SceneEntityKind.Actor));
    }

    [Fact]
    public void Ending_or_replacing_a_session_invalidates_every_old_receipt()
    {
        SessionGeneration? session = SessionGeneration.New();
        var runtime = new SceneRuntimeHandles(() => session);
        var original = runtime.Track(SceneEntityKind.Light, new object());
        session = null;
        Assert.Null(runtime.Resolve(original));
        session = SessionGeneration.New();
        var replacement = runtime.Track(SceneEntityKind.Light, new object());
        Assert.Null(runtime.Resolve(original));
        Assert.NotNull(runtime.Resolve(replacement));
        session = SessionGeneration.New();
        Assert.Null(runtime.Resolve(replacement));
        Assert.Throws<InvalidOperationException>(() => runtime.Require<object>(replacement, SceneEntityKind.Light));
    }

    [Theory]
    [InlineData(SceneEntityKind.Actor)]
    [InlineData(SceneEntityKind.Prop)]
    [InlineData(SceneEntityKind.Overlay)]
    [InlineData(SceneEntityKind.WorldObject)]
    [InlineData(SceneEntityKind.Light)]
    [InlineData(SceneEntityKind.Camera)]
    public void Rollback_uses_the_restored_entity_without_retargeting_receipt_reads(SceneEntityKind kind)
    {
        var runtime = new SceneRuntimeHandles(() => Session);
        var original = new object();
        var restored = new object();
        var receipt = runtime.Track(kind, original);
        object? removed = null;

        Assert.Same(original, runtime.Resolve(receipt));
        runtime.Remove<object>(receipt, kind, captured =>
        {
            Assert.Same(original, captured);
            return restored;
        }, current => removed = current);

        Assert.Same(restored, removed);
        Assert.Null(runtime.Resolve(receipt));
        Assert.Same(original, runtime.ResolveHistory(receipt));
        runtime.Clear();
        Assert.Null(runtime.ResolveHistory(receipt));
    }

    [Fact]
    public void Failed_rollback_keeps_the_receipt_available_for_retry()
    {
        var runtime = new SceneRuntimeHandles(() => Session);
        var original = new object();
        var receipt = runtime.Track(SceneEntityKind.Actor, original);
        Assert.Throws<InvalidOperationException>(() => runtime.Remove<object>(
            receipt, SceneEntityKind.Actor, current => current,
            _ => throw new InvalidOperationException("Removal refused")));
        Assert.Same(original, runtime.Resolve(receipt));
    }

    private static readonly SessionGeneration Session = SessionGeneration.New();
}
