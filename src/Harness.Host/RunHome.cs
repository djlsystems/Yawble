using System.Diagnostics;

namespace Harness.Host;

/// <summary>
/// A HOME OF ONE RUN'S OWN, for a run whose preset signs in with an issued credential:
/// <c>&lt;TMPDIR&gt;/home-&lt;random&gt;</c>, owner-only, made as the agent and removed when the run
/// ends. The CLI finds no login in it - only the credential in its environment - so an issued run
/// never reads the shared agent home's sign-in files, and anything it writes for itself goes with it.
///
/// <para>
/// MADE AS THE AGENT, as the member's TMPDIR is (<see cref="MemberTemp"/>): through the same
/// <see cref="AgentLaunchUser.Prefix"/> every agent child gets when the Host switches users, so the
/// agent owns it; by the Host otherwise, whose user is then the agent's. The one file the launch
/// puts in it - grok's <c>harness</c> MCP entry, which grok reads only from its home - is written by
/// the Host to its own launch directory and COPIED in as the agent.
/// </para>
///
/// <para>
/// REMOVED THROUGH <see cref="FolderRemoval"/>, because the agent writes inside it: the Host's pass,
/// then the agent's, links removed and never followed. A home a crash left behind is removed before
/// the member's next issued run makes its own.
/// </para>
/// </summary>
public static class RunHome
{
    /// <summary>The start of every run home's name in its parent.</summary>
    public const string Prefix = "home-";

    /// <summary>Where a run home's CLIs keep their caches: beside the homes, kept across runs.
    /// Copilot unpacks about 186 MB into a fresh HOME's cache otherwise, on every run.</summary>
    public const string CacheFolder = ".cache";

    /// <summary>The grok config file inside a home, as grok looks for it.</summary>
    public static string GrokConfigIn(string home) => Path.Combine(home, ".grok", "config.toml");

    /// <summary>
    /// A new home in <paramref name="parent"/>, with <paramref name="grokConfig"/> (a file the Host
    /// wrote and shared with the agent's group) copied to <c>.grok/config.toml</c> when given.
    /// When <paramref name="memberFolder"/> - the parent is one member's TMPDIR - homes an earlier
    /// run left there are removed first and the member's <see cref="CacheFolder"/> is made beside
    /// it; a parent several launches share (the launch check's) is never swept. Null when it could
    /// not be made.
    /// </summary>
    public static async Task<string?> CreateAsync(
        string parent, AgentLaunchUser? runAs, string? grokConfig, CancellationToken ct, bool memberFolder = true)
    {
        if (memberFolder) await SweepAsync(parent, runAs, ct);

        var home = Path.Combine(parent, Prefix + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..12]);

        if (runAs is { Switches: true } agent)
        {
            if (SystemCommand.Find("mkdir") is not { } mkdir || SystemCommand.Find("cp") is not { } cp) return null;
            if (!await RunAsync([.. agent.Prefix, mkdir, "-m", "700", "--", home], ct)) return null;
            if (memberFolder && !await RunAsync([.. agent.Prefix, mkdir, "-p", "-m", "700", "--", Path.Combine(parent, CacheFolder)], ct)) return null;

            if (grokConfig is not null)
            {
                var grok = Path.GetDirectoryName(GrokConfigIn(home))!;
                if (!await RunAsync([.. agent.Prefix, mkdir, "-m", "700", "--", grok], ct)) return null;
                if (!await RunAsync([.. agent.Prefix, cp, "--", grokConfig, GrokConfigIn(home)], ct)) return null;
            }

            return home;
        }

        try
        {
            const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(home);
                if (memberFolder) Directory.CreateDirectory(Path.Combine(parent, CacheFolder));
            }
            else
            {
                Directory.CreateDirectory(home, OwnerOnly);
                if (memberFolder) Directory.CreateDirectory(Path.Combine(parent, CacheFolder), OwnerOnly);
            }

            if (grokConfig is not null)
            {
                var grok = Path.GetDirectoryName(GrokConfigIn(home))!;
                if (OperatingSystem.IsWindows()) Directory.CreateDirectory(grok);
                else Directory.CreateDirectory(grok, OwnerOnly);
                File.Copy(grokConfig, GrokConfigIn(home));
            }

            return home;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Removes <paramref name="home"/> and everything the run wrote in it. Never throws.</summary>
    public static async Task RemoveAsync(string home, AgentLaunchUser? runAs)
    {
        try
        {
            await new FolderRemoval(runAs).RemoveInsideAsync(Path.GetDirectoryName(home)!, home, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The next issued run of this member sweeps it.
        }
    }

    /// <summary>Removes every run home in <paramref name="parent"/>: a member runs one run at a
    /// time, so any home there is one a stopped Host left.</summary>
    private static async Task SweepAsync(string parent, AgentLaunchUser? runAs, CancellationToken ct)
    {
        IEnumerable<string> stale;
        try
        {
            stale = Directory.EnumerateDirectories(parent, Prefix + "*").ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var home in stale)
        {
            ct.ThrowIfCancellationRequested();
            await RemoveAsync(home, runAs);
        }
    }

    private static async Task<bool> RunAsync(IReadOnlyList<string> command, CancellationToken ct)
    {
        var start = new ProcessStartInfo(command[0])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in command.Skip(1)) start.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(start);
            if (process is null) return false;

            var output = process.StandardOutput.ReadToEndAsync(ct);
            var error = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            await Task.WhenAll(output, error);
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
