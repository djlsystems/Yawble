using Harness.Contracts;
using Harness.Pty;

namespace Harness.Host;

/// <summary>
/// A PERSON'S TERMINAL, MADE ON THE WORKER THAT RUNS IT. Control resolves everything about a
/// Concierge (<see cref="TerminalLaunch"/>); what is written to this machine's temp folder and how
/// the CLI is started as the agent is the worker's: the MCP config, the system-prompt file, the
/// sharing of both with the agent's group, and <c>setpriv</c> in front of the arguments.
/// </summary>
public static class ConciergeTerminal
{
    /// <summary>
    /// The spec <paramref name="launch"/> is spawned with on this worker, at <paramref name="cols"/> by
    /// <paramref name="rows"/>, and the MCP config written for it (null when there is no platform to
    /// call or no key), which is removed when the terminal ends. Refuses, before anything is written,
    /// when agents must run as another user and this worker cannot switch to it.
    /// </summary>
    public static (PtySpec Spec, McpLaunchConfig? Mcp) Materialize(
        TerminalLaunch launch, AgentLaunchUser? runAs, int cols = PtySpecDefaults.Cols, int rows = PtySpecDefaults.Rows)
    {
        // FAIL CLOSED, before a file is written: see AgentLaunchUser.Refuses.
        if (runAs is { Refuses: true })
        {
            throw new InvalidOperationException(runAs.Refusal("The Concierge"));
        }

        var environment = new Dictionary<string, string>(launch.Environment, StringComparer.Ordinal);

        var mcp = launch.Mcp is { } target
            ? McpLaunchConfig.TryWrite(target.BaseUrl, environment.GetValueOrDefault("HARNESS_KEY"), target.Owner)
            : null;
        if (mcp is not null)
        {
            environment["HARNESS_MCP_CONFIG"] = mcp.JsonPath;
            environment["HARNESS_MCP_CONFIG_TOML"] = mcp.TomlPath;
        }

        List<string> argv = [.. launch.Argv];

        if (mcp is not null)
        {
            argv = argv.Select(argument => McpLaunchConfig.Apply(argument, mcp)).ToList();
        }

        List<string>? tempFiles = null;

        if (launch.SystemPromptArguments is { Count: > 0 } systemArguments)
        {
            var promptFile = Path.Combine(Path.GetTempPath(), $"os-concierge-{Guid.NewGuid():N}.system.txt");

            File.WriteAllText(promptFile, launch.SystemPrompt ?? string.Empty);
            tempFiles = [promptFile];

            // Substituted inside an already-tokenized element, never by re-splitting a joined command
            // line: a quote or a space inside the prompt path must not become an argument boundary.
            argv.AddRange(systemArguments.Select(
                // Both spellings, for the reason ProcessAgentRunner states: `{systemFile}` is a
                // permanent alias that existing catalogs carry, and an unsubstituted one is handed to
                // the CLI as a literal path rather than refused.
                a => a
                    .Replace("{systemPromptFile}", promptFile, StringComparison.Ordinal)
                    .Replace("{systemFile}", promptFile, StringComparison.Ordinal)));
        }

        // The Concierge runs as `agent` too, when the worker can switch: its MCP directory and prompt
        // file are handed to that user's group, and setpriv goes in front of the argv.
        if (runAs is not null)
        {
            if (mcp is not null) runAs.Share(Path.GetDirectoryName(mcp.JsonPath)!);
            foreach (var file in tempFiles ?? []) runAs.Share(file);
            argv = [.. runAs.Wrap(argv)];
        }

        return (
            new PtySpec(
                launch.Argv.Count > 0 ? launch.Argv[0] : string.Empty,
                launch.StartingFolder,
                cols,
                rows,
                Env: environment,
                Argv: argv,
                TempFiles: tempFiles,
                ClearEnvironment: launch.ClearEnvironment),
            mcp);
    }
}
