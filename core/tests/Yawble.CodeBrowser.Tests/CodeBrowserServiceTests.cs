using System.Text.Json.Nodes;
using Xunit;

namespace Yawble.CodeBrowser.Tests;

/// <summary>The code browser's RPCs, sessions, the event and the watch lifecycle.</summary>
public class CodeBrowserServiceTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset T0 = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
    private const string Window = "app:me";
    private readonly TempDir _tmp = new();
    private readonly string _clone;
    private readonly ManualClock _clock = new(T0);
    private readonly FakeHost _host;
    private readonly CodeBrowserService _service;

    public CodeBrowserServiceTests()
    {
        _clone = _tmp.Sub("clone");
        TempDir.Write(_clone, "src/app.cs", "class A\n{\n}\n");
        File.SetLastWriteTimeUtc(TempDir.Write(_clone, "README.md", "# Title\n"), DateTime.UtcNow.AddHours(-1));
        TempDir.Write(_clone, ".env", "TOKEN=1\n");
        _host = new FakeHost(_clone);
        _service = new CodeBrowserService(_host, new CodeBrowserOptions { Tracker = new TrackerOptions { Watch = false }, DownloadChunkBytes = 1000 }, _clock);
    }

    public async ValueTask DisposeAsync()
    {
        await _service.DisposeAsync();
        _tmp.Dispose();
    }

    private Task<JsonNode?> Call(string method, JsonObject p, string owner = Window) => _service.DispatchAsync(method, p, owner, Ct);

    private async Task<string> OpenAsync(string owner = Window) =>
        (await Call("code.open", new JsonObject { ["team"] = "web", ["member"] = "Ada" }, owner))!["session"]!.GetValue<string>();

    private async Task<string> RefusedAsync(string method, JsonObject p, string owner = Window, string code = CodeBrowserException.Refused)
    {
        var e = await Assert.ThrowsAsync<CodeBrowserException>(() => Call(method, p, owner));
        Assert.Equal(code, e.Code);
        return e.Message;
    }

    [Fact]
    public async Task OpenAnswersTheContractsShape()
    {
        var r = (await Call("code.open", new JsonObject { ["team"] = "web", ["member"] = "Ada" }))!.AsObject();
        Assert.StartsWith("cb-", r["session"]!.GetValue<string>());
        Assert.Equal(Path.GetFullPath(_clone), r["root"]!.GetValue<string>());
        Assert.True(r["clonePresent"]!.GetValue<bool>());
        Assert.Equal("src/app.cs", r["latest"]!["path"]!.GetValue<string>()); // newest by time (README is older below)
        Assert.Equal(1, r["latest"]!["line"]!.GetValue<int>());
    }

    [Fact]
    public async Task AnUnknownMemberIsRefused()
    {
        var e = await Assert.ThrowsAsync<CodeBrowserException>(() => Call("code.open", new JsonObject { ["team"] = "web", ["member"] = "Nobody" }));
        Assert.Equal("no_such_member", e.Code);
        Assert.Equal(CodeBrowserException.BadParams, (await Assert.ThrowsAsync<CodeBrowserException>(() => Call("code.open", new JsonObject()))).Code);
    }

    [Fact]
    public async Task ListMarksAndHides()
    {
        var s = await OpenAsync();
        var list = (await Call("code.list", new JsonObject { ["session"] = s, ["path"] = "" }))!.AsObject();
        Assert.Equal("", list["path"]!.GetValue<string>());
        var entries = list["entries"]!.AsArray().Select(e => e!.AsObject()).ToList();
        Assert.Equal(["src", ".env", "README.md"], entries.Select(e => e["name"]!.GetValue<string>()));
        Assert.Equal("dir", entries[0]["kind"]!.GetValue<string>());
        var env = entries[1];
        Assert.True(env["hidden"]!.GetValue<bool>());
        Assert.Equal("secrets", env["hiddenReason"]!.GetValue<string>());
        Assert.False(entries[2]["hidden"]!.GetValue<bool>());
        Assert.False(entries[2].ContainsKey("hiddenReason"));
        Assert.Null(entries[2]["marker"]);
        Assert.Equal(8, entries[2]["size"]!.GetValue<long>());
        Assert.EndsWith("Z", entries[2]["mtime"]!.GetValue<string>());

        // A change: the file and its folder get the live marker.
        var tracker = _service.TrackerOf(FakeHost.Key)!;
        await Call("code.read", new JsonObject { ["session"] = s, ["path"] = "src/app.cs" }); // the baseline
        TempDir.Write(_clone, "src/app.cs", "class A\n{\n  int x;\n}\n");
        tracker.Notify("src/app.cs", false, _clock.Now);
        _clock.Advance(TimeSpan.FromSeconds(1));
        list = (await Call("code.list", new JsonObject { ["session"] = s, ["path"] = "" }))!.AsObject();
        Assert.Equal("live", list["entries"]![0]!["marker"]!.GetValue<string>());
        var inner = (await Call("code.list", new JsonObject { ["session"] = s, ["path"] = "src" }))!["entries"]![0]!;
        Assert.Equal("src/app.cs", inner["path"]!.GetValue<string>());
        Assert.Equal("live", inner["marker"]!.GetValue<string>());
    }

    [Fact]
    public async Task ReadGivesTextWithLineMarks()
    {
        var s = await OpenAsync();
        var r = (await Call("code.read", new JsonObject { ["session"] = s, ["path"] = "src/app.cs" }))!.AsObject();
        Assert.Equal("text", r["type"]!.GetValue<string>());
        Assert.Equal("csharp", r["language"]!.GetValue<string>());
        Assert.Equal("class A\n{\n}\n", r["content"]!.GetValue<string>());
        Assert.Empty(r["lines"]!.AsArray());
        Assert.False(r["linesUnknown"]!.GetValue<bool>());

        TempDir.Write(_clone, "src/app.cs", "class A\n{\n  int x;\n}\n");
        _clock.Advance(TimeSpan.FromSeconds(5));
        // Read before the watcher's debounce has passed: the change is taken now, so the marks match the content.
        r = (await Call("code.read", new JsonObject { ["session"] = s, ["path"] = "src/app.cs" }))!.AsObject();
        var line = Assert.Single(r["lines"]!.AsArray())!;
        Assert.Equal(3, line["from"]!.GetValue<int>());
        Assert.Equal(3, line["to"]!.GetValue<int>());
        Assert.Equal("added", line["kind"]!.GetValue<string>());
        Assert.Equal("2026-11-12T10:00:05.000Z", line["at"]!.GetValue<string>());

        var ev = Assert.Single(_host.Events);
        Assert.Equal("code.changed", ev.Name);
        Assert.Equal(s, ev.Data["session"]!.GetValue<string>());
        Assert.Equal("src/app.cs", ev.Data["path"]!.GetValue<string>());
        Assert.Equal("added", ev.Data["ranges"]![0]!["kind"]!.GetValue<string>());
        Assert.Equal("2026-11-12T10:00:05.000Z", ev.Data["at"]!.GetValue<string>());

        TempDir.Write(_clone, "src/app.cs", "class A\n}\n");
        _clock.Advance(TimeSpan.FromSeconds(5));
        r = (await Call("code.read", new JsonObject { ["session"] = s, ["path"] = "src/app.cs" }))!.AsObject();
        var removed = Assert.Single(r["removed"]!.AsArray())!;
        Assert.Equal(1, removed["afterLine"]!.GetValue<int>());
        Assert.Equal("{\n  int x;", removed["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task MarkdownImagesAndNotShown()
    {
        var s = await OpenAsync();
        Assert.Equal("markdown", (await Call("code.read", new JsonObject { ["session"] = s, ["path"] = "README.md" }))!["type"]!.GetValue<string>());

        File.WriteAllBytes(Path.Combine(_clone, "logo.png"), [0x89, 0x50, 0x4E, 0x47, 0, 1, 2]);
        var img = (await Call("code.read", new JsonObject { ["session"] = s, ["path"] = "logo.png" }))!.AsObject();
        Assert.Equal("image", img["type"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47, 0, 1, 2]), img["base64"]!.GetValue<string>());
        Assert.False(img.ContainsKey("content"));

        File.WriteAllBytes(Path.Combine(_clone, "tool.exe"), [0x4D, 0x5A, 0, 0, 3]);
        var bin = (await Call("code.read", new JsonObject { ["session"] = s, ["path"] = "tool.exe" }))!.AsObject();
        Assert.Equal("notShown", bin["type"]!.GetValue<string>());
        Assert.Equal("binary", bin["notShownReason"]!.GetValue<string>());
        Assert.Equal(5, bin["size"]!.GetValue<long>());

        TempDir.Write(_clone, "big.txt", new string('x', (int)FileKinds.MaxPreviewBytes + 1));
        Assert.Equal("tooLarge", (await Call("code.read", new JsonObject { ["session"] = s, ["path"] = "big.txt" }))!["notShownReason"]!.GetValue<string>());

        TempDir.Write(_clone, "long.txt", string.Concat(Enumerable.Repeat("a\n", FileKinds.MaxPreviewLines + 1)));
        Assert.Equal("tooLarge", (await Call("code.read", new JsonObject { ["session"] = s, ["path"] = "long.txt" }))!["notShownReason"]!.GetValue<string>());

        var secret = (await Call("code.read", new JsonObject { ["session"] = s, ["path"] = ".env" }))!.AsObject();
        Assert.Equal("notShown", secret["type"]!.GetValue<string>());
        Assert.Equal("secrets", secret["notShownReason"]!.GetValue<string>());
        Assert.False(secret.ContainsKey("content"));
    }

    [Fact]
    public async Task RefusalsComeBackAsCodeRefused()
    {
        var s = await OpenAsync();
        await RefusedAsync("code.read", new JsonObject { ["session"] = s, ["path"] = "../outside.txt" });
        await RefusedAsync("code.read", new JsonObject { ["session"] = s, ["path"] = ".git/config" });
        await RefusedAsync("code.list", new JsonObject { ["session"] = s, ["path"] = "C:/Windows" });
        await RefusedAsync("code.download", new JsonObject { ["session"] = s, ["path"] = ".env" });
        await RefusedAsync("code.list", new JsonObject { ["session"] = "cb-unknown", ["path"] = "" });
    }

    [Fact]
    public async Task ASessionAnswersOnlyWhoOpenedIt()
    {
        var s = await OpenAsync(Window);
        await RefusedAsync("code.list", new JsonObject { ["session"] = s, ["path"] = "" }, owner: "cloud:viewer@example.com");
        await Call("code.close", new JsonObject { ["session"] = s }, "cloud:viewer@example.com"); // not theirs: nothing happens
        Assert.NotNull(await Call("code.list", new JsonObject { ["session"] = s, ["path"] = "" }));
    }

    [Fact]
    public async Task DownloadComesInChunks()
    {
        var bytes = Enumerable.Range(0, 2500).Select(i => (byte)(i % 251)).ToArray();
        File.WriteAllBytes(Path.Combine(_clone, "data.bin"), bytes);
        var s = await OpenAsync();
        var got = new List<byte>();
        long offset = 0;
        string? version = null;
        var answers = 0;
        while (true)
        {
            var p = new JsonObject { ["session"] = s, ["path"] = "data.bin", ["offset"] = offset };
            if (version is not null) p["version"] = version;
            var r = (await Call("code.download", p))!.AsObject();
            answers++;
            Assert.Equal("data.bin", r["name"]!.GetValue<string>());
            Assert.Equal(2500, r["size"]!.GetValue<long>());
            version = r["version"]!.GetValue<string>();
            got.AddRange(Convert.FromBase64String(r["base64"]!.GetValue<string>()));
            offset += r["length"]!.GetValue<long>();
            if (r["done"]!.GetValue<bool>()) break;
        }
        Assert.Equal(3, answers);
        Assert.Equal(bytes, got);

        // The file changes between chunks: refused, the person downloads again.
        File.WriteAllBytes(Path.Combine(_clone, "data.bin"), [.. bytes, 1, 2, 3]);
        Assert.Contains("changed", await RefusedAsync("code.download", new JsonObject { ["session"] = s, ["path"] = "data.bin", ["offset"] = 1000, ["version"] = version }));
    }

    [Fact]
    public async Task ASmallDownloadIsOneAnswer()
    {
        var s = await OpenAsync();
        var r = (await Call("code.download", new JsonObject { ["session"] = s, ["path"] = "README.md" }))!.AsObject();
        Assert.True(r["done"]!.GetValue<bool>());
        Assert.Equal("# Title\n", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(r["base64"]!.GetValue<string>())));
    }

    [Fact]
    public async Task OverFiftyMegabytesIsNotDownloaded()
    {
        using (var f = File.Create(Path.Combine(_clone, "huge.bin"))) f.SetLength(FileKinds.MaxDownloadBytes + 1);
        var s = await OpenAsync();
        Assert.Contains("50 MB", await RefusedAsync("code.download", new JsonObject { ["session"] = s, ["path"] = "huge.bin" }));
    }

    [Fact]
    public async Task NoCloneIsSaid()
    {
        Directory.Delete(_clone, recursive: true);
        var r = (await Call("code.open", new JsonObject { ["team"] = "web", ["member"] = "Ada" }))!.AsObject();
        Assert.False(r["clonePresent"]!.GetValue<bool>());
        Assert.Null(r["latest"]);
        await RefusedAsync("code.list", new JsonObject { ["session"] = r["session"]!.GetValue<string>(), ["path"] = "" }, code: CodeBrowserException.NoClone);
    }

    [Fact]
    public async Task CloseEndsTheSession()
    {
        var s = await OpenAsync();
        Assert.Empty((await Call("code.close", new JsonObject { ["session"] = s }))!.AsObject());
        await RefusedAsync("code.list", new JsonObject { ["session"] = s, ["path"] = "" });
    }

    [Fact]
    public async Task WatchingStopsTwoMinutesAfterTheLastCloseWhenNotRunning()
    {
        var s = await OpenAsync();
        Assert.True(_service.IsWatching(FakeHost.Key));
        await Call("code.close", new JsonObject { ["session"] = s });
        await _service.SweepAsync(Ct);
        _clock.Advance(TimeSpan.FromSeconds(110));
        await _service.SweepAsync(Ct);
        Assert.True(_service.IsWatching(FakeHost.Key)); // within the grace period
        _clock.Advance(TimeSpan.FromSeconds(15));
        await _service.SweepAsync(Ct);
        Assert.False(_service.IsWatching(FakeHost.Key));
    }

    [Fact]
    public async Task ARunningMemberIsWatchedWithoutADialog()
    {
        _host.Running.Add((_host.Member, T0 - TimeSpan.FromHours(2)));
        await _service.SweepAsync(Ct);
        Assert.True(_service.IsWatching(FakeHost.Key));
        _clock.Advance(TimeSpan.FromMinutes(10));
        await _service.SweepAsync(Ct);
        Assert.True(_service.IsWatching(FakeHost.Key));

        // A change an hour into the run is still "recent" while the run lasts.
        var tracker = _service.TrackerOf(FakeHost.Key)!;
        TempDir.Write(_clone, "src/run.cs", "x\n");
        tracker.Notify("src/run.cs", true, _clock.Now);
        tracker.Process(_clock.Now + TimeSpan.FromSeconds(1));
        _clock.Advance(TimeSpan.FromMinutes(45));
        await _service.SweepAsync(Ct);
        Assert.Equal("recent", tracker.Marker("src/run.cs", false, _clock.Now));

        _host.Running.Clear();
        await _service.SweepAsync(Ct);
        _clock.Advance(TimeSpan.FromMinutes(2));
        await _service.SweepAsync(Ct);
        Assert.False(_service.IsWatching(FakeHost.Key));
    }

    [Fact]
    public async Task IdleSessionsAreClosed()
    {
        var s = await OpenAsync();
        _clock.Advance(TimeSpan.FromMinutes(31));
        await RefusedAsync("code.list", new JsonObject { ["session"] = s, ["path"] = "" });
    }

    [Fact]
    public async Task OpenLandsOnTheLatestChange()
    {
        var s = await OpenAsync();
        await Call("code.read", new JsonObject { ["session"] = s, ["path"] = "README.md" });
        TempDir.Write(_clone, "README.md", "# Title\n\nMore.\n");
        var tracker = _service.TrackerOf(FakeHost.Key)!;
        tracker.Notify("README.md", false, _clock.Now);
        tracker.Process(_clock.Now + TimeSpan.FromSeconds(1));
        var r = (await Call("code.open", new JsonObject { ["team"] = "web", ["member"] = "Ada" }))!.AsObject();
        Assert.Equal("README.md", r["latest"]!["path"]!.GetValue<string>());
        Assert.Equal(2, r["latest"]!["line"]!.GetValue<int>());
    }

    [Fact]
    public async Task BranchLinesWhenGitIsThere()
    {
        if (!RealGit.Available) Assert.Skip("git is not installed here");
        var team = _tmp.Sub("team");
        var git = new RealGit(_tmp.Path);
        git.Ok(_tmp.Path, "init", "-q", "-b", "main", team);
        TempDir.Write(team, "src/app.cs", "class A\n{\n}\n");
        git.Ok(team, "add", "-A");
        git.Ok(team, "commit", "-q", "-m", "base");
        TempDir.Write(_clone, "src/app.cs", "class B\n{\n}\n");
        File.SetLastWriteTimeUtc(Path.Combine(_clone, "README.md"), DateTime.UtcNow.AddHours(-3));
        var recording = new RecordingGit(git);
        var host = new FakeHost(_clone, new BranchSource([team], "main", "members/ada"), recording);
        await using var service = new CodeBrowserService(host, new CodeBrowserOptions { Tracker = new TrackerOptions { Watch = false } }, _clock);
        var s = (await service.DispatchAsync("code.open", new JsonObject { ["team"] = "web", ["member"] = "Ada" }, Window, Ct))!["session"]!.GetValue<string>();
        var list = (await service.DispatchAsync("code.list", new JsonObject { ["session"] = s, ["path"] = "" }, Window, Ct))!["entries"]!.AsArray();
        Assert.Equal("branch", list.Single(e => e!["name"]!.GetValue<string>() == "src")!["marker"]!.GetValue<string>());
        Assert.Equal("branch", list.Single(e => e!["name"]!.GetValue<string>() == "README.md")!["marker"]!.GetValue<string>()); // not in the base
        var read = (await service.DispatchAsync("code.read", new JsonObject { ["session"] = s, ["path"] = "src/app.cs" }, Window, Ct))!.AsObject();
        var line = Assert.Single(read["lines"]!.AsArray())!;
        Assert.Equal("branch", line["kind"]!.GetValue<string>());
        Assert.Equal(1, line["from"]!.GetValue<int>());
        Assert.All(recording.Calls, c => Assert.False(SafeTree.IsUnder(Path.GetFullPath(c.Repository), _clone)));
    }
}
