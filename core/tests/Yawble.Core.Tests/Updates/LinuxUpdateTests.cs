using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Updates;
using Xunit;

namespace Yawble.Core.Tests.Updates;

/// <summary>
/// Linux updates: the release's tarball is found by its name and its own SHA256SUMS-linux, checked for what it holds
/// (one app folder, nothing leading outside it), and offered only to an install.sh install the person may change. The
/// feed is a fake and the tarballs are made here: nothing reaches the network and nothing is unpacked or run.
/// </summary>
public sealed class LinuxUpdateTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Version = "2026.11.20.1";
    private const string Base = UpdateService.ReleasesUrl + "/download/v2026.11.20.1/";
    private static readonly string Tarball = $"yawble-{Version}-linux-x64.tar.gz";
    private readonly TempDb _db = new();
    private readonly string _dir = Directory.CreateTempSubdirectory("yawble-linux-update-").FullName;

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed class Feed : IReleaseFeed
    {
        public ReleaseInfo? Latest { get; set; }
        public Dictionary<string, byte[]> Files { get; } = [];
        public List<string> Downloads { get; } = [];
        public Task<ReleaseInfo?> LatestAsync(CancellationToken ct) => Task.FromResult(Latest);
        public Task<string> ReadTextAsync(string url, CancellationToken ct) => Task.FromResult(Encoding.UTF8.GetString(Files[url]));
        public async Task DownloadAsync(string url, string file, Action<long>? progress, CancellationToken ct)
        {
            Downloads.Add(url);
            await File.WriteAllBytesAsync(file, Files[url], ct);
        }
    }

    /// <summary>A gzipped tar of these entries: (name, kind, link target).</summary>
    private static byte[] Tar(params (string Name, TarEntryType Type, string? Link)[] entries)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gz, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (name, type, link) in entries)
            {
                var e = new PaxTarEntry(type, name);
                if (link is not null) e.LinkName = link;
                if (type == TarEntryType.RegularFile) e.DataStream = new MemoryStream(Encoding.UTF8.GetBytes("x"));
                tar.WriteEntry(e);
            }
        }
        return ms.ToArray();
    }

    private static (string, TarEntryType, string?) F(string name) => (name, TarEntryType.RegularFile, null);

    private static byte[] GoodTarball(string root = "yawble-2026.11.20.1-linux-x64") => Tar(
        (root + "/", TarEntryType.Directory, null), F($"{root}/yawble"), F($"{root}/install.sh"), F($"{root}/apply-update.sh"),
        F($"{root}/yawble-launch.sh"), (root + "/resources/core/", TarEntryType.Directory, null), F($"{root}/resources/core/yawble-core"),
        ($"{root}/libffmpeg-link.so", TarEntryType.SymbolicLink, "libffmpeg.so"), F($"{root}/libffmpeg.so"));

    private string Write(byte[] bytes)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".tar.gz");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string? Check(string file) => new LinuxTarballCheck().Problem(file);

    [Fact]
    public void ASoundTarball_Passes_AndTheCheckSaysItIsNotASignature()
    {
        Assert.Null(Check(Write(GoodTarball())));
        Assert.False(new LinuxTarballCheck().ChecksPublisher);
    }

    [Fact]
    public void ATarballThatLeadsOutside_OrIsNotTheApp_IsRefused()
    {
        const string r = "yawble-x";
        Assert.Contains("not a plain relative path", Check(Write(Tar(F($"{r}/yawble"), F($"{r}/../evil")))));
        Assert.Contains("not a plain relative path", Check(Write(Tar(F("/etc/evil")))));
        Assert.Contains("points outside", Check(Write(Tar(F($"{r}/yawble"), ($"{r}/link", TarEntryType.SymbolicLink, "../../.bashrc")))));
        Assert.Contains("points outside", Check(Write(Tar(F($"{r}/yawble"), ($"{r}/link", TarEntryType.SymbolicLink, "/home/someone/.ssh")))));
        Assert.Contains("more than one top folder", Check(Write(Tar(F($"{r}/yawble"), F("other/file")))));
        Assert.Contains("is a CharacterDevice", Check(Write(Tar(F($"{r}/yawble"), ($"{r}/dev", TarEntryType.CharacterDevice, null)))));
        Assert.Contains("it has no install.sh, apply-update.sh", Check(Write(Tar(F($"{r}/yawble"), F($"{r}/yawble-launch.sh"), F($"{r}/resources/core/yawble-core")))));
        Assert.Contains("could not be read", Check(Write(Encoding.UTF8.GetBytes("not a tarball"))));
    }

    private (Feed Feed, UpdateService Service) LinuxRelease(byte[] tarball, string? sumsName = null, string? listed = null)
    {
        var feed = new Feed();
        var hash = Convert.ToHexString(SHA256.HashData(tarball)).ToLowerInvariant();
        var sums = sumsName ?? "SHA256SUMS-linux";
        feed.Files[Base + Tarball] = tarball;
        feed.Files[Base + sums] = Encoding.UTF8.GetBytes($"{hash}  {listed ?? Tarball}\n");
        feed.Files[Base + "SHA256SUMS"] = Encoding.UTF8.GetBytes($"{new string('0', 64)}  Yawble-Setup-{Version}-x64.exe\n");
        feed.Latest = new ReleaseInfo(Version, "v" + Version, UpdateService.ReleasesUrl + "/tag/v" + Version,
        [
            new ReleaseAsset($"Yawble-Setup-{Version}-x64.exe", Base + $"Yawble-Setup-{Version}-x64.exe", 10),
            new ReleaseAsset("SHA256SUMS", Base + "SHA256SUMS", 100),
            new ReleaseAsset(Tarball, Base + Tarball, tarball.Length),
            new ReleaseAsset(sums, Base + sums, 100),
        ]);
        return (feed, new UpdateService(_db.Dir, _db.ConnectionString, "2026.11.12.1", feed, new LinuxTarballCheck(), canInstall: true, "x64", os: "linux"));
    }

    [Fact]
    public async Task OnLinux_TheReleasesTarball_IsDownloaded_CheckedAgainstSha256SumsLinux_AndReady()
    {
        var (feed, updates) = LinuxRelease(GoodTarball());
        var s = await updates.CheckAsync(download: true, Ct);
        Assert.Equal("ready", s["state"]!.GetValue<string>());
        Assert.False(s["checksPublisher"]!.GetValue<bool>());
        Assert.Equal(Path.Combine(updates.Folder, Tarball), updates.InstallerToRun());
        Assert.Equal([Base + Tarball], feed.Downloads);
        Assert.Equal("SHA256SUMS-linux", updates.SumsName);
    }

    [Fact]
    public async Task OnLinux_ATarballOnlyInTheWindowsSums_OrNotSound_IsRefusedAndDeleted()
    {
        var (_, noLinuxSums) = LinuxRelease(GoodTarball(), sumsName: "SHA256SUMS-other");
        var a = await noLinuxSums.CheckAsync(download: true, Ct);
        Assert.Contains("has no yawble-2026.11.20.1-linux-x64.tar.gz with a SHA256SUMS-linux", a["error"]!.GetValue<string>());

        var (_, bad) = LinuxRelease(Tar(F("yawble-x/yawble"), ("yawble-x/l", TarEntryType.SymbolicLink, "../../x")));
        var b = await bad.CheckAsync(download: true, Ct);
        Assert.Contains("is not a sound Yawble tarball", b["error"]!.GetValue<string>());
        Assert.Empty(Directory.GetFiles(bad.Folder));
        Assert.Throws<InvalidOperationException>(() => bad.InstallerToRun());
    }

    [Fact]
    public void OnWindows_TheNamesAreUnchanged()
    {
        var w = new UpdateService(_db.Dir, _db.ConnectionString, "2026.11.12.1", new Feed(), new LinuxTarballCheck(), canInstall: true, "x64");
        Assert.Equal($"Yawble-Setup-{Version}-x64.exe", w.InstallerName(Version));
        Assert.Equal("SHA256SUMS", w.SumsName);
    }

    private string Install(params string[] files)
    {
        var prefix = Path.Combine(_dir, "opt", "yawble");
        Directory.CreateDirectory(Path.Combine(prefix, "resources", "core"));
        foreach (var f in files) File.WriteAllText(Path.Combine(prefix, f), "x");
        return prefix;
    }

    [Fact]
    public void OnlyAnInstallShInstallThePersonMayChange_UpdatesItself()
    {
        var prefix = Install("yawble", "install.sh", "apply-update.sh");
        var core = Path.Combine(prefix, "resources", "core") + Path.DirectorySeparatorChar;
        Assert.Equal(Path.GetFullPath(prefix), LinuxSelfUpdate.InstallFolder(core, _ => true));
        // Not writable (a system-wide install in /opt): the release page instead.
        Assert.Null(LinuxSelfUpdate.InstallFolder(core, f => f != Path.GetFullPath(prefix)));
        Assert.Null(LinuxSelfUpdate.InstallFolder(core, f => f == Path.GetFullPath(prefix)));
        // A development run: the core is not in <install>/resources/core.
        Assert.Null(LinuxSelfUpdate.InstallFolder(Path.Combine(_dir, "bin", "Debug"), _ => true));
    }

    [Fact]
    public void AnInstallWithoutTheUpdateScript_DoesNotUpdateItself()
    {
        var prefix = Install("yawble", "install.sh");
        Assert.Null(LinuxSelfUpdate.InstallFolder(Path.Combine(prefix, "resources", "core"), _ => true));
    }
}
