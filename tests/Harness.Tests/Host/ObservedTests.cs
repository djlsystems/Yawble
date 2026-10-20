using Harness.Host;
using Microsoft.Extensions.Logging;

namespace Harness.Tests.Host;

public sealed class ObservedTests
{
    [Fact]
    public async Task A_faulted_task_is_logged_and_never_thrown()
    {
        var log = new CapturingLogger();
        var faulted = Task.FromException(new IOException("disk full"));

        Observed.Forget(faulted, log, "containerChanged push");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("containerChanged push", entry.Message);
        Assert.Contains("disk full", entry.Message);
    }

    [Fact]
    public async Task A_completed_task_logs_nothing()
    {
        var log = new CapturingLogger();

        Observed.Forget(Task.CompletedTask, log, "nothing");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Empty(log.Entries);
    }

    [Fact]
    public void A_record_that_throws_while_composing_is_logged_and_the_timer_keeps_running()
    {
        var log = new CapturingLogger();
        var root = Path.Combine(Path.GetTempPath(), $"harness-observed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            WorkersRecord.RecordSafely(() => throw new IOException("workers.json is on a full disk"), root, log);

            var entry = Assert.Single(log.Entries);
            Assert.Equal(LogLevel.Warning, entry.Level);
            Assert.Contains("workers", entry.Message);
            Assert.Contains("full disk", entry.Message);
            Assert.False(File.Exists(Path.Combine(root, WorkersRecord.FileName)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
