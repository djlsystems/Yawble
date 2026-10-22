using System.Text;

namespace Harness.Host;

/// <summary>What <see cref="PreviewEnvFiles.Write"/> wrote: where, and the names in it. Never a value.</summary>
public sealed record PreviewEnvFile(string Path, IReadOnlyList<string> Variables);

/// <summary>
/// A TEAM'S ENVIRONMENT, FOR AN APP THE PERSON ASKED TO TRY, as a file in the Concierge's own folder
/// that its shell loads to start the app. A team's env holds secrets - a connection string with a
/// password - so the values go to a file and never through the terminal: the answer names the path
/// and the variable names only, and nothing is logged or stored.
///
/// <list type="number">
/// <item><b>Owner read-write only (0600)</b>, written through the handle whatever the umask. Where
/// the agent runs as another user it is given to the agent's group with group read
/// (<see cref="AgentLaunchUser.Share(Microsoft.Win32.SafeHandles.SafeFileHandle)"/>), as every other
/// file the Host writes for an agent, and "other" gets nothing.</item>
/// <item><b>Never written through a link.</b> The folder is the agent's to write in, so whatever is
/// at the name is removed first (a link itself, never its target) and the file is created new.</item>
/// <item><b>Shell syntax</b>: one <c>export NAME='value'</c> line per variable, a single quote in a
/// value written <c>'\''</c>, so <c>. &lt;path&gt;</c> sets exactly the team's values.</item>
/// <item><b>It does not outlive the session</b>: removed when the Concierge session ends, however it
/// ends, and every one left by a Host that stopped is removed at start.</item>
/// </list>
/// </summary>
public sealed class PreviewEnvFiles(TeamPaths paths, AgentLaunchUser runAs)
{
    public const string Prefix = "preview-env-";
    public const string Suffix = ".env";

    /// <summary>The refusal to anyone but the person's own Concierge.</summary>
    public const string Refusal =
        "Only the person's Concierge writes a team's environment for a preview; a Manager, a member and any other key are refused.";

    /// <summary>Owner read-write; nobody else.</summary>
    public const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>The file's name for <paramref name="team"/>: its id, with anything but a letter, a digit,
    /// <c>-</c> or <c>_</c> made <c>-</c>.</summary>
    public static string NameFor(string team) =>
        Prefix + new string([.. team.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-')]) + Suffix;

    /// <summary>The file's text: one <c>export</c> line per variable, in name order.</summary>
    public static string Render(IReadOnlyDictionary<string, string> env)
    {
        var text = new StringBuilder();
        foreach (var (name, value) in env.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            text.Append("export ").Append(name).Append("='").Append(value.Replace("'", "'\\''", StringComparison.Ordinal)).Append("'\n");
        }

        return text.ToString();
    }

    /// <summary>
    /// Writes <paramref name="env"/> for <paramref name="team"/> into <paramref name="userId"/>'s
    /// Concierge folder, replacing the one before. Null when that person has no Concierge folder yet.
    /// </summary>
    public PreviewEnvFile? Write(string userId, string team, IReadOnlyDictionary<string, string> env)
    {
        if (ConciergeWorkspaces.TryExisting(paths, userId) is not { } workspace) return null;

        var path = Path.Combine(workspace, NameFor(team));

        // A LINK IS REMOVED, NEVER FOLLOWED: File.Delete takes the link itself. Create-new then fails on
        // anything put back in between, rather than writing through it.
        File.Delete(path);

        var options = new FileStreamOptions { Mode = System.IO.FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = FileMode;

        using (var stream = new FileStream(path, options))
        {
            // The create mode is masked by the umask; the file's mode is this one whatever it is.
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(stream.SafeFileHandle, FileMode);

            var bytes = Encoding.UTF8.GetBytes(Render(env));
            stream.Write(bytes);
            runAs.Share(stream.SafeFileHandle);
        }

        return new PreviewEnvFile(path, [.. env.Keys.Order(StringComparer.Ordinal)]);
    }

    /// <summary>Removes every preview env file in <paramref name="userId"/>'s Concierge folder. Never
    /// throws: a session must end whatever is left.</summary>
    public void RemoveFor(string userId)
    {
        if (ConciergeWorkspaces.TryExisting(paths, userId) is { } workspace) RemoveIn(workspace);
    }

    /// <summary>Removes every preview env file in every Concierge folder: at start, before any
    /// Concierge does, so a Host stopped rather than its sessions ended leaves none.</summary>
    public void RemoveAll()
    {
        var root = paths.ConciergeWorkspacesRoot;
        if (!Directory.Exists(root)) return;

        foreach (var workspace in Quietly(() => Directory.EnumerateDirectories(root)))
        {
            if (TeamPaths.ConciergeOwnerOf(workspace) is not null) RemoveIn(workspace);
        }
    }

    /// <summary>
    /// <paramref name="revoke"/>, with this person's preview env files removed first. What
    /// <see cref="ConciergeSessionStore"/> is given, so every way a session ends removes them. The
    /// revoke runs whatever the removal did.
    /// </summary>
    public ConciergeRevoke RemovingOnEnd(ConciergeRevoke revoke) => async (key, ct) =>
    {
        try
        {
            RemoveFor(key.User);
        }
        finally
        {
            await revoke(key, ct);
        }
    };

    private static void RemoveIn(string workspace)
    {
        foreach (var file in Quietly(() => Directory.EnumerateFiles(workspace, Prefix + "*" + Suffix)))
        {
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static List<string> Quietly(Func<IEnumerable<string>> list)
    {
        try
        {
            return [.. list()];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
