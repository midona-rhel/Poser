using Poser.Application.Scene;

namespace Poser.Application.Tests.Scene;

public class CoalescedRefreshTests
{
    [Fact]
    public void Requests_run_only_on_drain_once_per_frame_and_exit_or_dispose_drops_them()
    {
        var queue = new CoalescedRefresh();
        int passes = 0;
        void Refresh()
        {
            passes++;
            // Discovery publishes from inside the pass; that waits a frame.
            queue.Request();
            queue.Request();
        }

        // A clear-first load: one request per delete and per spawn, none
        // of which may run a refresh in the publisher's stack.
        for (int i = 0; i < 40; i++)
            queue.Request();
        Assert.Equal(0, passes);
        queue.Drain(Refresh);
        Assert.Equal(1, passes);
        queue.Drain(Refresh);
        Assert.Equal(2, passes);

        // GPose exit cancels pending work; disposal rejects later requests.
        queue.Cancel();
        queue.Drain(Refresh);
        Assert.Equal(2, passes);
        queue.Request();
        queue.Stop();
        queue.Drain(Refresh);
        queue.Request();
        queue.Drain(Refresh);
        Assert.Equal(2, passes);
    }
}
