using Dalamud.Plugin.Services;

namespace Poser.Game;

/// <summary>
/// The spawn runtime's delayed callbacks: every one runs on the framework
/// thread and only while its exact descriptor (and, for an owned actor, its
/// ownership record) is still current. Stopped once, at dispose, and never
/// restarted.
/// </summary>
internal sealed class SpawnFramePoller
{
    private readonly IFramework? _framework;
    private readonly IActorSpawnNativeAdapter _native;
    private readonly SpawnOwnershipLedger _ownership;
    private readonly Func<long> _clock;
    private readonly IPluginLog? _log;

    public SpawnFramePoller(
        IFramework? framework,
        IActorSpawnNativeAdapter native,
        SpawnOwnershipLedger ownership,
        Func<long> clock,
        IPluginLog? log)
    {
        _framework = framework;
        _native = native;
        _ownership = ownership;
        _clock = clock;
        _log = log;
    }

    public bool IsStopped { get; private set; }

    public void Stop() => IsStopped = true;

    /// <summary>
    /// Bounded per-frame poll on the framework thread; logs on timeout. Never
    /// runs without an exact descriptor, and refuses outright when the
    /// lifetime hook is absent: a delayed callback cannot prove its target is
    /// still the same object across frames without the authoritative
    /// destruction transition.
    ///
    /// <paramref name="skipFrames"/> is Brio's <c>dontStartFor</c>: the first
    /// frames after a native mutation can answer a readiness question with the
    /// state that preceded it, so a condition that must not be believed too
    /// early skips them outright rather than trusting the first answer.
    /// </summary>
    public void PollUntil(
        SpawnOwnershipRecord? ownership,
        SpawnNativeDescriptor lifetime,
        Func<bool> condition,
        Action onSatisfied,
        int? timeoutMs,
        string what,
        int skipFrames = 0)
    {
        if (_framework is null)
            return;
        if (!_native.IsLifetimeAuthoritative)
        {
            _log?.Warning(
                $"ActorSpawnService: delayed {what} skipped - no authoritative lifetime");
            return;
        }

        var token = ownership?.Token;
        long? deadline = timeoutMs is { } bound ? _clock() + bound : null;
        var remainingSkips = skipFrames;
        void Tick(IFramework fw)
        {
            try
            {
                if (!IsCallbackCurrent(token, lifetime))
                {
                    _framework.Update -= Tick;
                    return;
                }
                if (remainingSkips > 0)
                {
                    // Still inside the window where the condition would answer
                    // about the pre-mutation state; the deadline keeps running.
                    remainingSkips--;
                    return;
                }
                if (condition())
                {
                    if (!IsCallbackCurrent(token, lifetime))
                    {
                        _framework.Update -= Tick;
                        return;
                    }
                    onSatisfied();
                    _framework.Update -= Tick;
                }
                else if (_clock() > deadline)
                {
                    _log?.Warning($"ActorSpawnService: timed out waiting for {what}");
                    _framework.Update -= Tick;
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"ActorSpawnService: poll for {what} failed: {ex.Message}");
                _framework.Update -= Tick;
            }
        }
        _framework.Update += Tick;
    }

    public bool IsCallbackCurrent(
        Guid? token,
        SpawnNativeDescriptor lifetime)
    {
        if (IsStopped)
            return false;
        if (_native.ResolveByIndex(lifetime.Index) != lifetime)
            return false;
        return token is null
            || _ownership.TryGetExact(token.Value, lifetime, out _);
    }
}
