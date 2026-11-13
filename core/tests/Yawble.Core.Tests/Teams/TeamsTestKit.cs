using System.Diagnostics;
using Yawble.Core.Bus;
using Yawble.Core.Teams;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>Git as an AGENT runs it inside its sandbox: cwd = its clone, HOME = its workspace, nothing else inherited.</summary>
internal static class AgentGit
{
    public static GitResult Run(MemberCheckout checkout, params string[] args) => Run(checkout.ClonePath, checkout.Workspace, args);

    public static GitResult Run(string cwd, string home, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        psi.Environment.Clear();
        psi.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH");
        psi.Environment["HOME"] = home;
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (OperatingSystem.IsWindows())
        {
            // As the compiler gives a Windows sandbox (SYSTEMROOT, WINDIR): Git for Windows' own runtime needs them.
            foreach (var name in new[] { "SystemRoot", "windir" })
                if (Environment.GetEnvironmentVariable(name) is { } value) psi.Environment[name] = value;
        }
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        // Compared as LF text on every OS (Git for Windows could print CRLF through a wrapper).
        return new GitResult(p.ExitCode, stdout.ReplaceLineEndings("\n").TrimEnd(), stderr.ReplaceLineEndings("\n").TrimEnd());
    }

    public static string Ok(MemberCheckout checkout, params string[] args)
    {
        var r = Run(checkout, args);
        Assert.True(r.ExitCode == 0, $"git {string.Join(' ', args)}: {r.Stderr}");
        return r.Stdout;
    }

    /// <summary>Host git with extra environment (a temporary GIT_INDEX_FILE, say), for building test branches.</summary>
    public static string HostWithEnv(string cwd, IReadOnlyDictionary<string, string> env, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        psi.Environment.Clear();
        psi.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH");
        psi.Environment["HOME"] = Path.GetTempPath();
        if (OperatingSystem.IsWindows())
            foreach (var name in new[] { "SystemRoot", "windir" })
                if (Environment.GetEnvironmentVariable(name) is { } value) psi.Environment[name] = value;
        foreach (var (k, v) in env) psi.Environment[k] = v;
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)}: {stderr}");
        return stdout.ReplaceLineEndings("\n").TrimEnd();
    }

    /// <summary>The host's view, for assertions: plain git with no config from anywhere.</summary>
    public static string Host(string cwd, params string[] args)
    {
        var r = Run(cwd, Path.GetTempPath(), ["-c", "protocol.allow=never", "-c", "protocol.file.allow=always", .. args]);
        Assert.True(r.ExitCode == 0, $"git {string.Join(' ', args)}: {r.Stderr}");
        return r.Stdout;
    }
}

/// <summary>A temp app data root with its database, for TeamRepos tests.</summary>
internal sealed class TeamsFixture : IDisposable
{
    public Bus.TempDb Db { get; } = new();
    public string AppRoot { get; }
    public TeamRepos Repos { get; }
    public List<string> Log { get; } = [];

    public TeamsFixture()
    {
        AppRoot = Path.Combine(Db.Dir, "app");
        Directory.CreateDirectory(AppRoot);
        // The person's own git config never applies to a test's temp repositories (no hooks, no credential helper).
        var emptyGlobal = Path.Combine(Db.Dir, "empty.gitconfig");
        File.WriteAllText(emptyGlobal, "");
        Repos = new TeamRepos(AppRoot, Db.ConnectionString, log: line => { lock (Log) Log.Add(line); })
        {
            Pusher = new PersonGitPush(globalConfig: emptyGlobal),
        };
    }

    public (MemberRef Manager, MemberRef Ada, MemberRef Ben) Team(string team = "Alpha")
    {
        using var store = Db.OpenStore();
        var id = store.CreateTeam(team);
        MemberRef Add(string name, MemberRole role, string cli)
        {
            // As BusEngine.AddMember does: the member's instruction type, the Manager's watches, a cursor.
            var m = store.AddMember(id, name, role, cli);
            store.Subscribe(m.Qualified, MessageTypes.Instruction(m));
            if (role == MemberRole.Manager) foreach (var type in MessageTypes.ManagerWatches) store.Subscribe(m.Qualified, type);
            store.InitCursor(m.Qualified);
            return m;
        }
        return (Add("Manager", MemberRole.Manager, "claude"), Add("Ada", MemberRole.Member, "claude"), Add("Ben", MemberRole.Member, "grok"));
    }

    public void Dispose() => Db.Dispose();
}
