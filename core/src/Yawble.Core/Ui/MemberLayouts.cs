using Yawble.Core.Bus;
using Yawble.Core.Policy;
using PolicyMember = Yawble.Core.Policy.MemberRef;

namespace Yawble.Core.Ui;

/// <summary>
/// Builds a member's <see cref="RunLayout"/> on this host for the policy screens: the effective policy, the
/// loosen preview and the sandbox dry run of every save. The app data root is the parent of the core's data folder
/// (&lt;userData&gt;/data), as RunLayout's folder plan says.
/// </summary>
public sealed class MemberLayouts(string dataDir, string installDir, string home, TargetOs? os = null, Agents.Profiles.IAgentResolver? agents = null)
{
    private readonly TargetOs _os = os ?? TargetOsExtensions.Host;

    public TargetOs Os => _os;

    public string AppDataRoot { get; } = Path.GetDirectoryName(Path.GetFullPath(dataDir).TrimEnd(Path.DirectorySeparatorChar))
        ?? Path.GetFullPath(dataDir);

    public RunLayout For(MemberRow member, IEnumerable<MemberRow> everyone) =>
        RunLayout.Standard(_os, home, AppDataRoot, installDir,
            new PolicyMember(member.Team, member.Name), CliOf(member.Cli), PolicyCli(CliOf(member.Cli).Id()),
            everyone.Select(m => new PolicyMember(m.Team, m.Name)));

    /// <summary>A representative member for saving the global policy (its dry run compiles this run).</summary>
    public RunLayout Sample(IEnumerable<MemberRow> everyone) =>
        RunLayout.Standard(_os, home, AppDataRoot, installDir,
            new PolicyMember("policy-sample", "member"), AgentCli.Claude, PolicyCli("claude"),
            everyone.Select(m => new PolicyMember(m.Team, m.Name)));

    /// <summary>The CLI behind a member's cli: a built-in's id or a custom agent's name (its base CLI).</summary>
    public AgentCli CliOf(string cli) => Agents.Profiles.AgentResolvers.Resolve(agents, cli).Cli;

    /// <summary>
    /// The policy screens check a policy against the app's own folders; the CLI's install grants are added (and
    /// checked) at each launch from the detected install, so here the CLI has none.
    /// </summary>
    private CliInstall PolicyCli(string cli) =>
        CliInstall.Missing(_os, new PolicyPaths(_os, home).Combine(new PolicyPaths(_os, home).Normalize(installDir) ?? installDir, "bin"), cli);
}

/// <summary>The effective-policy banner of a terminal window and an agent card.</summary>
public static class PolicyBanner
{
    public static string Title(MemberRow m, TargetOs os, EffectivePolicy p) =>
        $"{m.Team} / {m.Name} · sandbox · {Backend(os)} · {m.Cli} · net: {Net(p)}";

    public static IReadOnlyList<string> Details(MemberRow m, EffectivePolicy p)
    {
        var list = new List<string>
        {
            $"timeout {Duration(p.Compute.WallTimeoutSec)}",
            $"idle {Duration(p.Compute.IdleTimeoutSec)}",
            $"rw: {m.Name}/repo",
        };
        if (p.Readwrite.Count > 0) list.Add($"+rw: {string.Join(", ", p.Readwrite)}");
        if (p.Readonly.Count > 0) list.Add($"+ro: {string.Join(", ", p.Readonly)}");
        list.Add($"clipboard: {p.Clipboard}");
        return list;
    }

    public static string Backend(TargetOs os) => os switch
    {
        TargetOs.Windows => "ProcessContainer",
        TargetOs.MacOS => "seatbelt",
        _ => "bubblewrap",
    };

    public static string Net(EffectivePolicy p) => p.AllowHosts.Count switch
    {
        0 => "model + bus",
        <= 2 => "model + bus + " + string.Join(", ", p.AllowHosts),
        var n => $"model + bus + {n} hosts",
    };

    public static string Duration(int seconds)
    {
        if (seconds % 3600 == 0) return $"{seconds / 3600} h";
        if (seconds >= 3600) return $"{seconds / 3600} h {seconds % 3600 / 60} min";
        if (seconds % 60 == 0) return $"{seconds / 60} min";
        return $"{seconds} s";
    }
}
