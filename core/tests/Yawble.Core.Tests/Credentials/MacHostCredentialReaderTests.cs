using Yawble.Core.Credentials;
using Yawble.Core.Tests.Gateway;
using Xunit;

namespace Yawble.Core.Tests.Credentials;

/// <summary>
/// A stand-in for macOS's /usr/bin/security: it records every argv it is handed and answers like the real tool for the
/// items it holds (found: the value on stdout with a trailing newline, exit 0; absent: exit 44), or fails or times out
/// when told to. No real program is ever run.
/// </summary>
public sealed class FakeSecurityTool : ISecurityTool
{
    private readonly Dictionary<string, string> _items = new(StringComparer.Ordinal);

    public List<IReadOnlyList<string>> Calls { get; } = [];
    public List<TimeSpan> Timeouts { get; } = [];

    /// <summary>When set, every run exits with this code (not 0, not 44) and writes this to stdout (what the real tool might echo).</summary>
    public (int ExitCode, string Stdout)? Fails { get; set; }

    public bool TimesOut { get; set; }

    public int Reads => Calls.Count(c => c.Contains("-w"));

    public FakeSecurityTool Set(string service, string value)
    {
        lock (_items) _items[service] = value;
        return this;
    }

    public SecurityToolResult Run(IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        lock (_items)
        {
            Calls.Add([.. arguments]);
            Timeouts.Add(timeout);
            if (TimesOut) return new SecurityToolResult(-1, "", timedOut: true);
            if (Fails is { } f) return new SecurityToolResult(f.ExitCode, f.Stdout);
            var i = arguments.ToList().IndexOf("-s");
            if (arguments[0] != "find-generic-password" || i < 0 || !_items.TryGetValue(arguments[i + 1], out var value))
                return new SecurityToolResult(MacHostCredentialReader.ExitItemNotFound, "");
            return arguments.Contains("-w")
                ? new SecurityToolResult(0, value + "\n")
                : new SecurityToolResult(0, $"keychain: \"/Users/alice/Library/Keychains/login.keychain-db\"\nattributes:\n    \"svce\"<blob>=\"{arguments[i + 1]}\"\n");
        }
    }
}

/// <summary>
/// B124: the macOS reader of Claude Code's Keychain item runs /usr/bin/security (absolute path, argv list, 5 s), reads
/// the value from stdout, treats exit 44 as absent and every other failure as unreadable with the exit code only, and
/// never writes, modifies or deletes the item. Fakes only.
/// </summary>
public class MacHostCredentialReaderTests
{
    private const string Service = "Claude Code-credentials";

    [Fact]
    public void ATimedOutTool_WhoseKillThrows_IsTimedOut_NotAnException()
    {
        // Process.Kill(entireProcessTree: true) throws Win32Exception when a member of the tree cannot be ended, and
        // InvalidOperationException when it already exited. Fakes for both steps: no process is started.
        var steps = new List<string>();
        foreach (Exception thrown in new Exception[] { new System.ComponentModel.Win32Exception(1, "fake: access denied"), new InvalidOperationException("fake: exited") })
        {
            var result = ProcessSecurityTool.EndTimedOut(
                () => { steps.Add("kill"); throw thrown; },
                () => { steps.Add("wait"); throw new System.ComponentModel.Win32Exception(1, "fake"); });
            Assert.True(result.TimedOut);
            Assert.Equal(-1, result.ExitCode);
            Assert.Equal("", result.StandardOutput);
        }
        Assert.Equal(["kill", "wait", "kill", "wait"], steps);
    }

    [Fact]
    public void TheToolIsTheAbsolutePath_WithAFiveSecondTimeout()
    {
        Assert.Equal("/usr/bin/security", ProcessSecurityTool.Path);
        Assert.Equal(TimeSpan.FromSeconds(5), MacHostCredentialReader.Timeout);
        Assert.Equal(44, MacHostCredentialReader.ExitItemNotFound);
    }

    [Fact]
    public void Found_TheValueComesFromStdout_TrailingNewlineTrimmed()
    {
        var json = FakeKeychain.ClaudeSignIn(FakeSecrets.SetupToken, DateTimeOffset.UtcNow.AddHours(4));
        var tool = new FakeSecurityTool().Set(Service, json);
        var reader = new MacHostCredentialReader(tool);

        Assert.Equal(json, reader.Read(Service));
        var call = Assert.Single(tool.Calls);
        Assert.Equal(["find-generic-password", "-s", Service, "-w"], call); // argv: the service is one word, spaces and all
        Assert.Equal(TimeSpan.FromSeconds(5), Assert.Single(tool.Timeouts));
    }

    [Fact]
    public void Found_AHexPrintedValueIsDecoded()
    {
        var json = FakeKeychain.ClaudeSignIn(FakeSecrets.SetupToken, DateTimeOffset.UtcNow.AddHours(4));
        var tool = new FakeSecurityTool().Set(Service, Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(json)).ToLowerInvariant());
        Assert.Equal(json, new MacHostCredentialReader(tool).Read(Service));
        // A plain value that happens to be hex but is not JSON stays as it is.
        Assert.Equal("abcd", new MacHostCredentialReader(new FakeSecurityTool().Set(Service, "abcd")).Read(Service));
    }

    [Fact]
    public void Exists_RunsTheSameCommandWithoutW_SoThePasswordIsNotRequested()
    {
        var tool = new FakeSecurityTool().Set(Service, "{\"claudeAiOauth\":{}}");
        var reader = new MacHostCredentialReader(tool);

        Assert.Equal([Service], reader.Targets(Service));
        Assert.Equal(["find-generic-password", "-s", Service], Assert.Single(tool.Calls));
        Assert.Equal(0, tool.Reads);
        Assert.Empty(reader.Targets("Claude Code-*")); // no wildcard lookups
        Assert.Empty(reader.Targets(""));
        Assert.Single(tool.Calls);
    }

    [Fact]
    public void NotFound_Exit44_IsAbsent()
    {
        var reader = new MacHostCredentialReader(new FakeSecurityTool());
        Assert.Null(reader.Read(Service));
        Assert.Empty(reader.Targets(Service));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(36)] // errSecInteractionNotAllowed-like
    [InlineData(51)]
    [InlineData(128)]
    public void OtherFailure_IsUnreadable_WithTheExitCodeOnly(int code)
    {
        const string echoed = "sk-ant-oat01-ECHOEDBYTHETOOL0123456789";
        var tool = new FakeSecurityTool { Fails = (code, echoed) };
        var reader = new MacHostCredentialReader(tool);

        var e = Assert.Throws<CredentialStoreException>(() => reader.Read(Service));
        Assert.Contains($"exited with code {code}", e.Message);
        Assert.DoesNotContain("ECHOED", e.Message);
        Assert.DoesNotContain("ECHOED", e.ToString());
        Assert.Empty(reader.Targets(Service)); // existence: not signed in, no throw
    }

    [Fact]
    public void Timeout_IsUnreadable_AndSaysSo()
    {
        var reader = new MacHostCredentialReader(new FakeSecurityTool { TimesOut = true });
        var e = Assert.Throws<CredentialStoreException>(() => reader.Read(Service));
        Assert.Contains("did not answer within 5 s", e.Message);
        Assert.Empty(reader.Targets(Service));
    }

    [Fact]
    public void AResultNeverShowsItsOutput()
    {
        const string value = "sk-ant-oat01-NEVERINTOSTRING0123456789";
        Assert.Equal("security: exit code 0", new SecurityToolResult(0, value).ToString());
        Assert.Equal("security: timed out", new SecurityToolResult(-1, value, timedOut: true).ToString());
    }

    // ---- Never written: no code path writes, modifies, deletes or changes the access of Claude Code's item.

    [Fact]
    public void TheReaderOnlyEverRunsFindGenericPassword()
    {
        var tool = new FakeSecurityTool().Set(Service, FakeKeychain.ClaudeSignIn(FakeSecrets.SetupToken, DateTimeOffset.UtcNow.AddHours(4)));
        var reader = new MacHostCredentialReader(tool);
        reader.Targets(Service);
        reader.Read(Service);
        tool.Fails = (1, "");
        try { reader.Read(Service); } catch (CredentialStoreException) { }
        reader.Targets(Service);

        Assert.Equal(4, tool.Calls.Count);
        Assert.All(tool.Calls, c => Assert.Equal("find-generic-password", c[0]));
    }

    [Fact]
    public void TheAppsOwnStoreRefusesClaudeCodesItem_ForAnyWriteOrDelete()
    {
        Assert.True(ForeignKeychainItems.IsForeign(ClaudeHostSession.KeychainService));
        Assert.False(ForeignKeychainItems.IsForeign(CredentialService.ServiceName));
        foreach (var op in new[] { "a write", "a delete", "a credential store" })
        {
            var e = Assert.Throws<CredentialStoreException>(() => ForeignKeychainItems.EnsureWritable(Service, op));
            Assert.Contains(op, e.Message);
        }
        ForeignKeychainItems.EnsureWritable(CredentialService.ServiceName, "a write"); // the app's own: allowed
#pragma warning disable CA1416 // the guard runs before any Keychain call, so this is safe off macOS
        Assert.Throws<CredentialStoreException>(() => new MacKeychainStore(ClaudeHostSession.KeychainService));
#pragma warning restore CA1416
    }

    /// <summary>
    /// No source file of the core asks macOS to add, change, delete or re-permission a Keychain item other than through
    /// the app's own store (which refuses Claude Code's service, above), and nothing but MacKeychainStore calls the
    /// Security framework. A new code path that would write Claude Code's item fails here.
    /// </summary>
    [Fact]
    public void NoCodePathCanWriteAnotherProgramsKeychainItem()
    {
        var src = Path.Combine(Binaries.RepoRoot, "core", "src");
        string[] writes =
        [
            "add-generic-password", "delete-generic-password", "set-generic-password-partition-list", "set-key-partition-list",
            "add-internet-password", "delete-internet-password", "SecItemAdd", "SecItemUpdate", "SecItemDelete",
            "SecKeychainItemSetAccess", "SecAccessCreate", "SecKeychainItemSetAccessWithPassword",
        ];
        var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file).Replace('\\', '/');
            if (rel.Contains("/obj/") || rel.Contains("/bin/")) continue;
            var text = File.ReadAllText(file);
            problems.AddRange(writes.Where(w => text.Contains(w, StringComparison.Ordinal)).Select(w => $"{rel}: {w}"));
            if (!rel.EndsWith("Credentials/MacKeychainStore.cs", StringComparison.Ordinal) && text.Contains("Security.framework", StringComparison.Ordinal))
                problems.Add($"{rel}: calls the Security framework");
            if (text.Contains("new ProcessSecurityTool(", StringComparison.Ordinal) && !rel.EndsWith("Credentials/HostCredentialReader.cs", StringComparison.Ordinal))
                problems.Add($"{rel}: runs /usr/bin/security outside the reader");
        }
        // The app's own store guards each write and delete before any Keychain call.
        var store = File.ReadAllText(Path.Combine(src, "Yawble.Core", "Credentials", "MacKeychainStore.cs"));
        Assert.Contains("ForeignKeychainItems.EnsureWritable(service, \"a write\");", store);
        Assert.Contains("ForeignKeychainItems.EnsureWritable(service, \"a delete\");", store);
        Assert.DoesNotContain("ReadItem", store);
        Assert.DoesNotContain("ItemExists", store);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
