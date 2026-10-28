using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// THE ROWS' BROWSER PROFILES ON A WORKER, without a browser: where a key puts one, what a start
/// writes into it (the person's languages, a clean last exit, no stale single-instance lock), what a
/// delete removes, and that Chrome is started as an ordinary browser - never headless, never with an
/// automation switch or without its sandbox.
/// </summary>
public sealed class SignInProfilesTests : IDisposable
{
    private const string MemberKey = "0123456789abcdef0123456789abcdef";
    private const string RowKey = MemberKey + "/fedcba9876543210fedcba9876543210";
    private const string OtherRow = MemberKey + "/00000000000000000000000000000000";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sign-in-profiles-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class NoSink : IRunStreamSink
    {
        public ValueTask<bool> StreamAsync(StreamChunk chunk, CancellationToken ct = default) => ValueTask.FromResult(true);
    }

    private string Make(string key)
    {
        var folder = SignInProfiles.Folder(_root, key);
        Directory.CreateDirectory(Path.Combine(folder, "chrome", "Default"));
        File.WriteAllText(Path.Combine(folder, "chrome", "Default", "Cookies"), "x");
        File.WriteAllText(folder + ".lock", "");
        return folder;
    }

    [Theory]
    [InlineData(RowKey, true, true)]
    [InlineData(MemberKey, false, true)]
    [InlineData("../" + MemberKey, false, false)]
    [InlineData(MemberKey + "/..", false, false)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF", false, false)]
    [InlineData("", false, false)]
    public void Only_hashes_name_a_profile(string key, bool row, bool any)
    {
        Assert.Equal(row, SignInProfiles.IsKey(key));
        Assert.Equal(any, SignInProfiles.IsMemberOrRowKey(key));
        if (!any) Assert.Throws<ArgumentException>(() => SignInProfiles.Folder(_root, key));
    }

    [Fact]
    public async Task Deleting_a_row_removes_its_profile_and_deleting_a_member_removes_every_row_of_its()
    {
        var browsers = new WorkerSignInBrowsers(new NoSink(), _ => Task.CompletedTask, profileRoot: _root);
        var row = Make(RowKey);
        var other = Make(OtherRow);

        await browsers.DeleteProfilesAsync(RowKey);
        Assert.False(Directory.Exists(row));
        Assert.False(File.Exists(row + ".lock"));
        Assert.True(Directory.Exists(other));

        await browsers.DeleteProfilesAsync("../" + MemberKey);
        Assert.True(Directory.Exists(other));

        await browsers.DeleteProfilesAsync(MemberKey);
        Assert.False(Directory.Exists(SignInProfiles.Folder(_root, MemberKey)));
    }

    [Fact]
    public void A_start_sets_the_persons_languages_keeps_the_rest_and_clears_a_stale_lock()
    {
        var data = Path.Combine(Make(RowKey), "chrome");
        File.WriteAllText(Path.Combine(data, "Default", "Preferences"), """{"intl":{"accept_languages":"en-US,en"},"kept":{"a":1}}""");
        File.CreateSymbolicLink(Path.Combine(data, "SingletonLock"), "other-host-123");

        SignInProfiles.Prepare(data, ["de-DE", "de"]);

        var preferences = JsonNode.Parse(File.ReadAllText(Path.Combine(data, "Default", "Preferences")))!;
        Assert.Equal("de-DE,de", (string?)preferences["intl"]!["accept_languages"]);
        Assert.Equal("Normal", (string?)preferences["profile"]!["exit_type"]);
        Assert.Equal(1, (int?)preferences["kept"]!["a"]);
        Assert.Null(new FileInfo(Path.Combine(data, "SingletonLock")).LinkTarget);
    }

    [Fact]
    public void The_persons_languages_and_time_zone_are_taken_only_when_they_are_ones()
    {
        Assert.Equal(["de-DE", "de"], ChromiumSignInBrowser.Languages(["de-DE", "de", "DE", "--headless", "a b"]));
        Assert.Equal(["en-US", "en"], ChromiumSignInBrowser.Languages(null));
        Assert.Equal(["en-US", "en"], ChromiumSignInBrowser.Languages(["--lang=x"]));
        Assert.Equal(10, ChromiumSignInBrowser.Languages([.. Enumerable.Range(0, 20).Select(i => "x" + (char)('a' + i))]).Count);

        if (File.Exists("/usr/share/zoneinfo/Europe/Berlin")) Assert.Equal("Europe/Berlin", ChromiumSignInBrowser.TimeZone("Europe/Berlin"));
        Assert.Equal("UTC", ChromiumSignInBrowser.TimeZone("../../etc/passwd"));
        Assert.Equal("UTC", ChromiumSignInBrowser.TimeZone("Not/A_Zone"));
        Assert.Equal("UTC", ChromiumSignInBrowser.TimeZone(null));
    }

    [Fact]
    public void Chrome_starts_as_an_ordinary_sandboxed_browser()
    {
        var arguments = ChromiumSignInBrowser.Arguments("/p", (1280, 887), (320, 96), "de-DE");

        foreach (var forbidden in new[] { "--headless", "--no-sandbox", "--enable-automation", "--disable-setuid-sandbox", "--single-process", "--no-zygote" })
        {
            Assert.DoesNotContain(arguments, a => a.StartsWith(forbidden, StringComparison.Ordinal));
        }

        Assert.Contains("--disable-blink-features=AutomationControlled", arguments);
        Assert.Contains("--lang=de-DE", arguments);
        Assert.Contains("--window-size=1280,887", arguments);
        Assert.DoesNotContain(arguments, a => a.StartsWith("--remote-debugging-port", StringComparison.Ordinal));
    }
}
