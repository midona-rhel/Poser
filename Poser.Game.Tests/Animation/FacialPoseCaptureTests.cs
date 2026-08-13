using System.Reflection;
using System.Runtime.CompilerServices;
using Dalamud.Plugin.Services;
using Poser.Application.Animation;
using Poser.Application.Lifecycle;
using Poser.Application.Operations;
using Poser.Application.Selection;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Game.Animation;
using Poser.Game.Bindings;

namespace Poser.Game.Tests.Animation;

public sealed class FacialPoseCaptureTests
{
    [Fact]
    public void Capture_consumes_the_shared_session_source_and_exposes_receipt_lifecycle()
    {
        var constructor = Assert.Single(typeof(FacialPoseCapture).GetConstructors());

        Assert.Contains(
            constructor.GetParameters(),
            parameter => parameter.ParameterType == typeof(ISessionGenerationSource));
        Assert.NotNull(typeof(FacialPoseCapture).GetProperty("LastReceipt"));
        Assert.NotNull(typeof(FacialPoseCapture).GetMethod("CancelPending"));
        Assert.NotNull(typeof(FacialPoseCapture).GetEvent("ReceiptChanged"));
    }

    [Fact]
    public void Capture_does_not_own_or_mint_session_generation_state()
    {
        var sourceParameter = Assert.Single(
            typeof(FacialPoseCapture)
                .GetConstructors()
                .SelectMany(constructor => constructor.GetParameters()),
            parameter =>
                parameter.ParameterType == typeof(ISessionGenerationSource));

        Assert.Equal("sessionGeneration", sourceParameter.Name);
        Assert.DoesNotContain(
            typeof(FacialPoseCapture).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static),
            method => method.Name.Contains("Session", StringComparison.OrdinalIgnoreCase)
                && method.Name.Contains("New", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Pending_receipt_is_non_terminal_and_terminal_states_are_explicit()
    {
        Assert.Contains(OperationReceiptState.Pending, Enum.GetValues<OperationReceiptState>());
        Assert.Contains(OperationReceiptState.Applied, Enum.GetValues<OperationReceiptState>());
        Assert.Contains(OperationReceiptState.RolledBack, Enum.GetValues<OperationReceiptState>());
        Assert.Contains(OperationReceiptState.Failed, Enum.GetValues<OperationReceiptState>());
        Assert.Contains(OperationReceiptState.RecoveryRequired, Enum.GetValues<OperationReceiptState>());
        Assert.Contains(OperationReceiptState.Cancelled, Enum.GetValues<OperationReceiptState>());
    }

    [Fact]
    public void Begin_without_an_active_session_has_no_operation_mutation_or_callback()
    {
        var scene = new SceneSession(new SelectionSession());
        var runtime = NewProxy<ITransformRuntimePort>();
        var gestures = new TransformGestureService(
            scene,
            runtime,
            new TransformHistory());
        var transforms = new TransformCommandService(
            scene,
            runtime,
            gestures.History,
            gestures);
        var source = new TestSessionSource();
        var capture = new FacialPoseCapture(
            NewProxy<IFramework>(),
            (StableBindingRegistry)RuntimeHelpers.GetUninitializedObject(
                typeof(StableBindingRegistry)),
            scene,
            new AnimationSession(NewProxy<IAnimationRuntimePort>()),
            transforms,
            gestures,
            source,
            NewProxy<IPluginLog>());
        var callbacks = 0;
        capture.ReceiptChanged += _ => callbacks++;

        var actor = new ActorId(Guid.NewGuid(), 1);
        var result = capture.Begin(
            actor,
            new ActorDescriptor(actor, "Actor", Array.Empty<SkeletonDescriptor>()));

        Assert.False(result.Success);
        Assert.Null(capture.LastReceipt);
        Assert.False(capture.IsPending);
        Assert.False(capture.LastReceipt is { });
        Assert.Equal(0, callbacks);
        Assert.False(capture.LastReceipt is { State: OperationReceiptState.Pending });
        Assert.False(capture.IsPending);

        capture.Dispose();
        gestures.Dispose();
    }

    private static T NewProxy<T>() where T : class =>
        DispatchProxy.Create<T, DefaultProxy>();

    private class DefaultProxy : DispatchProxy
    {
        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? args)
        {
            if (targetMethod?.Name == "get_IsInFrameworkUpdateThread")
                return true;
            if (targetMethod?.ReturnType == typeof(void))
                return null;
            if (targetMethod?.ReturnType is { IsValueType: true } type)
                return Activator.CreateInstance(type);
            return null;
        }
    }

    private sealed class TestSessionSource : ISessionGenerationSource
    {
        public SessionGeneration? ActiveSessionGeneration => null;
    }
}
