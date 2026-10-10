using Poser.Application.Scene;

namespace Poser.Application.Tests.Scene;

public class CoalescedRefreshTests
{
    [Fact]
    public void NotificationsDuringFollowUpWaitForNextTickAndExitOrDisposeStopsQueuedWork()
    {
        var queue = new CoalescedRefresh();
        int passes = 0, depth = 0;
        void Refresh()
        {
            Assert.Equal(1, ++depth);
            if (++passes < 4)
            {
                queue.Request(Refresh);
                queue.Request(Refresh);
            }
            depth--;
        }
        queue.Request(Refresh);
        Assert.Equal(2, passes);
        queue.Drain(Refresh);
        Assert.Equal(4, passes);

        // GPose exit cancels pending work; disposal rejects queued callbacks.
        passes = 0;
        void Endless() { passes++; queue.Request(Endless); }
        queue.Request(Endless);
        queue.Cancel();
        queue.Drain(Endless);
        Assert.Equal(2, passes);
        queue.Request(Endless);
        Assert.Equal(4, passes);
        queue.Stop();
        queue.Drain(Endless);
        queue.Request(Endless);
        Assert.Equal(4, passes);
    }
}
