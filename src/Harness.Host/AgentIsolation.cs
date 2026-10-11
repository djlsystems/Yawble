using System.ComponentModel;

namespace Harness.Host;

/// <summary>
/// How a headless preset keeps a member to the tools the platform gives it: the `harness` MCP
/// server and the CLI's own local tools, and nothing the shared agent home or the signed-in account
/// brings along.
///
/// THE HOME IS SHARED AND IS NEVER EDITED. The Concierge is the person's own session and keeps
/// every connector, home MCP server, plugin and skill they set up; a member reaches none of them.
/// Everything here is a LAUNCH decision - arguments, environment - and nothing in it removes or
/// rewrites a file under the agent home.
///
/// WHAT EACH ENTRY SAYS WAS MEASURED against the CLI installed in the image, with a real launch,
/// never read from documentation; the seed records how, next to each preset. A switch that did not
/// hold under measurement is not listed as if it did: it goes in <see cref="Gaps"/>.
/// </summary>
public sealed record AgentIsolation(
    [property: Description(
        "Arguments that switch off what the CLI would otherwise load from the account and the shared "
        + "home. They go where the launch's arguments hold the `{isolation}` token, or first when "
        + "there is none.")]
    IReadOnlyList<string> Arguments,
    [property: Description(
        "Variables set for a member's launch, AFTER every other source - the preset's `env` and the "
        + "team's env - so neither can switch isolation back on. A key beginning HARNESS_ is ignored.")]
    IReadOnlyDictionary<string, string>? Env = null,
    [property: Description(
        "The CLI's own local tools a member may be offered, by the name its transcript gives them "
        + "(`Bash`, `exec_command`, `run_terminal_command`, `bash`). Anything else, outside the "
        + "platform's `harness` server and `allowedServers`, is a foreign tool.")]
    IReadOnlyList<string>? AllowedTools = null,
    [property: Description(
        "MCP servers beyond `harness` a member may be offered, by the name the CLI gives them. Empty "
        + "for every built-in preset. A server driven by a platform token (Copilot's "
        + "`github-mcp-server` and GH_TOKEN) is allowed only by naming it here.")]
    IReadOnlyList<string>? AllowedServers = null,
    [property: Description(
        "What the CLI still loads from the shared home that no launch switch turns off, measured "
        + "and written down so a person reads it rather than assumes it. Each is a sentence.")]
    IReadOnlyList<string>? Gaps = null)
{
    /// <summary>The launch-argument element the isolation arguments replace.</summary>
    public const string Token = "{isolation}";

    /// <summary>The platform's own MCP server, allowed for every preset and never declared.</summary>
    public const string PlatformServer = "harness";
}

/// <summary>What the per-run check and the pre-flight report hold a preset to.</summary>
public enum IsolationState
{
    /// <summary>A headless preset with a declaration: harness, its allowed servers and tools.</summary>
    Isolated,

    /// <summary>A headless language-model preset with no declaration. Shown as not verified, and
    /// still checked: any server other than harness is foreign.</summary>
    NotVerified,

    /// <summary>An interactive preset: the Concierge, the person's own session, which keeps every
    /// tool. Listed as information, never flagged.</summary>
    Concierge,

    /// <summary>A preset that launches no language model (a program such as `cat`). Nothing to check.</summary>
    NotAModel,
}

/// <summary>
/// The tools one preset may be offered: THE ANSWER THE PER-RUN FOREIGN-TOOL CHECK AND THE PRE-FLIGHT
/// REPORT READ, from <see cref="AgentCatalog.Allowance"/>. Never a second store: it is
/// derived from the preset each time it is asked.
/// </summary>
public sealed record ToolAllowance(
    string Preset,
    IsolationState State,
    IReadOnlyList<string> AllowedServers,
    IReadOnlyList<string>? AllowedTools,
    IReadOnlyList<string> Gaps)
{
    /// <summary>Whether a run of this preset is checked for foreign tools at all. The Concierge and
    /// a program are not.</summary>
    public bool Checked => State is IsolationState.Isolated or IsolationState.NotVerified;

    /// <summary>
    /// Whether a tool the agent was offered is allowed. <paramref name="server"/> is the MCP server
    /// it came from, or null for one of the CLI's own tools.
    ///
    /// A NOT VERIFIED preset declares no local tools, so its local tools are not judged (true) and
    /// its servers are: harness only. The report says "not verified" beside it, which is what keeps
    /// that true from reading as clean.
    /// </summary>
    public bool Allows(string? server, string tool)
    {
        if (!Checked) return true;

        if (server is not null)
            return AllowedServers.Contains(server, StringComparer.OrdinalIgnoreCase);

        return AllowedTools is null || AllowedTools.Contains(tool, StringComparer.Ordinal);
    }
}

public static class AgentIsolationPolicy
{
    /// <summary>The allowance for <paramref name="definition"/>; null for a preset this tenant does not have.</summary>
    public static ToolAllowance? For(AgentDefinition? definition)
    {
        if (definition is null) return null;

        var state = definition.Mode == AgentMode.Interactive ? IsolationState.Concierge
            : definition.Launch is { LanguageModel: false } ? IsolationState.NotAModel
            : definition.Isolation is null ? IsolationState.NotVerified
            : IsolationState.Isolated;

        IReadOnlyList<string> servers =
            [AgentIsolation.PlatformServer, .. definition.Isolation?.AllowedServers ?? []];

        return new ToolAllowance(
            definition.Name,
            state,
            servers,
            state == IsolationState.Isolated ? definition.Isolation!.AllowedTools ?? [] : null,
            definition.Isolation?.Gaps ?? []);
    }

    /// <summary>
    /// A member's command with its preset's isolation folded in: the arguments at the
    /// <see cref="AgentIsolation.Token"/> (or first), the environment carried for the runner to set
    /// last. A token with no declaration behind it is dropped rather than handed to the CLI as a
    /// literal `{isolation}` argument.
    /// </summary>
    public static AgentCommand Apply(AgentCommand command, AgentIsolation? isolation)
    {
        IReadOnlyList<string> inserted = isolation?.Arguments ?? [];
        var arguments = new List<string>(command.Arguments.Count + inserted.Count);
        var placed = false;

        foreach (var argument in command.Arguments)
        {
            if (argument == AgentIsolation.Token)
            {
                if (!placed) arguments.AddRange(inserted);
                placed = true;
                continue;
            }

            arguments.Add(argument);
        }

        if (!placed) arguments.InsertRange(0, inserted);

        var environment = isolation?.Env?
            .Where(e => !e.Key.StartsWith("HARNESS_", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);

        return command with
        {
            Arguments = arguments,
            IsolationEnvironment = environment is { Count: > 0 } ? environment : null,
        };
    }
}
