using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Ipc;
using Yawble.Core.Link;
using Yawble.Core.Provenance;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Updates;
using Xunit;

namespace Yawble.Core.Tests.Updates;

/// <summary>
/// Updates to the app: the newest release is found, a newer one's installer is downloaded and verified (hash and
/// signature) before it is ready, and only the person's window installs it. The feed and the signature check are fakes:
/// nothing reaches the network and nothing runs.
/// </summary>
public sealed class UpdateServiceTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Base = UpdateService.ReleasesUrl + "/download/v2026.11.20.1/";
    private readonly TempDb _db = new();

    public void Dispose() => _db.Dispose();

    private sealed class Feed : IReleaseFeed
    {
        public ReleaseInfo? Latest { get; set; }
        public Dictionary<string, byte[]> Files { get; } = [];
        public List<string> Downloads { get; } = [];
        public Exception? Fail { get; set; }

        public Task<ReleaseInfo?> LatestAsync(CancellationToken ct) => Fail is null ? Task.FromResult(Latest) : Task.FromException<ReleaseInfo?>(Fail);
        public Task<string> ReadTextAsync(string url, CancellationToken ct) => Task.FromResult(Encoding.UTF8.GetString(Files[url]));
        public async Task DownloadAsync(string url, string file, Action<long>? progress, CancellationToken ct)
        {
            Downloads.Add(url);
            await File.WriteAllBytesAsync(file, Files[url], ct);
            progress?.Invoke(Files[url].Length);
        }
    }

    private sealed class Check : IInstallerCheck
    {
        public string? Answer { get; set; }
        public string? Problem(string file) => Answer;
    }

    private static readonly byte[] Installer = Encoding.UTF8.GetBytes("a made-up installer");

    private static (Feed Feed, Check Check) Release(string version = "2026.11.20.1", string? sumsFor = null, byte[]? installer = null)
    {
        var name = $"Yawble-Setup-{version}-x64.exe";
        var feed = new Feed();
        var bytes = installer ?? Installer;
        var hash = Convert.ToHexString(SHA256.HashData(Installer)).ToLowerInvariant();
        feed.Files[Base + name] = bytes;
        feed.Files[Base + "SHA256SUMS"] = Encoding.UTF8.GetBytes($"{hash}  {sumsFor ?? name}\n");
        feed.Latest = new ReleaseInfo(version, "v" + version, UpdateService.ReleasesUrl + "/tag/v" + version,
            [new ReleaseAsset(name, Base + name, bytes.Length), new ReleaseAsset("SHA256SUMS", Base + "SHA256SUMS", 100)]);
        return (feed, new Check());
    }

    private UpdateService Service(Feed feed, Check check, string current = "2026.11.12.1", bool canInstall = true) =>
        new(_db.Dir, _db.ConnectionString, current, feed, check, canInstall, "x64");

    [Theory]
    [InlineData("2026.11.20.1", "2026.11.12.1", true)]
    [InlineData("2026.11.12.2", "2026.11.12.1", true)]
    [InlineData("2026.11.12.1", "2026.11.12.1", false)]
    [InlineData("2026.11.12.1", "2026.11.20.1", false)]
    [InlineData("2026.11.12.1", "0.0.0+dev.abc1234", true)]
    [InlineData("2026.11.12.1", "2026.11.12.1+dev.abc1234.dirty", false)]
    [InlineData("v2026.11.13.1", "2026.11.12.1+dev.abc1234", true)]
    [InlineData("not-a-version", "2026.11.12.1", false)]
    public void ComparesReleaseVersions(string latest, string current, bool newer) => Assert.Equal(newer, ReleaseVersion.IsNewer(latest, current));

    [Fact]
    public async Task ANewerRelease_IsDownloaded_Verified_AndReady()
    {
        var (feed, check) = Release();
        var updates = Service(feed, check);
        var s = await updates.CheckAsync(download: true, Ct);
        Assert.Equal("ready", s["state"]!.GetValue<string>());
        Assert.True(s["available"]!.GetValue<bool>());
        Assert.Equal("2026.11.20.1", s["latest"]!.GetValue<string>());
        var path = updates.InstallerToRun();
        Assert.Equal(Path.Combine(updates.Folder, "Yawble-Setup-2026.11.20.1-x64.exe"), path);
        Assert.Equal(Installer, File.ReadAllBytes(path));
        // Checked again: already there and still verifying, so nothing is downloaded twice.
        await updates.CheckAsync(download: true, Ct);
        Assert.Single(feed.Downloads);
    }

    [Fact]
    public async Task UpToDate_NothingIsDownloaded()
    {
        var (feed, check) = Release("2026.11.12.1");
        var s = await Service(feed, check).CheckAsync(download: true, Ct);
        Assert.Equal("upToDate", s["state"]!.GetValue<string>());
        Assert.False(s["available"]!.GetValue<bool>());
        Assert.Empty(feed.Downloads);
        feed.Latest = null;
        Assert.Equal("upToDate", (await Service(feed, check).CheckAsync(download: true, Ct))["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task WithAutomaticDownloadOff_ACheckOnlySaysItIsAvailable()
    {
        var (feed, check) = Release();
        var updates = Service(feed, check);
        var s = await updates.CheckAsync(download: false, Ct);
        Assert.Equal("available", s["state"]!.GetValue<string>());
        Assert.Empty(feed.Downloads);
        Assert.Equal("ready", (await updates.DownloadAsync(Ct))["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnInstallerThatDoesNotMatchItsHash_OrIsNotTheirs_IsDeleted_AndNeverRuns()
    {
        var (feed, check) = Release(installer: Encoding.UTF8.GetBytes("something else"));
        var updates = Service(feed, check);
        var s = await updates.CheckAsync(download: true, Ct);
        Assert.Equal("error", s["state"]!.GetValue<string>());
        Assert.Contains("did not match its SHA-256", s["error"]!.GetValue<string>());
        Assert.Empty(Directory.GetFiles(updates.Folder));
        Assert.Throws<InvalidOperationException>(() => updates.InstallerToRun());

        var (good, unsigned) = Release();
        unsigned.Answer = "it is signed by CN=Someone Else, not DJL Systems, Inc.";
        var other = Service(good, unsigned);
        var e = await other.CheckAsync(download: true, Ct);
        Assert.Contains("is not signed by the publisher", e["error"]!.GetValue<string>());
        Assert.Empty(Directory.GetFiles(other.Folder));
    }

    [Fact]
    public async Task FilesFromAnywhereElse_OrMissingFromTheSums_AreRefused()
    {
        var (feed, check) = Release(sumsFor: "another-file.exe");
        Assert.Contains("does not list", (await Service(feed, check).CheckAsync(download: true, Ct))["error"]!.GetValue<string>());

        var (moved, c2) = Release();
        moved.Latest = moved.Latest! with { Assets = [.. moved.Latest.Assets.Select(a => a with { Url = a.Url.Replace("github.com/djlsystems/Yawble", "evil.example/x") })] };
        Assert.Contains("not on this app's GitHub Releases", (await Service(moved, c2).CheckAsync(download: true, Ct))["error"]!.GetValue<string>());
        Assert.Empty(moved.Downloads);
    }

    [Fact]
    public async Task GitHubNotAnswering_IsAnError_NotACrash()
    {
        var (feed, check) = Release();
        feed.Fail = new HttpRequestException("no route to host");
        var s = await Service(feed, check).CheckAsync(download: true, Ct);
        Assert.Equal("error", s["state"]!.GetValue<string>());
        Assert.Contains("could not reach GitHub Releases", s["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task OffWindows_TheReleasePageIsOffered_NothingIsDownloaded()
    {
        var (feed, check) = Release();
        var updates = Service(feed, check, canInstall: false);
        var s = await updates.CheckAsync(download: true, Ct);
        Assert.Equal("available", s["state"]!.GetValue<string>());
        Assert.False(s["canInstall"]!.GetValue<bool>());
        Assert.Empty(feed.Downloads);
        Assert.Throws<InvalidOperationException>(() => updates.InstallerToRun());
    }

    [Fact]
    public void AutomaticChecks_AreOnUntilTurnedOff()
    {
        var (feed, check) = Release();
        var updates = Service(feed, check);
        Assert.True(updates.Auto());
        updates.SetAuto(false);
        Assert.False(updates.Auto());
        Assert.False(updates.Status()["auto"]!.GetValue<bool>());
    }

    [Fact]
    public async Task OnlyThePersonsWindowChecksDownloadsOrInstalls_ACloudViewerSeesTheStatus()
    {
        var (feed, check) = Release();
        var rpc = new UpdateRpc(Service(feed, check));
        using (Actor.Use(new Actor(Actor.Cloud, "viewer")))
        {
            Assert.NotNull(await rpc.DispatchAsync("updates.status", null, Ct));
            foreach (var m in new[] { "updates.check", "updates.download", "updates.setAuto", "updates.installer" })
                Assert.Equal("forbidden", (await Assert.ThrowsAsync<RpcException>(() => rpc.DispatchAsync(m, new JsonObject { ["on"] = false }, Ct))).Code);
        }
        Assert.Empty(feed.Downloads);
        using (Actor.Use(Actor.Window))
        {
            Assert.Equal("unavailable", (await Assert.ThrowsAsync<RpcException>(() => rpc.DispatchAsync("updates.installer", null, Ct))).Code);
            await rpc.DispatchAsync("updates.check", null, Ct);
            var path = (await rpc.DispatchAsync("updates.installer", null, Ct))!["path"]!.GetValue<string>();
            Assert.EndsWith("Yawble-Setup-2026.11.20.1-x64.exe", path);
        }
        Assert.Null(RemoteAccess.Refusal("updates.status", null, allowHostTerminals: false));
        Assert.Equal(UpdateRpc.LocalOnly, RemoteAccess.Refusal("updates.installer", null, allowHostTerminals: true));
    }

    [Fact]
    public void TheSumsFileIsRead_ByName()
    {
        var sums = $"{new string('a', 64)}  Yawble-Setup-1-x64.exe\r\n{new string('b', 64)} *Yawble-Setup-1-arm64.exe\n";
        Assert.Equal(new string('b', 64), UpdateService.ExpectedHash(sums, "Yawble-Setup-1-arm64.exe"));
        Assert.Null(UpdateService.ExpectedHash(sums, "other.exe"));
    }

    [Fact]
    public void TheRealSignatureCheck_RefusesAnUnsignedFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        var file = Path.Combine(_db.Dir, "unsigned.exe");
        File.WriteAllBytes(file, Installer);
        Assert.NotNull(new AuthenticodeCheck().Problem(file));
    }
}
