using System.Reflection;
using Dalamud.Plugin.Services;
using Poser.Game.Posing;
using Poser.Game.Entities;
using Poser.Game.Services;

namespace Poser.Game.Tests.Posing;

public sealed class PoseExportCaptureTests
{
    [Fact]
    public void Export_writes_once_only_after_every_slot_refreshes()
    {
        using var fixture = new Fixture();
        // A timeout never writes unrefreshed caches.
        fixture.Begin();
        fixture.Framework.Scheduled[0]();
        Assert.Equal(0, fixture.Writes);
        Assert.Equal(new[] { false }, fixture.Results);
        Assert.False(fixture.Capture.IsPending);

        // One interrupted slot refuses the whole capture.
        fixture.Begin();
        fixture.Posing.End(fixture.Slots[0], true);
        Assert.Equal(0, fixture.Writes);
        fixture.Posing.End(fixture.Slots[1], false);
        fixture.Framework.Scheduled[^1]();
        Assert.Equal(0, fixture.Writes);
        Assert.Equal(new[] { false, false }, fixture.Results);

        // A retry writes once, and an old timeout cannot finish it.
        fixture.Begin();
        fixture.Framework.Scheduled[0]();
        Assert.True(fixture.Capture.IsPending);
        fixture.FinishBoth(true);
        Assert.Equal(1, fixture.Writes);
        Assert.Equal(new[] { false, false, true }, fixture.Results);
    }

    private sealed class Fixture : IDisposable
    {
        public readonly Proxy Framework;
        public readonly Proxy Posing;
        public readonly PoseExportCapture Capture;
        public readonly ISkeleton[] Slots = [New<ISkeleton>(), New<ISkeleton>()];
        public readonly List<bool> Results = [];
        public int Writes;

        public Fixture()
        {
            var framework = New<IFramework>();
            Framework = (Proxy)(object)framework;
            var posing = New<IBonePosingService>();
            Posing = (Proxy)(object)posing;
            Capture = new PoseExportCapture(framework, posing,
                New<IPoseFileService>(), New<IPluginLog>());
        }

        public void Begin() => Assert.True(Capture.Begin(Slots,
            _ => { Writes++; return true; }, Results.Add).Success);

        public void FinishBoth(bool executed)
        {
            foreach (var slot in Slots) Posing.End(slot, executed);
            Framework.Scheduled[^1]();
        }

        public void Dispose() => Capture.Dispose();
    }

    private static T New<T>() where T : class => DispatchProxy.Create<T, Proxy>();

    public class Proxy : DispatchProxy
    {
        public readonly List<Action> Scheduled = [];
        private Action<TransitiveActionOutcome>? _ended;

        public void End(ISkeleton slot, bool executed) =>
            _ended?.Invoke(new(slot, executed));

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "get_IsInFrameworkUpdateThread": return true;
                case "RunOnTick":
                    Scheduled.Add((Action)args![0]!);
                    return Task.CompletedTask;
                case "add_TransitiveActionsEnded":
                    _ended += (Action<TransitiveActionOutcome>)args![0]!;
                    return null;
                case "remove_TransitiveActionsEnded":
                    _ended -= (Action<TransitiveActionOutcome>)args![0]!;
                    return null;
                default:
                    return method.ReturnType != typeof(void) && method.ReturnType.IsValueType
                        ? Activator.CreateInstance(method.ReturnType) : null;
            }
        }
    }
}
