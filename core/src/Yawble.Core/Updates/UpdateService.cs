using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Yawble.Core.Updates;

/// <summary>A file of a release: its name, where it downloads from, its size.</summary>
public sealed record ReleaseAsset(string Name, string Url, long Size);

/// <summary>A published release: its version (yyyy.mm.dd.N), tag, page, and files.</summary>
public sealed record ReleaseInfo(string Version, string Tag, string PageUrl, IReadOnlyList<ReleaseAsset> Assets);

/// <summary>Where releases come from. Tests pass a fake: no test reaches the network.</summary>
public interface IReleaseFeed
{
    /// <summary>The newest published release (never a draft or a prerelease), or null when there is none.</summary>
    Task<ReleaseInfo?> LatestAsync(CancellationToken ct);

    Task<string> ReadTextAsync(string url, CancellationToken ct);

    /// <summary>Downloads to <paramref name="file"/>, reporting the bytes so far.</summary>
    Task DownloadAsync(string url, string file, Action<long>? progress, CancellationToken ct);
}

/// <summary>Whether a downloaded installer may run. Tests pass a fake.</summary>
public interface IInstallerCheck
{
    /// <summary>Null when the file may run (a valid signature by the app's publisher, or on Linux a sound tarball); otherwise why not.</summary>
    string? Problem(string file);

    /// <summary>Whether it checks the publisher's signature (Windows), or only what the file holds (Linux: no signature).</summary>
    bool ChecksPublisher => true;
}

/// <summary>The release versions (yyyy.mm.dd.N) and how a build's version compares with them.</summary>
public static class ReleaseVersion
{
    /// <summary>A release version, or a build's (its release part: "2026.11.12.1+dev.abc" is 2026.11.12.1; "0.0.0+dev..." is 0).</summary>
    public static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var core = text.Trim().TrimStart('v').Split('+')[0];
        var parts = core.Split('.');
        if (parts.Length is < 3 or > 4 || !parts.All(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _))) return null;
        var n = parts.Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        return n.Length == 4 ? new Version(n[0], n[1], n[2], n[3]) : new Version(n[0], n[1], n[2]);
    }

    /// <summary>Whether <paramref name="latest"/> is a newer release than the build <paramref name="current"/>.</summary>
    public static bool IsNewer(string latest, string current)
    {
        var l = Parse(latest);
        var c = Parse(current) ?? new Version(0, 0, 0);
        return l is not null && l > c;
    }
}

/// <summary>
/// Updates to the app itself: GitHub Releases is checked on start and every few hours (when automatic checks are on,
/// as they are by default) or when the person asks. A newer release's installer for this computer is downloaded to
/// &lt;data&gt;/updates/, then verified: its SHA-256 must match the release's SHA256SUMS, and it must carry a valid
/// signature by the publisher. Only then is it ready, and only the window installs it (the shell quits and runs it).
/// On Linux (os "linux") the file is the release's tarball, listed in SHA256SUMS-linux, and it has no signature: its
/// SHA-256 and its layout (<see cref="LinuxTarballCheck"/>) are checked, and the shell's apply-update.sh installs it over
/// an install.sh install. Where the app cannot replace itself (canInstall false) the release's page is offered.
/// </summary>
public sealed class UpdateService(
    string dataDir, string connectionString, string currentVersion, IReleaseFeed feed, IInstallerCheck check,
    bool canInstall, string arch, TimeProvider? clock = null, Action<string>? log = null, Func<bool>? managed = null, string os = "win")
{
    public const string ManagedMessage = "updates to this app are managed by your organization";

    /// <summary>Whether the organization manages updates (<see cref="UpdatePolicy"/>): then the app checks nothing.</summary>
    public bool Managed => managed?.Invoke() ?? false;

    public const string AutoSetting = "updates.auto";
    public const string ReleasesUrl = "https://github.com/djlsystems/Yawble/releases";
    public static readonly TimeSpan Every = TimeSpan.FromHours(6);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _work = new(1, 1);

    private string _state = "idle"; // idle, checking, upToDate, available, downloading, ready, error
    private ReleaseInfo? _latest;
    private string? _error;
    private string? _checkedAt;
    private long _downloaded;
    private long _size;
    private string? _installer;

    public string Folder => Path.Combine(dataDir, "updates");

    private bool Linux => os == "linux";

    public string InstallerName(string version) => Linux ? $"yawble-{version}-linux-{arch}.tar.gz" : $"Yawble-Setup-{version}-{arch}.exe";

    /// <summary>The release's checksum file for this computer's installer: Windows and Linux each have their own.</summary>
    public string SumsName => Linux ? "SHA256SUMS-linux" : "SHA256SUMS";

    private string OldPattern => Linux ? "yawble-*-linux-*" : "Yawble-Setup-*";

    public JsonObject Status()
    {
        lock (_lock)
        {
            return new JsonObject
            {
                ["current"] = currentVersion,
                ["state"] = _state,
                ["latest"] = _latest?.Version,
                ["available"] = _latest is not null && ReleaseVersion.IsNewer(_latest.Version, currentVersion),
                ["notesUrl"] = _latest?.PageUrl ?? ReleasesUrl,
                ["canInstall"] = canInstall,
                ["checksPublisher"] = check.ChecksPublisher,
                ["auto"] = Auto(),
                ["managed"] = Managed,
                ["checkedAt"] = _checkedAt,
                ["error"] = _error,
                ["downloaded"] = _downloaded,
                ["size"] = _size,
            };
        }
    }

    private void Set(string state, string? error = null)
    {
        lock (_lock) { _state = state; _error = error; }
    }

    // ------------------------------------------------------------------ the setting

    public bool Auto()
    {
        using var c = new SqliteConnection(connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM app_meta WHERE key = @k;";
        cmd.Parameters.AddWithValue("@k", AutoSetting);
        return cmd.ExecuteScalar() is not string v || v != "false";
    }

    public void SetAuto(bool on)
    {
        using var c = new SqliteConnection(connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO app_meta (key, value) VALUES (@k, @v) ON CONFLICT(key) DO UPDATE SET value = @v;";
        cmd.Parameters.AddWithValue("@k", AutoSetting);
        cmd.Parameters.AddWithValue("@v", on ? "true" : "false");
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------ check, download

    /// <summary>Asks for the newest release; with <paramref name="download"/>, a newer one is downloaded and verified too.</summary>
    public async Task<JsonObject> CheckAsync(bool download, CancellationToken ct)
    {
        if (Managed) { Set("managed"); return Status(); }
        if (!await _work.WaitAsync(0, ct).ConfigureAwait(false)) return Status(); // a check or download is under way
        try
        {
            Set("checking");
            ReleaseInfo? latest;
            try { latest = await feed.LatestAsync(ct).ConfigureAwait(false); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                Set("error", $"could not reach GitHub Releases: {e.Message}");
                return Status();
            }
            lock (_lock)
            {
                _latest = latest;
                _checkedAt = _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
            }
            if (latest is null || !ReleaseVersion.IsNewer(latest.Version, currentVersion)) { Set("upToDate"); return Status(); }
            if (Ready(latest.Version)) { Set("ready"); return Status(); }
            Set("available");
            if (download && canInstall) await DownloadLockedAsync(latest, ct).ConfigureAwait(false);
            return Status();
        }
        finally
        {
            _work.Release();
        }
    }

    /// <summary>Downloads and verifies the newest release's installer (after a check found it).</summary>
    public async Task<JsonObject> DownloadAsync(CancellationToken ct)
    {
        if (Managed) throw new InvalidOperationException(ManagedMessage);
        if (!canInstall) throw new InvalidOperationException($"updates are installed from the release page on this computer: {ReleasesUrl}");
        ReleaseInfo? latest;
        lock (_lock) latest = _latest;
        if (latest is null || !ReleaseVersion.IsNewer(latest.Version, currentVersion)) return await CheckAsync(download: true, ct).ConfigureAwait(false);
        if (!await _work.WaitAsync(0, ct).ConfigureAwait(false)) return Status();
        try
        {
            if (!Ready(latest.Version)) await DownloadLockedAsync(latest, ct).ConfigureAwait(false);
            else Set("ready");
            return Status();
        }
        finally
        {
            _work.Release();
        }
    }

    private async Task DownloadLockedAsync(ReleaseInfo release, CancellationToken ct)
    {
        var name = InstallerName(release.Version);
        var asset = release.Assets.FirstOrDefault(a => a.Name == name);
        var sums = release.Assets.FirstOrDefault(a => a.Name == SumsName);
        if (asset is null || sums is null)
        {
            Set("error", $"release {release.Version} has no {name} with a {SumsName} for this computer");
            return;
        }
        if (!FromThisRepository(asset.Url) || !FromThisRepository(sums.Url))
        {
            Set("error", "the release's files are not on this app's GitHub Releases");
            return;
        }
        Directory.CreateDirectory(Folder);
        var file = Path.Combine(Folder, name);
        var part = file + ".part";
        lock (_lock) { _downloaded = 0; _size = asset.Size; _state = "downloading"; _error = null; }
        try
        {
            var expected = ExpectedHash(await feed.ReadTextAsync(sums.Url, ct).ConfigureAwait(false), name);
            if (expected is null) { Set("error", $"{SumsName} does not list {name}"); return; }
            File.Delete(part);
            await feed.DownloadAsync(asset.Url, part, n => { lock (_lock) _downloaded = n; }, ct).ConfigureAwait(false);
            if (!string.Equals(Hash(part), expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(part);
                Set("error", $"{name} did not match its SHA-256 in {SumsName}; it was deleted");
                return;
            }
            if (check.Problem(part) is { } problem)
            {
                File.Delete(part);
                Set("error", check.ChecksPublisher
                    ? $"{name} is not signed by the publisher ({problem}); it was deleted"
                    : $"{name} is not a sound Yawble tarball ({problem}); it was deleted");
                return;
            }
            File.Move(part, file, overwrite: true);
            File.WriteAllText(file + ".sha256", expected);
            foreach (var old in Directory.GetFiles(Folder, OldPattern).Where(f => !f.StartsWith(file, StringComparison.OrdinalIgnoreCase)))
                TryDelete(old);
            lock (_lock) _installer = file;
            Set("ready");
            log?.Invoke($"Update: {name} downloaded and verified");
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or UnauthorizedAccessException)
        {
            TryDelete(part);
            Set("error", $"the download did not finish: {e.Message}");
        }
    }

    /// <summary>Whether the installer for <paramref name="version"/> is already downloaded and still verifies.</summary>
    private bool Ready(string version)
    {
        var file = Path.Combine(Folder, InstallerName(version));
        if (!File.Exists(file) || !File.Exists(file + ".sha256")) return false;
        if (!string.Equals(Hash(file), File.ReadAllText(file + ".sha256").Trim(), StringComparison.OrdinalIgnoreCase) || check.Problem(file) is not null) return false;
        lock (_lock) _installer = file;
        return true;
    }

    /// <summary>
    /// The installer to run, verified again now (hash and signature): the window quits and runs it. Throws when no newer
    /// release is ready.
    /// </summary>
    public string InstallerToRun()
    {
        ReleaseInfo? latest;
        lock (_lock) latest = _latest;
        if (Managed) throw new InvalidOperationException(ManagedMessage);
        if (!canInstall) throw new InvalidOperationException($"updates are installed from the release page on this computer: {ReleasesUrl}");
        if (latest is null || !ReleaseVersion.IsNewer(latest.Version, currentVersion) || !Ready(latest.Version))
            throw new InvalidOperationException("no update is downloaded and verified yet");
        lock (_lock) return _installer!;
    }

    /// <summary>When automatic checks are on: a minute after start, then every <see cref="Every"/>, check and download.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), _clock, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            if (Auto() && !Managed)
            {
                try { await CheckAsync(download: true, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (Exception e) when (e is SqliteException or IOException) { log?.Invoke($"Update: the check failed: {e.Message}"); }
            }
            try { await Task.Delay(Every, _clock, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    // ------------------------------------------------------------------ helpers

    public static bool FromThisRepository(string url) =>
        url.StartsWith(ReleasesUrl + "/download/", StringComparison.Ordinal);

    /// <summary>The SHA-256 SHA256SUMS gives a file ("&lt;hex&gt;  &lt;name&gt;" lines), or null.</summary>
    public static string? ExpectedHash(string sums, string name)
    {
        foreach (var line in sums.Replace("\r\n", "\n").Split('\n'))
        {
            var parts = line.Trim().Split((char[])[' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*').Trim() == name && parts[0].Length == 64) return parts[0];
        }
        return null;
    }

    private static string Hash(string file)
    {
        using var s = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// The organization's update policy (Group Policy, Intune or any tool that writes the registry): the DWORD
/// HKLM\SOFTWARE\Policies\DJL Systems\Yawble\UpdateChecks = 0 turns the app's own update checks off, because IT
/// deploys new versions itself. Any other value, or none, leaves them to the person (Settings > General > Updates).
/// </summary>
public static class UpdatePolicy
{
    public const string Key = @"SOFTWARE\Policies\DJL Systems\Yawble";
    public const string Value = "UpdateChecks";

    public static bool Managed()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(Key);
        return key?.GetValue(Value) is int v && v == 0;
    }
}

/// <summary>GitHub Releases of the app's repository, over HTTPS.</summary>
public sealed class GitHubReleaseFeed(HttpClient http) : IReleaseFeed
{
    public const string LatestApi = "https://api.github.com/repos/djlsystems/Yawble/releases/latest";

    public async Task<ReleaseInfo?> LatestAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestApi);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonObject>(ct).ConfigureAwait(false);
        var tag = json?["tag_name"]?.GetValue<string>();
        if (tag is null || ReleaseVersion.Parse(tag) is null) return null;
        var assets = (json!["assets"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(a => new ReleaseAsset(a["name"]?.GetValue<string>() ?? "", a["browser_download_url"]?.GetValue<string>() ?? "", a["size"]?.GetValue<long>() ?? 0))
            .Where(a => a.Name.Length > 0 && a.Url.Length > 0)
            .ToList();
        return new ReleaseInfo(tag.TrimStart('v'), tag, json["html_url"]?.GetValue<string>() ?? UpdateService.ReleasesUrl, assets);
    }

    public async Task<string> ReadTextAsync(string url, CancellationToken ct) => await http.GetStringAsync(url, ct).ConfigureAwait(false);

    public async Task DownloadAsync(string url, string file, Action<long>? progress, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = File.Create(file);
        var buffer = new byte[1 << 16];
        long total = 0;
        int n;
        while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            total += n;
            progress?.Invoke(total);
        }
    }
}

/// <summary>
/// Windows: the installer's Authenticode signature is valid and trusted (WinVerifyTrust, revocation checked against the
/// whole chain), and its signer is the publisher (O=<see cref="Publisher"/>).
/// </summary>
public sealed class AuthenticodeCheck : IInstallerCheck
{
    public const string Publisher = "DJL Systems, Inc.";

    public string? Problem(string file)
    {
        if (!OperatingSystem.IsWindows()) return "installers are verified on Windows only";
        var status = WinTrust.Verify(file);
        if (status != 0) return $"its signature is not valid (WinVerifyTrust 0x{status:X8})";
        try
        {
#pragma warning disable SYSLIB0057 // reading the signer of a signed file; there is no replacement API for this
            using var cert = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(file);
#pragma warning restore SYSLIB0057
            var subject = cert.Subject;
            return subject.Contains($"O=\"{Publisher}\"", StringComparison.Ordinal) || subject.Contains($"O={Publisher}", StringComparison.Ordinal)
                ? null
                : $"it is signed by {subject}, not {Publisher}";
        }
        catch (CryptographicException e)
        {
            return $"its signer could not be read: {e.Message}";
        }
    }

    private static class WinTrust
    {
        private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FileInfo
        {
            public uint cbStruct;
            public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Data
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref Data data);

        public static int Verify(string file)
        {
            var info = new FileInfo { cbStruct = (uint)Marshal.SizeOf<FileInfo>(), pcwszFilePath = file };
            var infoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfo>());
            try
            {
                Marshal.StructureToPtr(info, infoPtr, false);
                var data = new Data
                {
                    cbStruct = (uint)Marshal.SizeOf<Data>(),
                    dwUIChoice = 2, // WTD_UI_NONE
                    fdwRevocationChecks = 1, // WTD_REVOKE_WHOLECHAIN
                    dwUnionChoice = 1, // WTD_CHOICE_FILE
                    pFile = infoPtr,
                    dwStateAction = 1, // WTD_STATEACTION_VERIFY
                    dwProvFlags = 0x80, // WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT
                };
                var result = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);
                data.dwStateAction = 2; // WTD_STATEACTION_CLOSE
                WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);
                return result;
            }
            finally
            {
                Marshal.DestroyStructure<FileInfo>(infoPtr);
                Marshal.FreeHGlobal(infoPtr);
            }
        }
    }
}
