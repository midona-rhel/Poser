using Poser.Application.Lifecycle;

namespace Poser.Application.Tests.Lifecycle;

public sealed class StartupCleanupTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void Failure_unwinds_acquired_resources_and_allows_a_second_start(int failAfter)
    {
        var live = new HashSet<int>();
        var disposed = new List<int>();
        var original = new InvalidOperationException("activation");
        var error = Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var startup = new StartupCleanup(_ => { });
            for (int i = 0; i < failAfter; i++)
            {
                int resource = i;
                Assert.True(live.Add(resource));
                startup.OnFailure(() => { live.Remove(resource); disposed.Add(resource); });
            }
            throw original;
        }));
        Assert.Same(original, error);
        Assert.Empty(live);
        Assert.Equal(Enumerable.Range(0, failAfter).Reverse(), disposed);

        using var second = new StartupCleanup(_ => { });
        for (int i = 0; i < 7; i++)
        {
            int resource = i;
            Assert.True(live.Add(resource));
            second.OnFailure(() => live.Remove(resource));
        }
        second.Complete();
        second.Dispose();
        Assert.Equal(7, live.Count);
    }

    [Fact]
    public void Throwing_cleanup_and_logger_do_not_mask_original_failure_or_skip_other_resources()
    {
        int releases = 0;
        var startup = new StartupCleanup(_ => throw new Exception("logger"));
        startup.OnFailure(() => releases++);
        startup.OnFailure(() => throw new Exception("cleanup"));
        var original = new InvalidOperationException("activation");
        var error = Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using (startup)
                throw original;
        }));
        Assert.Same(original, error);
        startup.Dispose();
        Assert.Equal(1, releases);
    }
}
