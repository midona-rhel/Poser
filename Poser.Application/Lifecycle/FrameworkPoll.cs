using System.Diagnostics;

namespace Poser.Application.Lifecycle;

/// <summary>The one poll-until-bound loop: native state that settles over
/// frames is probed, never waited on by event.</summary>
public static class FrameworkPoll
{
    /// <summary>
    /// Runs <paramref name="probe"/> until it answers true or
    /// <paramref name="bound"/> has elapsed, pausing <paramref name="interval"/>
    /// between probes. The probe always runs at least once, and the bound is
    /// checked only after a probe, so the last answer is never stale. True
    /// when the probe was satisfied, false at the bound. A cancelled
    /// <paramref name="token"/> throws from the pause; probe exceptions
    /// propagate.
    /// </summary>
    public static async Task<bool> Until(
        Func<Task<bool>> probe, TimeSpan bound, TimeSpan interval, CancellationToken token)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            if (await probe())
                return true;
            if (elapsed.Elapsed >= bound)
                return false;
            await Task.Delay(interval, token);
        }
    }
}
