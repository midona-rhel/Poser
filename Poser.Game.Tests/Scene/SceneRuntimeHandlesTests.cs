using Poser.Application.Scene;
using Poser.Domain.Identity;
using Poser.Domain.Operations;
using Poser.Game.Scene;

namespace Poser.Game.Tests.Scene;

public sealed class SceneRuntimeHandlesTests
{
    [Fact]
    public void Receipts_only_resolve_the_exact_instance_in_the_issuing_runtime_and_session()
    {
        var initial = SessionGeneration.New();
        SessionGeneration? session = initial;
        var runtime = new SceneRuntimeHandles(() => session);
        var other = new SceneRuntimeHandles(() => session);
        var first = new object();
        var replacement = new object();
        var original = runtime.Track(SceneEntityKind.Actor, first);
        Assert.Same(first, runtime.Require<object>(original, SceneEntityKind.Actor));
        Assert.Null(other.Resolve(original));
        Assert.Null(runtime.Resolve(new SceneEntityHandle(initial, SceneEntityKind.Actor)));
        Assert.Null(runtime.Resolve<object>(original, SceneEntityKind.Camera));

        runtime.Forget(original);
        var restored = runtime.Track(SceneEntityKind.Actor, replacement);
        Assert.Null(runtime.Resolve(original));
        Assert.Same(replacement, runtime.Require<object>(restored, SceneEntityKind.Actor));

        // Ending or replacing the session invalidates every older receipt.
        session = null;
        Assert.Null(runtime.Resolve(restored));
        session = SessionGeneration.New();
        var next = runtime.Track(SceneEntityKind.Light, new object());
        Assert.Null(runtime.Resolve(restored));
        Assert.NotNull(runtime.Resolve(next));
        session = SessionGeneration.New();
        Assert.Null(runtime.Resolve(next));
        Assert.Throws<InvalidOperationException>(() => runtime.Require<object>(next, SceneEntityKind.Light));
    }

    [Fact]
    public void Rollback_uses_the_restored_entity_without_retargeting_receipt_reads()
    {
        var runtime = new SceneRuntimeHandles(() => Session);
        var original = new object();
        var restored = new object();
        var receipt = runtime.Track(SceneEntityKind.Actor, original);
        object? removed = null;

        // A refused removal keeps the receipt available for retry.
        Assert.Throws<InvalidOperationException>(() => runtime.Remove<object>(
            receipt, SceneEntityKind.Actor, current => current,
            _ => throw new InvalidOperationException("Removal refused")));
        Assert.Same(original, runtime.Resolve(receipt));

        runtime.Remove<object>(receipt, SceneEntityKind.Actor, captured =>
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

    private static readonly SessionGeneration Session = SessionGeneration.New();
}
