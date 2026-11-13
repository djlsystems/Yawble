using System.Diagnostics;
using Yawble.Core.Credentials;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Gateway;
using Xunit;

namespace Yawble.Core.Tests.Credentials;

/// <summary>
/// Credentials round-trip through the 0600 fallback with owner-only permissions
/// checked. Fake secrets only. Credential Manager, Keychain and a running Secret Service are NOT exercised here.
/// </summary>
public class CredentialServiceTests
{
    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void EveryCredential_RoundTripsThroughThe0600File_OwnerOnly()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the 0600 fallback is Linux's");
        using var db = new TempDb();
        var dataDir = Path.Combine(db.Dir, "data");
        var service = CredentialService.FileFallback(dataDir, db.ConnectionString);
        var file = Assert.IsType<FileCredentialStore>(service.Store);
        Assert.Equal("file-0600", service.Store.Kind);
        Assert.NotNull(service.FallbackWarning);
        Assert.Contains("only your account can read", service.FallbackWarning);

        foreach (var slot in CredentialSlots.All) Assert.Null(service.Get(slot));
        service.Set(CredentialSlot.ClaudeSetupToken, FakeSecrets.SetupToken);
        service.Set(CredentialSlot.AnthropicApiKey, "  " + FakeSecrets.AnthropicKey + "\n"); // pasted with whitespace
        service.Set(CredentialSlot.XaiApiKey, FakeSecrets.XaiKey);

        // Owner-only: exactly rw------- , checked by .NET and by stat(1) independently.
        Assert.Equal(FileCredentialStore.OwnerOnly, File.GetUnixFileMode(file.FilePath));
        Assert.Equal("600", Stat(file.FilePath));
        Assert.Empty(Directory.GetFiles(dataDir, "*.tmp")); // no leftover temp copies

        // A fresh service over the same folder (the next app start) reads them back.
        var again = CredentialService.FileFallback(dataDir, db.ConnectionString);
        Assert.Equal(FakeSecrets.SetupToken, again.Get(CredentialSlot.ClaudeSetupToken));
        Assert.Equal(FakeSecrets.AnthropicKey, again.Get(CredentialSlot.AnthropicApiKey));
        Assert.Equal(FakeSecrets.XaiKey, again.Get(CredentialSlot.XaiApiKey));

        Assert.True(again.Clear(CredentialSlot.XaiApiKey));
        Assert.False(again.Clear(CredentialSlot.XaiApiKey));
        Assert.Null(again.Get(CredentialSlot.XaiApiKey));
        Assert.Equal("600", Stat(file.FilePath));

        // Nothing secret in the database.
        var dbText = string.Concat(Directory.GetFiles(db.Dir, "yawble.db*").Select(p => System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(p))));
        foreach (var secret in FakeSecrets.All) Assert.DoesNotContain(secret, dbText);
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void AWidenedFile_IsNarrowedBackTo0600_OnTheNextRead()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix permissions");
        using var db = new TempDb();
        var repaired = new List<string>();
        var service = CredentialService.FileFallback(Path.Combine(db.Dir, "data"), db.ConnectionString, repaired.Add);
        service.Set(CredentialSlot.ClaudeSetupToken, FakeSecrets.SetupToken);
        var path = ((FileCredentialStore)service.Store).FilePath;
        File.SetUnixFileMode(path, FileCredentialStore.OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        Assert.Equal(FakeSecrets.SetupToken, service.Get(CredentialSlot.ClaudeSetupToken));
        Assert.Equal("600", Stat(path));
        Assert.Contains(repaired, l => l.Contains("narrowed to 0600"));
    }

    [Fact]
    public void TheSwitch_PicksTheCredentialPerMode_AndPersists()
    {
        using var db = new TempDb();
        var service = CredentialService.FileFallback(Path.Combine(db.Dir, "data"), db.ConnectionString);
        service.Set(CredentialSlot.ClaudeSetupToken, FakeSecrets.SetupToken);
        service.Set(CredentialSlot.AnthropicApiKey, FakeSecrets.AnthropicKey);

        Assert.Equal((ClaudeMode.Subscription, FakeSecrets.SetupToken), service.ClaudeCredential());
        service.ClaudeMode = ClaudeMode.ApiKey;
        Assert.Equal((ClaudeMode.ApiKey, FakeSecrets.AnthropicKey), service.ClaudeCredential());
        Assert.Equal(GrokMode.Session, service.GrokMode);
        service.GrokMode = GrokMode.ApiKey;

        var again = CredentialService.FileFallback(Path.Combine(db.Dir, "data"), db.ConnectionString);
        Assert.Equal(ClaudeMode.ApiKey, again.ClaudeMode);
        Assert.Equal(GrokMode.ApiKey, again.GrokMode);
        Assert.Throws<ArgumentException>(() => again.Set(CredentialSlot.XaiApiKey, "two words"));
        Assert.Throws<ArgumentException>(() => again.Set(CredentialSlot.XaiApiKey, "   "));
    }

    [Fact]
    public void TheSetupTokenIssueDate_DrivesTheRenewalWarning30DaysAhead()
    {
        using var db = new TempDb();
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero));
        var store = new FileCredentialStore(Path.Combine(db.Dir, "data"));
        var service = new CredentialService(store, db.ConnectionString, clock);
        Assert.False(service.ClaudeStatus().Present);
        service.Set(CredentialSlot.ClaudeSetupToken, FakeSecrets.SetupToken);
        var status = service.ClaudeStatus();
        Assert.True(status.Present);
        Assert.Equal(clock.Now, status.IssuedAt);
        Assert.False(status.RenewSoon);
        clock.Advance(TimeSpan.FromDays(334));
        Assert.False(service.ClaudeStatus().RenewSoon);
        clock.Advance(TimeSpan.FromDays(2));
        Assert.True(service.ClaudeStatus().RenewSoon);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void OnThisLinuxHost_WithNoSecretService_TheServiceFallsBackToTheFile()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only");
        Assert.SkipUnless(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")),
            "a D-Bus session exists here; this test proves only the no-Secret-Service fallback");
        using var db = new TempDb();
        var log = new List<string>();
        var probe = LinuxSecretServiceStore.Probe(out var reason);
        Assert.Null(probe);
        Assert.False(string.IsNullOrEmpty(reason));

        var service = CredentialService.ForThisMachine(Path.Combine(db.Dir, "data"), db.ConnectionString, log.Add);
        Assert.Equal("file-0600", service.Store.Kind);
        Assert.NotNull(service.FallbackWarning);
        Assert.Contains(log, l => l.Contains("owner-only file fallback"));
    }

    /// <summary>
    /// The octal permission bits of <paramref name="path"/> as stat(1) reports them: an independent check of what
    /// .NET says. GNU stat (Linux) takes <c>-c %a</c>; BSD stat (macOS) has no <c>-c</c> and takes <c>-f %Lp</c>.
    /// There is no stat(1) or POSIX mode on Windows: callers gate on that.
    /// </summary>
    internal static string Stat(string path)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("no POSIX modes on Windows");
        string[] args = OperatingSystem.IsLinux() ? ["-c", "%a", path] : ["-f", "%Lp", path];
        var psi = new ProcessStartInfo("stat", args) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var mode = p.StandardOutput.ReadToEnd().Trim();
        var error = p.StandardError.ReadToEnd().Trim();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"stat {string.Join(' ', args)} exited {p.ExitCode}: {error}");
        // Permission bits only: a setgid bit inherited from the parent folder (seen on this data volume) grants nothing.
        return mode.Length > 3 ? mode[^3..] : mode;
    }
}
