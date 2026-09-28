using System.Globalization;
using System.Runtime.InteropServices;

namespace Harness.Host;

/// <summary>
/// WHO AN AGENT CHILD RUNS AS. Decided once, when the Host starts, and then fixed.
///
/// <para>
/// In the image the Host runs as <c>harness</c> and every agent child (headless members, the
/// Concierge PTY and the auth probe) runs as <c>agent</c>. Without the split, an agent could
/// signal the Host (a <c>pkill -f Harness.Host.dll</c> from a test would stop the live platform)
/// and read its database and keys.
/// </para>
///
/// <para>
/// THE SWITCH IS <c>setpriv</c>, prefixed to the child's command line. It works because the
/// entrypoint hands the Host exactly two capabilities, CAP_SETUID and CAP_SETGID, as ambient
/// capabilities. The prefix clears the inheritable and ambient sets, so the agent keeps no
/// capability at all. The Host can switch only when ALL of these hold:
/// the target user exists, it is not the user the Host already runs as, the Host holds both
/// capabilities, <c>setpriv</c> is in a root-owned system directory (never from PATH, whose first
/// folders the agent owns - see <see cref="SystemCommand"/>), and the Host can hand a file to the target's group
/// (that last one is how the child reads the MCP config the Host writes for it). Otherwise it
/// launches as its own user. That is the case in development, in the test
/// suite, and in an image that has no <c>agent</c> user. <see cref="Reason"/> says which case
/// applies, and the Host logs it at start.
/// </para>
/// </summary>
public sealed record AgentLaunchUser(
    bool Switches, string Name, int Uid, int Gid, string Reason, string SetprivPath = "/usr/bin/setpriv",
    bool ClearsCapabilities = false, bool AgentUserUnreachable = false)
{
    /// <summary>The user children run as when nothing is configured.</summary>
    public const string DefaultName = "agent";

    /// <summary>Launch as the Host's own user: no prefix and no sharing.</summary>
    public static AgentLaunchUser Same(string name, string reason) => new(false, name, -1, -1, reason);

    /// <summary>
    /// What goes in front of the child's own command: <c>setpriv</c> and its arguments when this
    /// switches, nothing when it does not. <c>--init-groups</c> gives the child the target's
    /// supplementary groups and not the Host's. The inheritable and ambient sets are cleared so
    /// the child ends with no capability.
    ///
    /// <para>
    /// WHEN IT DOES NOT SWITCH BUT THE HOST HOLDS AMBIENT OR INHERITABLE CAPABILITIES
    /// (<see cref="ClearsCapabilities"/>), the prefix is <c>setpriv</c> with only the two clearing
    /// flags, so no child inherits them. The Host cannot drop its own ambient set instead:
    /// capabilities are per thread, and .NET starts a process from whichever thread asks.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Prefix => Switches
        ?
        [
            SetprivPath,
            "--reuid=" + Uid.ToString(CultureInfo.InvariantCulture),
            "--regid=" + Gid.ToString(CultureInfo.InvariantCulture),
            "--init-groups",
            "--inh-caps=-all",
            "--ambient-caps=-all",
            "--",
        ]
        : ClearsCapabilities
            ? [SetprivPath, "--inh-caps=-all", "--ambient-caps=-all", "--"]
            : [];

    /// <summary>
    /// What goes in front of a command the Host runs AS ITSELF on its own files (a local
    /// repository's git): no switch, and every inheritable and ambient capability cleared whenever
    /// the Host holds any - always when it switches, since switching needs them.
    /// </summary>
    public IReadOnlyList<string> HostPrefix => Switches || ClearsCapabilities
        ? [SetprivPath, "--inh-caps=-all", "--ambient-caps=-all", "--"]
        : [];

    /// <summary>
    /// FAIL CLOSED: the agent user exists and is somebody else, but this Host cannot
    /// start a child as it (no capabilities, no setpriv, or no share with its group). Launching as
    /// the Host instead would run agent-installed CLIs, and git with agent-written config, as the
    /// user that owns the database and the keys. So every caller of <see cref="Prefix"/> refuses
    /// when this is true. It is false in development and the tests (no <c>agent</c> user, or the
    /// Host already is it).
    /// </summary>
    public bool Refuses => !Switches && AgentUserUnreachable;

    /// <summary>
    /// The error a refusing caller reports, naming <paramref name="what"/> and <see cref="Reason"/>.
    /// </summary>
    public string Refusal(string what) =>
        $"{what} was not started: a user '{Name}' exists on this machine, but the Host cannot start a child as it "
        + $"({Reason}). Running it as the Host instead would give agent-written code the Host's database and keys. "
        + "Fix the image or its runtime flags (the Host log's \"Agent launch\" line says the same), then restart the Host.";

    /// <summary>The Host log's start line, and what <c>--doctor</c> reports as the mode.</summary>
    public string Mode => Switches ? "separate user" : Refuses ? "REFUSED" : "same user as the Host";

    /// <summary><paramref name="command"/> with <see cref="Prefix"/> in front of it.</summary>
    public IReadOnlyList<string> Wrap(IReadOnlyList<string> command) => [.. Prefix, .. command];

    /// <summary>
    /// Lets the agent READ what the Host wrote at <paramref name="path"/>: the file, or the
    /// directory and everything in it, is given to the agent's group with group read (and
    /// traverse on directories). The owner stays the Host, and "other" gets nothing new. A
    /// per-launch MCP directory is created owner-only and stays closed to every other user.
    /// It is recursive so a file added to that directory later (a key file) is covered too.
    /// Does nothing when this does not switch. Never throws, because a launch must not fail
    /// here: an agent that cannot read its config reports that itself.
    /// </summary>
    public void Share(string path)
    {
        if (!Switches || OperatingSystem.IsWindows()) return;

        try
        {
            if (Directory.Exists(path))
            {
                ShareOne(path, UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
                foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
                {
                    ShareOne(entry, Directory.Exists(entry)
                        ? UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                        : UnixFileMode.GroupRead);
                }
            }
            else if (File.Exists(path))
            {
                ShareOne(path, UnixFileMode.GroupRead);
            }

            LetThrough(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Every directory between the temp root and <paramref name="path"/> lets the agent's group
    /// through: given to the group, traverse only (710). The Host runs with umask 0007, so a
    /// parent it made (/tmp/harness-mcp) is 770 and its own group's, and a per-launch directory
    /// the agent may read would otherwise be out of its reach. Traverse without read or write means a launch directory is reached only by
    /// its unguessable name: the agent cannot list the parent or touch another launch's
    /// directory. Only directories inside the temp root are touched, and /tmp itself never.
    /// </summary>
    private void LetThrough(string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        for (var dir = Path.GetDirectoryName(Path.GetFullPath(path));
             dir is not null && dir.Length > root.Length && dir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
             dir = Path.GetDirectoryName(dir))
        {
            _ = lchown(dir, -1, Gid);
            var mode = File.GetUnixFileMode(dir) & ~(UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
            File.SetUnixFileMode(dir, mode | UnixFileMode.GroupExecute);
        }
    }

    private void ShareOne(string path, UnixFileMode add)
    {
        _ = lchown(path, -1, Gid);
        File.SetUnixFileMode(path, File.GetUnixFileMode(path) | add);
    }

    /// <summary>
    /// The decision, with every fact about the machine passed in. <see cref="Resolve"/> gathers
    /// those facts; this is where the rule lives, so a test can walk each branch.
    /// </summary>
    public static AgentLaunchUser Decide(
        string name, (int Uid, int Gid)? target, int hostUid, ulong effectiveCapabilities,
        string? setpriv, Func<int, bool> canShareWithGroup)
    {
        if (target is not { } user)
        {
            return Same(name, $"there is no user '{name}' on this machine");
        }

        if (user.Uid == hostUid)
        {
            return Same(name, $"the Host already runs as '{name}' (uid {hostUid})");
        }

        // From here on the agent user exists and is somebody else, so falling back to the Host's
        // own user runs agent-installed programs as the Host. Recorded, so a caller that can
        // decline to (the auth probe) does.
        const ulong setgid = 1UL << 6, setuid = 1UL << 7;
        if ((effectiveCapabilities & (setuid | setgid)) != (setuid | setgid))
        {
            return Same(name,
                $"the Host (uid {hostUid}) lacks CAP_SETUID and CAP_SETGID, so it cannot start a child as '{name}'")
                with { AgentUserUnreachable = true };
        }

        if (setpriv is null)
        {
            return Same(name, $"setpriv is not in a root-owned system directory ({string.Join(", ", SystemCommand.Directories)})")
                with { AgentUserUnreachable = true };
        }

        if (!canShareWithGroup(user.Gid))
        {
            return Same(name,
                $"the Host (uid {hostUid}) cannot give a file to group {user.Gid}, so a child running as '{name}' could not read its MCP config; the Host user must be a member of that group")
                with { AgentUserUnreachable = true };
        }

        return new AgentLaunchUser(true, name, user.Uid, user.Gid,
            $"agent children run as '{name}' (uid {user.Uid}, gid {user.Gid}); the Host runs as uid {hostUid}",
            setpriv);
    }

    /// <summary>
    /// Reads this machine: <paramref name="passwd"/> for the target, the Host's effective uid, its
    /// effective capabilities from <c>/proc/self/status</c>, <c>setpriv</c> in a system directory,
    /// and whether a file can be given to the target's group (measured by doing it to a scratch
    /// directory). When it does not switch and the Host holds ambient or inheritable capabilities,
    /// <see cref="ClearsCapabilities"/> is set so children still start with none.
    /// </summary>
    public static AgentLaunchUser Resolve(string? name = null, string passwd = "/etc/passwd")
    {
        name = string.IsNullOrWhiteSpace(name) ? DefaultName : name.Trim();

        if (!OperatingSystem.IsLinux())
        {
            return Same(name, "the Host is not running on Linux");
        }

        var setpriv = SystemCommand.Find("setpriv");
        var decided = Decide(
            name,
            LookUp(name, passwd),
            (int)geteuid(),
            Capabilities("CapEff:"),
            setpriv,
            CanShareWithGroup);

        return !decided.Switches && setpriv is not null && (Capabilities("CapAmb:") | Capabilities("CapInh:")) != 0
            ? decided with { ClearsCapabilities = true, SetprivPath = setpriv }
            : decided;
    }

    /// <summary>The uid and gid of <paramref name="name"/> in a passwd-format file, or null.</summary>
    public static (int Uid, int Gid)? LookUp(string name, string passwd)
    {
        if (!File.Exists(passwd)) return null;

        foreach (var line in File.ReadLines(passwd))
        {
            var fields = line.Split(':');
            if (fields.Length >= 4
                && fields[0] == name
                && int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var uid)
                && int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var gid))
            {
                return (uid, gid);
            }
        }

        return null;
    }

    /// <summary>One capability set of this process from <c>/proc/self/status</c>, e.g. <c>CapEff:</c>.</summary>
    private static ulong Capabilities(string set)
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                if (line.StartsWith(set, StringComparison.Ordinal)
                    && ulong.TryParse(line[set.Length..].Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var caps))
                {
                    return caps;
                }
            }
        }
        catch (IOException)
        {
        }

        return 0;
    }

    private static bool CanShareWithGroup(int gid)
    {
        string? scratch = null;
        try
        {
            scratch = Directory.CreateTempSubdirectory("harness-share-probe-").FullName;
            return lchown(scratch, -1, gid) == 0;
        }
        catch (IOException)
        {
            return false;
        }
        finally
        {
            if (scratch is not null) try { Directory.Delete(scratch); } catch (IOException) { }
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();

    [DllImport("libc", SetLastError = true)]
    private static extern int lchown(string path, int owner, int group);
}
