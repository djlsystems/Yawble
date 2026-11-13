using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Yawble.CodeBrowser.Tests;

/// <summary>
/// Edge probes: the size of the biggest answers as the Cloud Link puts them on the WebSocket
/// (CloudLink.ServeAsync: JsonSerializer.SerializeToUtf8Bytes(message) with default options), and path refusals the
/// other tests do not spell out (.git spellings, 8.3 short names, alternate data streams, hidden files at depth).
/// </summary>
public class TesterProbeTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Window = "app:me";
    private readonly TempDir _tmp = new();
    private readonly string _clone;
    private readonly FakeHost _host;
    private readonly CodeBrowserService _service;

    public TesterProbeTests()
    {
        _clone = _tmp.Sub("clone");
        _host = new FakeHost(_clone);
        _service = new CodeBrowserService(_host, new CodeBrowserOptions { Tracker = new TrackerOptions { Watch = false } });
    }

    public async ValueTask DisposeAsync()
    {
        await _service.DisposeAsync();
        _tmp.Dispose();
    }

    private Task<JsonNode?> Call(string method, JsonObject p) => _service.DispatchAsync(method, p, Window, Ct);

    private async Task<string> OpenAsync() =>
        (await Call("code.open", new JsonObject { ["team"] = "web", ["member"] = "Ada" }))!["session"]!.GetValue<string>();

    /// <summary>What CloudLink.Send puts on the socket for a reply carrying this result.</summary>
    private static int WireBytes(JsonNode? result)
    {
        var reply = new JsonObject { ["type"] = "result", ["session"] = "viewer-session", ["id"] = 12345, ["result"] = result };
        return JsonSerializer.SerializeToUtf8Bytes(reply).Length;
    }

    private static void WriteBytes(string root, string rel, byte[] bytes)
    {
        var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    // ------------------------------------------------------------------ wire sizes (point 5)

    [Fact]
    public async Task DownloadChunkOfRandomBytesIsAbout6MbOnTheWire()
    {
        var bytes = new byte[5 * 1024 * 1024];
        new Random(11).NextBytes(bytes);
        WriteBytes(_clone, "big.bin", bytes);
        var s = await OpenAsync();
        var r = await Call("code.download", new JsonObject { ["session"] = s, ["path"] = "big.bin" });
        var wire = WireBytes(r);
        TestContext.Current.SendDiagnosticMessage($"download chunk (4 MiB random) on the wire: {wire:N0} bytes");
        // base64 of 4 MiB is 5,592,408 chars; the default encoder escapes '+' as \u002B (6 bytes), about 1 char in 64.
        Assert.InRange(wire, 5_592_408, 6_200_000);
        Assert.True(wire < 16 * 1024 * 1024);
    }

    [Fact]
    public async Task ReadOf1MbHtmlLikeTextIsAbout6MbOnTheWire()
    {
        // 1 MiB of '<' and '>' (an HTML / XML / Vue file in the extreme): each is escaped as \u003C / \u003E by the default encoder.
        var line = new string('<', 30) + new string('>', 29) + "\n";
        var sb = new StringBuilder();
        while (sb.Length + line.Length <= 1024 * 1024) sb.Append(line);
        TempDir.Write(_clone, "page.html", sb.ToString());
        var s = await OpenAsync();
        var r = await Call("code.read", new JsonObject { ["session"] = s, ["path"] = "page.html" });
        Assert.Equal("text", r!["type"]!.GetValue<string>());
        var wire = WireBytes(r);
        TestContext.Current.SendDiagnosticMessage($"code.read (1 MiB of '<' '>') on the wire: {wire:N0} bytes");
        Assert.True(wire > 5_500_000, $"expected the escaping to blow up the read to ~6 MB, got {wire:N0}");
        Assert.True(wire < 16 * 1024 * 1024);
    }

    [Fact]
    public async Task ReadOf1MbCjkTextIsAbout2MbOnTheWire()
    {
        var line = new string('漢', 40) + "\n"; // 3 UTF-8 bytes each, escaped to 6
        var sb = new StringBuilder();
        while (Encoding.UTF8.GetByteCount(sb.ToString()) + Encoding.UTF8.GetByteCount(line) <= 1024 * 1024 - 200) sb.Append(line);
        TempDir.Write(_clone, "zh.txt", sb.ToString());
        var s = await OpenAsync();
        var wire = WireBytes(await Call("code.read", new JsonObject { ["session"] = s, ["path"] = "zh.txt" }));
        TestContext.Current.SendDiagnosticMessage($"code.read (1 MiB CJK) on the wire: {wire:N0} bytes");
        Assert.InRange(wire, 1_900_000, 2_200_000);
    }

    [Fact]
    public async Task ListOfTheMaximum10000EntriesStaysUnder3Mb()
    {
        var dir = Path.Combine(_clone, "many");
        Directory.CreateDirectory(dir);
        for (var i = 0; i < 10_500; i++) File.WriteAllText(Path.Combine(dir, $"file-with-a-fairly-long-name-{i:D5}.component.spec.ts"), "");
        var s = await OpenAsync();
        var r = await Call("code.list", new JsonObject { ["session"] = s, ["path"] = "many" });
        Assert.Equal(SafeTree.MaxListEntries, r!["entries"]!.AsArray().Count);
        var wire = WireBytes(r);
        TestContext.Current.SendDiagnosticMessage($"code.list (10,000 entries) on the wire: {wire:N0} bytes");
        Assert.True(wire < 3_000_000);
    }

    // ------------------------------------------------------------------ path refusals (point 4)

    [Theory]
    [InlineData(".git/config")]
    [InlineData(".GIT/config")]
    [InlineData("sub/.git/config")]
    [InlineData("sub/deeper/.Git/HEAD")]
    [InlineData(".git./config")]
    [InlineData(".git /config")]
    [InlineData("sub/.git")]
    [InlineData("a.txt::$DATA")]
    [InlineData(".git::$INDEX_ALLOCATION/config")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("//server/share/x")]
    [InlineData("sub/../../x")]
    public async Task GitAndEscapingSpellingsAreRefusedForReadAndDownload(string path)
    {
        TempDir.Write(_clone, ".git/config", "[core]\n");
        TempDir.Write(_clone, "sub/.git/config", "[core]\n");
        TempDir.Write(_clone, "sub/deeper/.git/HEAD", "ref: x\n");
        TempDir.Write(_clone, "a.txt", "a\n");
        var s = await OpenAsync();
        foreach (var method in new[] { "code.read", "code.download", "code.list" })
        {
            var e = await Assert.ThrowsAsync<CodeBrowserException>(() => Call(method, new JsonObject { ["session"] = s, ["path"] = path }));
            Assert.Equal(CodeBrowserException.Refused, e.Code);
        }
    }

    [Fact]
    public async Task NestedGitFoldersAreNotListedAtAnyDepth()
    {
        TempDir.Write(_clone, "sub/deeper/.git/HEAD", "ref: x\n");
        TempDir.Write(_clone, "sub/deeper/ok.txt", "ok\n");
        var s = await OpenAsync();
        var r = await Call("code.list", new JsonObject { ["session"] = s, ["path"] = "sub/deeper" });
        var names = r!["entries"]!.AsArray().Select(e => e!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(["ok.txt"], names);
    }

    [Fact]
    public async Task ShortNamesCannotReachGitOrHiddenFiles()
    {
        if (!OperatingSystem.IsWindows()) return;
        TempDir.Write(_clone, ".git/config", "[core]\n");
        TempDir.Write(_clone, "credentials.json", "{\"k\":1}\n");
        TempDir.Write(_clone, "a-long-folder-name/.env.production", "TOKEN=1\n");
        var s = await OpenAsync();
        var probes = new List<string>();
        foreach (var rel in new[] { ".git", "credentials.json", "a-long-folder-name/.env.production" })
        {
            var shortFull = ShortPath(Path.Combine(_clone, rel.Replace('/', '\\')));
            var shortRoot = ShortPath(_clone);
            if (shortFull is null || shortRoot is null) continue;
            var leafShort = Path.GetFileName(shortFull);
            if (string.Equals(leafShort, Path.GetFileName(rel), StringComparison.OrdinalIgnoreCase)) continue; // no 8.3 name on this volume
            var parent = Path.GetDirectoryName(rel.Replace('/', '\\'))!.Replace('\\', '/');
            probes.Add(parent.Length == 0 ? leafShort : parent + "/" + leafShort);
        }
        TestContext.Current.SendDiagnosticMessage(probes.Count == 0 ? "8.3 names are off on the temp volume: probe skipped" : "8.3 probes: " + string.Join(", ", probes));
        foreach (var p in probes)
        {
            foreach (var method in new[] { "code.read", "code.download" })
            {
                var target = p.StartsWith("GIT", StringComparison.OrdinalIgnoreCase) && !p.Contains('/') ? p + "/config" : p;
                var e = await Assert.ThrowsAsync<CodeBrowserException>(() => Call(method, new JsonObject { ["session"] = s, ["path"] = target }));
                Assert.Equal(CodeBrowserException.Refused, e.Code);
            }
        }
    }

    [Fact]
    public async Task HiddenFileInsideAHiddenFolderAndPolicyFolderIsNeverRead()
    {
        TempDir.Write(_clone, "credentials/notes.txt", "pw=1\n");
        TempDir.Write(_clone, "private/keys.txt", "pw=2\n");
        var denied = Path.Combine(_clone, "private");
        _host.Denied = p => SafeTree.IsUnder(Path.GetFullPath(p), denied);
        var s = await OpenAsync();
        foreach (var path in new[] { "credentials/notes.txt", "private/keys.txt" })
        {
            var r = await Call("code.read", new JsonObject { ["session"] = s, ["path"] = path });
            Assert.Equal("notShown", r!["type"]!.GetValue<string>());
            Assert.Equal("secrets", r["notShownReason"]!.GetValue<string>());
            Assert.Null(r["content"]);
            await Assert.ThrowsAsync<CodeBrowserException>(() => Call("code.download", new JsonObject { ["session"] = s, ["path"] = path }));
            await Assert.ThrowsAsync<CodeBrowserException>(() => Call("code.list", new JsonObject { ["session"] = s, ["path"] = Path.GetDirectoryName(path)!.Replace('\\', '/') }));
        }
        var root = await Call("code.list", new JsonObject { ["session"] = s, ["path"] = "" });
        foreach (var e in root!["entries"]!.AsArray())
            Assert.True(e!["hidden"]!.GetValue<bool>(), $"{e["name"]} should be listed as hidden");
    }

    [Fact]
    public async Task ANewerVersionMidDownloadIsRefused()
    {
        var bytes = new byte[6 * 1024 * 1024];
        WriteBytes(_clone, "d.bin", bytes);
        var s = await OpenAsync();
        var first = (await Call("code.download", new JsonObject { ["session"] = s, ["path"] = "d.bin" }))!.AsObject();
        Assert.False(first["done"]!.GetValue<bool>());
        File.SetLastWriteTimeUtc(Path.Combine(_clone, "d.bin"), DateTime.UtcNow.AddMinutes(1));
        var e = await Assert.ThrowsAsync<CodeBrowserException>(() => Call("code.download", new JsonObject
        {
            ["session"] = s, ["path"] = "d.bin", ["offset"] = first["length"]!.GetValue<long>(), ["version"] = first["version"]!.GetValue<string>(),
        }));
        Assert.Contains("changed", e.Message);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint size);

    private static string? ShortPath(string path)
    {
        var sb = new StringBuilder(1024);
        return GetShortPathNameW(path, sb, (uint)sb.Capacity) == 0 ? null : sb.ToString();
    }
}
