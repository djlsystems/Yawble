using System.Text.Json.Nodes;
using Yawble.Core.Lifetime;
using Xunit;

namespace Yawble.Core.Tests.Lifetime;

/// <summary>The "it was removed" notice, on fake OS facts.</summary>
public class RemovalWatchTests
{
    private sealed class FakeProbe : IInstallProbe
    {
        public bool ExeDeleted, DataChanged, Throws;
        public bool ExecutableDeleted() => Throws ? throw new IOException("no /proc") : ExeDeleted;
        public bool DataFolderChanged() => DataChanged;
    }

    private sealed class FakeFacts : IInstallFacts
    {
        public bool IsLinux { get; init; }
        public string? Link { get; init; }
        public string? ProcessPath { get; init; }
        public HashSet<string> Files { get; } = [];
        public string? LinkTarget(string path) => path == "/proc/self/exe" ? Link : null;
        public bool FileExists(string path) => Files.Contains(path);
    }

    private readonly List<(string Name, JsonNode? Data)> _published = [];
    private readonly List<string> _log = [];

    private RemovalWatch Watch(IInstallProbe probe, TimeSpan? interval = null) =>
        new(probe, (n, d) => { lock (_published) _published.Add((n, d)); }, _log.Add, interval);

    [Fact]
    public void NothingIsSaidWhileEverythingIsInPlace()
    {
        Assert.Null(Watch(new FakeProbe()).CheckOnce());
        Assert.Empty(_published);
    }

    [Fact]
    public void ADeletedExecutableOrAChangedDataFolderPublishesTheNoticeAtEveryCheck_AndLogsOnce()
    {
        var probe = new FakeProbe { ExeDeleted = true };
        var watch = Watch(probe);
        Assert.Equal(new RemovalNotice(true, false), watch.CheckOnce());
        probe.DataChanged = true;
        Assert.Equal(new RemovalNotice(true, true), watch.CheckOnce());

        Assert.Equal(2, _published.Count);
        Assert.All(_published, p => Assert.Equal("app.removed", p.Name));
        var last = _published[^1].Data!;
        Assert.Equal("Yawble was uninstalled or its data was removed", last["message"]!.GetValue<string>());
        Assert.True(last["executableDeleted"]!.GetValue<bool>());
        Assert.True(last["dataFolderChanged"]!.GetValue<bool>());
        Assert.Single(_log);
    }

    [Fact]
    public void ACheckThatFailsSaysNothing()
    {
        Assert.Null(Watch(new FakeProbe { Throws = true, DataChanged = true }).CheckOnce());
        Assert.Empty(_published);
        Assert.Contains(_log, l => l.Contains("no /proc"));
    }

    [Fact]
    public async Task TheWatchChecksOnItsIntervalAndStopsWhenCancelled()
    {
        using var cts = new CancellationTokenSource();
        var run = Watch(new FakeProbe { DataChanged = true }, TimeSpan.FromMilliseconds(10)).RunAsync(cts.Token);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline) { lock (_published) if (_published.Count >= 2) break; await Task.Delay(10, TestContext.Current.CancellationToken); }
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        lock (_published) Assert.True(_published.Count >= 2);
    }

    [Theory]
    [InlineData("/opt/yawble/core/yawble-core (deleted)", true)]
    [InlineData("/opt/yawble/core/yawble-core", false)]
    public void OnLinuxProcSelfExeSaysWhetherTheExecutableWasDeleted(string link, bool deleted)
    {
        var probe = new SystemInstallProbe(() => true, new FakeFacts { IsLinux = true, Link = link, ProcessPath = "/opt/yawble/core/yawble-core" });
        Assert.Equal(deleted, probe.ExecutableDeleted());
        Assert.False(probe.DataFolderChanged());
    }

    [Fact]
    public void ElsewhereTheExecutablesPathMustStillNameAFile()
    {
        var facts = new FakeFacts { IsLinux = false, ProcessPath = @"C:\Program Files\Yawble\resources\core\yawble-core.exe" };
        Assert.True(new SystemInstallProbe(() => true, facts).ExecutableDeleted());
        facts.Files.Add(facts.ProcessPath!);
        Assert.False(new SystemInstallProbe(() => true, facts).ExecutableDeleted());
        // Linux without a readable /proc falls back to the same check; no path at all says nothing.
        Assert.True(new SystemInstallProbe(() => true, new FakeFacts { IsLinux = true, Link = null, ProcessPath = "/gone" }).ExecutableDeleted());
        Assert.False(new SystemInstallProbe(() => true, new FakeFacts { IsLinux = false, ProcessPath = null }).ExecutableDeleted());
    }

    [Fact]
    public void TheDataFolderHasChangedWhenTheLockSaysItIsNoLongerOurs()
    {
        var ours = true;
        var probe = new SystemInstallProbe(() => ours, new FakeFacts { ProcessPath = null });
        Assert.False(probe.DataFolderChanged());
        ours = false;
        Assert.True(probe.DataFolderChanged());
    }
}
