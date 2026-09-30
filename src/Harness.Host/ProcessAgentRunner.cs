using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Harness.Contracts;
using Harness.Pty;

namespace Harness.Host;

/// <summary>
/// Runs an agent as a child process, prompt on stdin, output captured.
///
/// The agent is told nothing about Harness: it receives a prompt and a directory and produces
/// output. Everything platform-aware lives in the container, which is the line that keeps a worker
/// agent from needing to know any of this exists.
/// </summary>
/// <remarks>
/// <paramref name="diagnostics"/> is OPTIONAL and null means a runner that records nothing, which
/// is the shape every other seam in this codebase uses for an artifact store - see
/// `MemberRuntime`'s transcript and ledger. It is optional so the specs that construct this class
/// by hand do not each have to supply one, and because a diagnostics store is an observer: a runner
/// without one must go on running agents exactly as it did.
///
/// EVERY WRITE THROUGH IT IS FIRE-AND-AWAIT ON A PATH THAT IS ALREADY FAILING, and the store's own
/// never-throws guarantee is what makes that safe here - this class must not gain a way to fail
/// while describing a failure.
/// </remarks>
public sealed partial class ProcessAgentRunner(
    AgentCatalog catalog,
    RunHeartbeat heartbeat,
    ILogger<ProcessAgentRunner>? log = null,
    IDiagnosticsLog? diagnostics = null,
    AgentLaunchUser? runAs = null,
    LiveRuns? live = null,
    IMemberReports? reports = null,
    LaunchLookup? lookup = null) : IAgentRunner
{
    /// <summary>How long a launch looks for a program missing from PATH; the Host's is ~30s.</summary>
    private readonly LaunchLookup _lookup = lookup ?? LaunchLookup.Default;

    private const string ClaudeJson = "claude-json";
    private const string GrokJson = "grok-json";
    private const string CopilotUsageFile = "copilot-usage-file";
    private const string CodexTotal = "codex-total";
    private const string AntigravityJson = "antigravity-json";

    /// <summary>
    /// The launch error for a program that never appeared: what was looked for, for how long, and
    /// that re-sending tries again. It asks nobody to repair anything.
    /// </summary>
    public static string LaunchMissingText(string fileName, LaunchLookup lookup) =>
        $"`{fileName}` is not an executable file on PATH: it was not found when this run started"
        + (lookup.Window > TimeSpan.Zero ? $", nor in the {lookup.WindowText} after" : string.Empty)
        + ", so this member was not started. It may be being installed or updated; re-sending the "
        + "instruction will try again.";

    public async Task<AgentResult> RunAsync(AgentInvocation invocation, CancellationToken ct = default)
    {
        // FIRST OF THE FOUR REFUSALS, and the order is chosen: the other three are about what this
        // member is CONFIGURED with, and this one is about whether its files are there at all. A
        // team whose root is on an unplugged volume has no workspace, no transcripts and no shared
        // documents, so naming a catalog problem for it would send the reader to a screen with
        // nothing wrong on it.
        //
        // IT MUST REFUSE RATHER THAN RUN. `WorkingDirectory` below falls back to
        // `Environment.CurrentDirectory` when the workspace is missing - which is deliberate for an
        // ordinary member whose folder was removed underneath it, and catastrophic here: for anyone
        // running the Host from a clone, the Host's current directory is the SOURCE TREE, and an
        // unmarked member of an unreachable team would write its instructions file and its work
        // product into somebody's repository.
        //
        // The recovery is neither the catalog nor a settings screen - there is no setter for a
        // team's root - so the message names the folder and says a restart is what picks
        // it up, because restoration is what reads it.
        if (invocation.UnreachableRoot is { Length: > 0 } unreachable)
        {
            return new AgentResult(
                -1,
                string.Empty,
                $"This team's folder '{unreachable}' could not be reached when the Host started, so "
                + "this run did not begin - a member with no workspace would otherwise run in "
                + "whatever directory the Host itself was started from. Reconnect the drive or "
                + "share and restart the Host.");
        }

        // FAIL CLOSED. The agent user exists but this Host cannot switch to it, so the
        // only way to run this member would be as the Host itself - the user that owns the database
        // and the keys. Refused before anything is written or started.
        if (runAs is { Refuses: true })
        {
            return new AgentResult(-1, string.Empty, runAs.Refusal("This member"));
        }

        // Resolved from the CATALOG, by name, on every invocation - never from a per-container
        // binding captured when the member was created. The answer is derivable from
        // `invocation.Agent`, which is read off the container's own definition on every wake.
        //
        // A per-container map would be two stores of one fact, drifting apart in silence:
        // repointing a member would have to write both or produce a card that named one preset
        // while the process ran another; a deleted preset added back again would do nothing until
        // the Host restarted; and changing a live preset's MODE would wait for a restart too. The
        // write side is what keeps resolving here safe: `PUT /api/agents` already refuses to remove or re-mode a preset that a
        // member or a team's Concierge still references.
        if (catalog.For(invocation.Agent) is not { } command)
        {
            return new AgentResult(
                -1,
                string.Empty,

                // NO FAILURE TEXT IN THIS FILE NAMES ITS CONTAINER, and that is a rule rather than
                // a preference here. All three renderers supply the subject already, each with the
                // BARE name: MessageText.Of prefixes "{who} FAILED.", ContainerCard titles the
                // card, and StatusCommand.ActivityText shows one container's own tail. So the id
                // repeated a subject the reader had just been given - and repeated it in the
                // QUALIFIED `Team/Name` form, which is an IDENTITY and not a label. `{Id.Name}` is
                // the half-fix to refuse: it swaps the form and keeps the repetition.
                // FailureTextNamesNoIdTests pins every path below.
                //
                // The AGENT is named, and that is the distinction: it is what the reader has to go
                // and fix, and unlike the container it is not already on the card.
                //
                // BOTH recoveries work on the next wake, and neither needs a restart: nothing
                // caches the command per container.
                $"'{invocation.Agent}' is not an Agent this tenant has, so this run did not start. "
                + "Add it back in Agents, or point this member at a different Agent in its "
                + "settings. Either takes effect the next time it is woken.");
        }

        // AN EMPTY SYSTEM PROMPT IS REFUSED, not run. Every member is composed from its
        // role's built-in prompt, so this is not reachable from any setting - it guards a caller
        // that hands the runner an invocation built by hand. It is the single worst state this
        // design has: the agent starts, costs money, and answers with no idea what it is or who it
        // works for.
        if (string.IsNullOrWhiteSpace(invocation.SystemPrompt))
        {
            return new AgentResult(
                -1,
                string.Empty,
                "This member's system prompt is empty, so it was not run - an agent told nothing "
                + "costs money and answers noise.");
        }

        // RESOLVED HERE, so a miss is RECORDED and reported as a launch failure rather than
        // surfacing later as a spawn error naming a bare command. A miss is looked at again for
        // about 30 seconds first: the shared install is replaced in place while a CLI updates, and
        // a launch landing in that gap is not an agent fault. The only trace of a program that
        // reappears is the one progress line saying the run waited for it.
        var resolvedFileName = await ChildProcess.FindAsync(
            command.FileName,
            _lookup,
            reports is null
                ? null
                : () => reports.ProgressAsync(
                    invocation.Container,
                    $"`{command.FileName}` was not on PATH when this run started; looking again for up to "
                    + $"{_lookup.WindowText}, as it may be being installed or updated.",
                    CancellationToken.None),
            ct);

        if (resolvedFileName is null && ct.IsCancellationRequested)
        {
            return new AgentResult(
                -1,
                string.Empty,
                $"This run was stopped while it waited for `{command.FileName}` to appear on PATH, so it did not start.",
                FailureClass: FailureClasses.Interrupted);
        }

        if (resolvedFileName is null)
        {
            if (diagnostics is not null)
            {
                await diagnostics.WriteAsync(
                    DiagnosticSeverity.Warning,
                    DiagnosticKinds.ProcessExecutableNotFound,
                    DiagnosticSources.Process,
                    message: command.FileName,
                    detail: $$"""
                              {"fileName":{{JsonSerializer.Serialize(command.FileName)}},
                               "agent":{{JsonSerializer.Serialize(invocation.Agent)}},
                               "container":{{JsonSerializer.Serialize(invocation.Container.ToString())}}}
                              """,
                    ct: ct);
            }

            // NOT AN AGENT FAULT, AND NO REPAIR IS ASKED OF ANYBODY: nothing ran, and the likeliest
            // cause is an install or update in progress, so the words say what is true and the
            // class tells the Manager to re-send.
            return new AgentResult(
                -1,
                string.Empty,
                LaunchMissingText(command.FileName, _lookup),
                FailureClass: FailureClasses.LaunchMissing);
        }

        // From a system directory, never PATH: setsid runs before the agent prefix, so
        // it still holds the Host's capabilities. Built by the launcher every member shares.
        if (ChildProcess.StartInfo(resolvedFileName, invocation.WorkingDirectory, runAs) is not { } start)
        {
            return new AgentResult(
                -1,
                string.Empty,
                $"setsid is not in a root-owned system directory ({string.Join(", ", SystemCommand.Directories)}), so this member could not be started.");
        }

        // THE MEMBER'S OWN TEMPORARY FOLDER, never the /tmp every member and the Host share.
        // Refused rather than falling back to /tmp when the workspace is there but the folder
        // cannot be made: a member quietly back in the shared folder is the failure this closes.
        var memberTemp = await MemberTemp.EnsureAsync(invocation.WorkingDirectory, runAs, ct);

        if (memberTemp is null && Directory.Exists(invocation.WorkingDirectory))
        {
            return new AgentResult(
                -1,
                string.Empty,
                $"This member's temporary folder, or the link '{MemberTemp.LinkFor(invocation.WorkingDirectory)}' "
                + "that names it, could not be created, so this member was not started. Check that its "
                + "workspace and the Host's temp folder can be written, and that nothing else sits at that name.");
        }

        // History first, then the instruction that woke it - the order a human would read them in,
        // and the order that keeps the stable part at the front, which is what a prompt cache
        // matches on. Composed ONCE, here, because it now has two possible destinations.
        var prompt = string.IsNullOrEmpty(invocation.Context)
            ? invocation.Prompt
            : invocation.Context + "\n\n" + invocation.Prompt;

        // THE FILE FORM IS ORDERED THE OTHER WAY ROUND - INSTRUCTION FIRST - AND THAT DIFFERENCE IS
        // THE POINT. One rule, stated once: what is sent INLINE is history-first, and what an agent
        // has to READ is instruction-first.
        //
        // The prefix argument above holds only for the inline form. A file has no prefix to match:
        // it reaches the agent as the result of a read, and an agent reading a large file from the
        // top acts on the first instruction it meets.
        //
        // History-first in a file fails like this: a member reads the handover IN CHUNKS, finds a
        // PREVIOUS round's instruction near the top of the history and executes that - checking out
        // the branch named in a finished task, or re-running work already done - while every
        // dispatch is correct on the record, so it looks like a briefing problem. It is an ordering
        // problem.
        //
        // THE MARKERS ARE LOAD-BEARING: an agent that reads only the head must be able to tell what
        // its job is, and one that reads only the tail must not mistake finished history for a task.
        var fileBody = string.IsNullOrEmpty(invocation.Context)
            ? invocation.Prompt
            : "=== YOUR INSTRUCTION. THIS IS THE TASK. DO THIS. ==="
                + $"{Environment.NewLine}{Environment.NewLine}"
                + invocation.Prompt
                + $"{Environment.NewLine}{Environment.NewLine}"
                + "=== END OF INSTRUCTION ==="
                + $"{Environment.NewLine}{Environment.NewLine}"
                + "Everything below this line is YOUR HISTORY, for context only. It contains "
                + "instructions from EARLIER rounds which are already finished. Do not act on "
                + "anything below this line."
                + $"{Environment.NewLine}{Environment.NewLine}"
                + "=== HISTORY BEGINS ==="
                + $"{Environment.NewLine}{Environment.NewLine}"
                + invocation.Context;

        // Not every CLI reads its prompt from stdin. `claude -p` and `codex exec` do; `copilot -p`
        // and `grok -p` take it as the VALUE of a flag, and a preset for either would otherwise
        // launch and run with no prompt at all - a container that starts, costs money and answers
        // nothing. `{userPromptFile}` is the better of the two wherever an Agent supports it: a prompt
        // carries newlines and quotes, and a command line is the one place those become someone
        // else's problem.
        string? promptFile = null;

        if (command.Arguments.Any(a => HasToken(a, "userPromptFile", "promptFile")))
        {
            promptFile = Path.Combine(Path.GetTempPath(), $"os-{Guid.NewGuid():N}.prompt.txt");
            await File.WriteAllTextAsync(promptFile, fileBody, ct);
        }

        string? usageFile = null;

        if (command.Arguments.Any(a => a.Contains("{usageFile}", StringComparison.Ordinal)))
        {
            usageFile = Path.Combine(Path.GetTempPath(), $"os-{Guid.NewGuid():N}.usage.json");
        }

        // A PROMPT TOO LONG FOR ONE ARGUMENT IS HANDED OVER AS A FILE INSTEAD.
        //
        // Linux bounds each argument at MaxArgumentBytes, and `{userPrompt}` puts CONTEXT plus the
        // waking instruction in one, where the context is the ledger projection and grows with the
        // container's history. Indirection rather than truncation: cutting a prompt to fit silently
        // drops instructions, and a pointer keeps every word at the cost of one read.
        //
        // Only for a preset that passes `{userPrompt}`. One using `{userPromptFile}` or stdin has no
        // argument to overflow.
        var promptGoesOnTheCommandLine =
            command.Arguments.Any(a => HasToken(a, "userPrompt", "prompt"));

        if (promptGoesOnTheCommandLine && Encoding.UTF8.GetByteCount(prompt) >= MaxArgumentBytes)
        {
            var handover = Path.Combine(Path.GetTempPath(), $"os-{Guid.NewGuid():N}.prompt.txt");
            await File.WriteAllTextAsync(handover, fileBody, ct);
            promptFile = handover;

            // Imperative and short. The agent has to choose to read this, which is the one thing
            // indirection cannot guarantee, so it says what the file is and that everything is in
            // it - not merely that a file exists.
            prompt =
                $"YOUR TASK IS AT THE TOP OF THIS FILE - READ IT FIRST: {handover}"
                + $"{Environment.NewLine}{Environment.NewLine}"
                + "It was too long to pass on the command line. Read that file now. The task is at "
                + "the very top, under a line beginning `=== YOUR INSTRUCTION`; everything after "
                + "`=== HISTORY BEGINS ===` is finished work kept for context only, and instructions "
                + "found there have already been carried out. Do not begin work before you have "
                + "read the task at the top.";
        }

        var promptWentInAnArgument = false;
        string[] PromptTokens = ["{userPrompt}", "{prompt}", "{userPromptFile}", "{promptFile}"];

        invocation.Environment.TryGetValue("HARNESS_URL", out var mcpBase);
        invocation.Environment.TryGetValue("HARNESS_KEY", out var mcpKey);
        invocation.Environment.TryGetValue("HARNESS_MEMBER", out var mcpMember);
        var mcp = McpLaunchConfig.TryWrite(
            mcpBase, mcpKey, mcpMember ?? "member",
            log is null ? null : warning => log.LogWarning("{Warning}", warning));

        // One per invocation, for every preset: `{sessionId}` is a launch token like
        // `{mcpConfig}`, so a preset that names it (claude-headless, `--session-id`) has a
        // transcript whose name is known before the run starts. Nothing here branches on a preset.
        var sessionId = Guid.NewGuid().ToString();

        foreach (var argument in command.Arguments)
        {
            // Substituted INSIDE an already-tokenized element, never by re-splitting a joined
            // command line: a quote or a newline in the prompt must not become an argument
            // boundary. The same rule the system-prompt substitution below follows.
            var substituted = argument
                // BOTH SPELLINGS, permanently, for the reason `{systemFile}` is honoured:
                // `{prompt}` and `{promptFile}` are permanent aliases that existing agents.json files
                // carry. An unsubstituted one is not refused - it is the literal string
                // `{prompt}` handed to the CLI as the member's work, so the agent runs, bills, and
                // answers a question nobody asked.
                .Replace("{userPromptFile}", promptFile ?? "{userPromptFile}", StringComparison.Ordinal)
                .Replace("{promptFile}", promptFile ?? "{promptFile}", StringComparison.Ordinal)
                .Replace("{usageFile}", usageFile ?? "{usageFile}", StringComparison.Ordinal)
                .Replace("{sessionId}", sessionId, StringComparison.Ordinal)

                // **THE MEMBER'S WORKSPACE, AS AN ABSOLUTE PATH.** Every member has its own
                // workspace root and the platform spawns the child there - which is enough for a CLI
                // that treats its process working directory as "here". `agy` does not: it resolves
                // `.` against a scratch folder of its own under the user profile, SHARED BY EVERY
                // RUN AND EVERY MEMBER, and writes there while reporting success - a file asked for
                // "here" lands in `~/.gemini/antigravity-cli/scratch`, beside other members'
                // leftovers.
                //
                // This is the same defect the `copilot` preset carries `--add-dir .` for,
                // one step worse: a RELATIVE path does not fix it, because the thing being resolved
                // against is wrong. An absolute one does, verified.
                //
                // A member that appears to have written a file which is nowhere on the team's disk
                // is the failure this closes, and it is silent from every angle: the agent reports
                // the write, the exit code is 0, and the card goes green.
                .Replace("{workspace}", invocation.WorkingDirectory, StringComparison.Ordinal)
                .Replace("{userPrompt}", prompt, StringComparison.Ordinal)
                .Replace("{prompt}", prompt, StringComparison.Ordinal)

                // THE THIRD WAY TO SAY WHO A MEMBER IS, and the one a single-channel CLI needs.
                //
                // The other two put the system prompt somewhere of its own - a file named by a flag,
                // or a conventional file in the workspace. `agy` has neither: no flag takes it, and
                // AGENTS.md, GEMINI.md, .agy/AGENTS.md and ANTIGRAVITY.md are all ignored in print
                // mode, tested against the real CLI and still ignored with `--add-dir .`. Its whole
                // interface is one string.
                //
                // So the identity rides the same argument as the work; the alternative is a
                // preset with no system prompt at all - which this runner refuses a few lines above,
                // because an agent starting, costing money and not knowing what it is is the worst
                // state here.
                .Replace("{systemPrompt}", invocation.SystemPrompt, StringComparison.Ordinal);

            substituted = McpLaunchConfig.Apply(substituted, mcp);

            // ONLY A PROMPT TOKEN MEANS THE PROMPT TRAVELLED IN AN ARGUMENT. Comparing the whole
            // substituted argument would count `{mcpConfig}`, `{usageFile}`, `{workspace}` and
            // `{systemPrompt}` as well, so a preset that reads its prompt on stdin but names any of
            // those - `claude -p --mcp-config {mcpConfig}` - would be handed an empty stdin and exit 1
            // with "Input must be provided either through stdin or as a prompt argument".
            promptWentInAnArgument |= PromptTokens.Any(token => argument.Contains(token, StringComparison.Ordinal));
            start.ArgumentList.Add(substituted);
        }

        foreach (var (name, value) in invocation.Environment) start.Environment[name] = value;

        if (mcp is not null)
        {
            start.Environment["HARNESS_MCP_CONFIG"] = mcp.JsonPath;
            start.Environment["HARNESS_MCP_CONFIG_TOML"] = mcp.TomlPath;
        }

        // REMOVED, NOT CLEARED, AND THE ORDER MATTERS: after the merge above, so a caller
        // cannot reintroduce one of these by handing it in.
        //
        // `start.Environment` is a real dictionary pre-populated from the PARENT, which is what
        // makes `Remove` work here where `AgentEnvironment.Cleared` could not - that one is a
        // dictionary of strings, and no string means absent. Setting FORCE_COLOR empty is the
        // defect, not the fix: node reads a SET value of any kind as "force colour on".
        //
        // Defence in depth rather than the mechanism. `Program.cs` removes these from the Host's
        // own process at startup, which is the only thing that reaches the PTY path - Porta.Pty
        // merges and cannot express removal - so by the time we get here there is usually nothing
        // to remove. This covers a Host started some way that skips that.
        foreach (var name in AgentEnvironment.MustBeAbsent) start.Environment.Remove(name);

        // Only this command's own provider key, and any the caller handed in deliberately. The
        // Host holds every provider's key and `start.Environment` inherited all of them.
        AgentEnvironment.ScopeProviderKeys(start.Environment, command.FileName, invocation.Environment);

        // After the merge, so neither the catalog nor a team's env can point it elsewhere.
        if (memberTemp is not null) start.Environment[MemberTemp.Variable] = memberTemp;

        // Written to a file rather than an argument: a system prompt contains newlines and quotes,
        // and a command line is the one place those become someone else's problem.
        string? systemFile = null;

        if (command.SystemPromptArguments is { Count: > 0 } systemArguments
            && !string.IsNullOrWhiteSpace(invocation.SystemPrompt))
        {
            systemFile = Path.Combine(Path.GetTempPath(), $"os-{Guid.NewGuid():N}.system.txt");
            await File.WriteAllTextAsync(systemFile, invocation.SystemPrompt, ct);

            foreach (var argument in systemArguments)
            {
                // BOTH SPELLINGS, and `{systemFile}` is a PERMANENT alias rather than a deprecation.
                // Existing agents.json files carry it, including on instances nobody will ever
                // reset - and an unsubstituted token is not an error, it
                // is the literal string `{systemFile}` handed to the CLI as its system-prompt PATH.
                // The agent then launches knowing neither its own name nor its team, with nothing
                // refused and nothing logged. Two Replace calls buy that away forever.
                start.ArgumentList.Add(
                    argument
                        .Replace("{systemPromptFile}", systemFile, StringComparison.Ordinal)
                        .Replace("{systemFile}", systemFile, StringComparison.Ordinal));
            }
        }

        // The Agents with no system-prompt argument at all - codex, copilot and grok - read their
        // instructions from a conventional file in the working directory instead. Written rather
        // than dropped, because a member with no system prompt knows neither its own name nor its
        // team, and nothing refuses it or logs it.
        //
        // Guarded on the directory EXISTING, deliberately, and not merged into the fallback above:
        // when a container's workspace is missing, WorkingDirectory silently becomes the Host's own
        // current directory, and writing AGENTS.md there drops a file into whatever tree the Host
        // was started from - this repository, for anyone running it from a clone.
        if (command.InstructionsFile is { Length: > 0 } instructions
            && !string.IsNullOrWhiteSpace(invocation.SystemPrompt)
            && Directory.Exists(invocation.WorkingDirectory))
        {
            try
            {
                var instructionsPath = Path.Combine(invocation.WorkingDirectory, instructions);

                // The guard above tests the WORKSPACE ROOT, not this file's own parent - every
                // preset shipped today names a flat "AGENTS.md", for which the two are the same
                // directory, so that guard alone was never enough for a preset naming a NESTED
                // path. `Path.Combine` creates nothing, and `Directory.CreateDirectory` on a
                // directory that already exists is a no-op, so this is additive: it changes
                // behaviour only when `instructions` carries a subdirectory. Without it, the write
                // below throws `DirectoryNotFoundException` - which derives from `IOException`, so
                // the catch below swallowed it silently and the agent launched knowing neither its
                // own name nor its team, with a green card and nothing logged.
                var instructionsDirectory = Path.GetDirectoryName(instructionsPath);
                if (!string.IsNullOrEmpty(instructionsDirectory))
                {
                    Directory.CreateDirectory(instructionsDirectory);
                }

                // Path containment is a SEPARATE gap, deliberately not fixed here: a preset naming
                // "../../elsewhere/agent.md" is not run through `TeamDocuments.Resolve`, so this
                // write is not checked against the team's own root.
                await File.WriteAllTextAsync(instructionsPath, invocation.SystemPrompt, ct);
            }
            catch (IOException)
            {
                // A workspace that cannot be written is a launch that fails on its own terms, with
                // the agent's own error, which is more use to a reader than an exception from here.
            }
        }

        // NULL means unbounded. Read from the preset rather than from a constant here: how long a
        // run may take is a property of the Agent, and a number in this file would be a policy
        // nobody chose.
        //
        // Ignored for an Interactive preset by construction: nothing interactive reaches this
        // runner, and a terminal's lifetime belongs to `ConciergeReaper`.
        var timeout = catalog.Definition(invocation.Agent)?.TimeoutSeconds;

        // The caller's token and the preset's clock, as ONE token. `Stopping` fires for either, and
        // the two are told apart below by asking which - because they mean different things and are
        // fixed in different places.
        //
        // AN IDLE CLOCK, NOT A WALL CLOCK: every progress report this member files pushes its own
        // deadline out again, so `TimeSpan` means "this long with nothing to say". See
        // ChildProcess.Clock and RunHeartbeat.
        using var clock = ChildProcess.Clock(heartbeat, invocation.Container, timeout, ct);

        // The live view of this run, begun once the process exists and ended with the run.
        Watch? watchable = null;

        try
        {
            // Everything written for this child is handed to its group just before it
            // starts, so a file added to the MCP directory after TryWrite is covered too.
            if (runAs is not null)
            {
                if (mcp is not null) runAs.Share(Path.GetDirectoryName(mcp.JsonPath)!);
                foreach (var file in new[] { promptFile, systemFile }) if (file is not null) runAs.Share(file);
            }

            // A transcript the agent names itself is the newest one written from here on.
            var launchedAt = DateTimeOffset.UtcNow;

            // Launched, pumped, fed and waited on by the launcher every member shares: its own
            // session, the agent user, both pipes read before the wait, stdin written and CLOSED
            // (a CLI that reads it waits forever on a pipe nobody closed), the process group killed
            // at the end and a bounded drain. See ChildProcess.RunAsync.
            //
            // Only when the prompt did not already travel in an argument is it sent on stdin.
            // Sending it twice would have `copilot -p` answer one prompt while reading another off
            // stdin, and `codex exec` append a second copy as a <stdin> block.
            var outcome = await ChildProcess.RunAsync(
                start,
                promptWentInAnArgument ? null : prompt,
                clock.Stopping,

                // Only recorded, so a person can watch: nothing below reads it, so usage and
                // output are the same with or without a watcher.
                onStarted: _ => watchable = BeginLive(invocation, start, sessionId, launchedAt));

            if (outcome.Killed)
            {
                // RECORDED. A killed child is a run that did not finish, and without this row the
                // only record of it would be an `AgentResult` whose message reaches the card and the
                // transcript - both of which belong to the TEAM. Whoever is asking "what was this
                // instance doing" is not reading a member's card.
                //
                // WHICH clock ran out is on the ROW as well as in the sentence below, because the
                // two send a reader to different screens: a timeout is a setting on this Agent, a
                // stop is the Host going down.
                if (diagnostics is not null)
                {
                    await diagnostics.WriteAsync(
                        DiagnosticSeverity.Warning,
                        DiagnosticKinds.ProcessKilled,
                        DiagnosticSources.Process,
                        message: invocation.Container.ToString(),
                        detail: $$"""
                                  {"container":{{JsonSerializer.Serialize(invocation.Container.ToString())}},
                                   "agent":{{JsonSerializer.Serialize(invocation.Agent)}},
                                   "reason":"{{(clock.Expired ? "no-progress-timeout" : "host-stopping")}}",
                                   "timeoutSeconds":{{timeout ?? 0}}}
                                  """,

                        // CancellationToken.None, deliberately. `ct` is the token that has just
                        // been cancelled - passing it would cancel the write recording the
                        // cancellation, which is the one moment this row is worth having.
                        ct: CancellationToken.None);
            }

            // What it did before it was killed is still in its own transcript.
            var killedTranscript = await watchable!.TranscriptAsync();

            // WHICH clock ran out, in the words a reader can act on. A timeout is a setting on
            // this Agent; a stop is the Host going down and the run being reported as failed at
            // the next start. One message for both sends half of them to the wrong screen.
            return clock.Expired
                ? new AgentResult(
                    -1,
                    string.Empty,
                    $"This run went {timeout}s without reporting progress and was "
                    + $"stopped. That limit is '{invocation.Agent}' own, on the Agents screen - "
                    + "raise it there, or leave it unset for no limit at all. It measures "
                    + "SILENCE rather than total time, so a member that says what it is doing "
                    + "can work for as long as it needs to.",
                    ProcessId: outcome.ProcessId,

                    // THE SAME SPLIT, SAID AS A CLASS. Neither ever resumes automatically and
                    // they are still two classes rather than one, because a class is read by
                    // people as well as by the resume: a timeout is a SETTING on this Agent and
                    // a shutdown is the Host going down, and one word for both sends half the
                    // readers to the wrong screen exactly as one message would.
                    FailureClass: FailureClasses.Timeout,
                    AgentTranscript: killedTranscript)
                : new AgentResult(
                    -1,
                    string.Empty,
                    "This run was stopped before it finished, because the Host was shutting "
                    + "down. Whatever it had done is not recorded.",
                    ProcessId: outcome.ProcessId,
                    FailureClass: FailureClasses.Interrupted,
                    AgentTranscript: killedTranscript);
            }

            var heldOpen = outcome.HeldOpen;
            var output = outcome.Stdout;
            var errors = outcome.Stderr;
            var usage = ParseUsage(command.UsageFormat, output, errors, usageFile);

            // **AN ENVELOPE THAT SAYS IT FAILED IS A FAILURE, WHATEVER THE EXIT CODE.**
            //
            // agy can answer `status: ERROR` with `RESOURCE_EXHAUSTED (code 429)` - a provider rate
            // limit - while exiting 0 and printing a plausible summary of work it has not finished.
            // `Succeeded` is `LaunchError is null && ExitCode == 0 && ...`, so without this such a
            // run would be published as COMPLETED with its prose as the answer.
            //
            // The same shape `CredentialUseRunner` exists for - a member whose tools were denied,
            // exiting 0 and reported as a success - arriving through a different door: here the CLI
            // knows it failed and says so in a field, and this reads it.
            var envelopeError = EnvelopeFailure(command.UsageFormat, output);

            // WHAT KIND OF FAILURE, FROM THE BRAND'S OWN WORDS. The detection above
            // already tells a 429 from a crash, so its answer is handed to the classifier along
            // with everything else the run said rather than detected twice.
            //
            // CLASSIFIED ON EVERY RUN THAT REACHED HERE, not only the ones that exited non-zero,
            // because the envelope case is precisely a run that exited 0 and had failed. The
            // container discards the class for a run that succeeded.
            //
            // ONE CLOCK READING for the whole decision, so a reset time and the moment it is
            // measured against cannot drift between two calls.
            var finding = AgentFailureEvidence.Classify(
                command.UsageFormat,
                string.Join(
                    Environment.NewLine,
                    new[] { output, errors, envelopeError }.Where(part => !string.IsNullOrWhiteSpace(part))),
                DateTimeOffset.UtcNow);

            if (usage is not null && TryUnwrapOutput(command.UsageFormat, output) is { } unwrapped)
            {
                output = unwrapped;
            }

            var combined = new StringBuilder(output);

            if (heldOpen)
            {
                // Said in the OUTPUT rather than a log, because that is what reaches the transcript
                // and the card, where somebody looking at this run will be. Usually nothing is
                // missing - the agent wrote everything it had before exiting, and only the handle
                // outlived it - so this explains the pause rather than warning of a loss.
                combined.AppendLine();
                combined.AppendLine(
                    $"[harness] Something this run started is still holding its output after "
                    + $"{ChildProcess.DrainGrace.TotalSeconds:0}s - a server, a watcher, or a tunnel. The run is "
                    + "complete; anything written after this line was not captured.");

                // AND RECORDED: a post-exit drain hitting DrainGrace with a child still holding
                // output is a process failure the instance must be able to describe. The line above goes into the run's OUTPUT, which is where somebody
                // looking at that run will be; this row is for somebody looking at the INSTANCE,
                // asking why several teams' output is truncated in the same window. They are the
                // same fact at two scales.
                if (diagnostics is not null)
                {
                    await diagnostics.WriteAsync(
                        DiagnosticSeverity.Warning,
                        DiagnosticKinds.ProcessOutputHeldOpen,
                        DiagnosticSources.Process,
                        message: invocation.Container.ToString(),
                        detail: $$"""
                                  {"container":{{JsonSerializer.Serialize(invocation.Container.ToString())}},
                                   "agent":{{JsonSerializer.Serialize(invocation.Agent)}},
                                   "graceSeconds":{{ChildProcess.DrainGrace.TotalSeconds:0}}}
                                  """,
                        ct: ct);
                }
            }

            // Kept, not discarded: several CLIs write almost everything to stderr, so treating it as
            // failure-only output would throw away the answer.
            if (!string.IsNullOrWhiteSpace(errors)) combined.Append(errors);

            return new AgentResult(
                outcome.ExitCode,
                combined.ToString().Trim(),

                // The usage is kept even when the envelope failed: it was still spent, and a
                // refused run that cost 43k tokens must not be filed as costing nothing.
                envelopeError,
                Usage: usage,
                ProcessId: outcome.ProcessId,

                // NULL WHEN NOTHING RECOGNISED THE WORDS, which is the ordinary answer and the one
                // this design wants: a preset with no entry in the evidence table is honestly
                // silent, exactly as a preset with no usage format is today, and the container
                // files that as `unknown`.
                FailureClass: finding?.FailureClass,
                RetryAfter: finding?.RetryAfter,

                // Recorded on the run's terminal row; read by nothing above, so usage and
                // output are the same whether or not there is one.
                AgentTranscript: await watchable!.TranscriptAsync());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // RECORDED. The `AgentResult` below reaches the card and the transcript, which
            // belong to the team; this row is the instance's own copy, and it carries the exception
            // TYPE, which the sentence cannot. "Three members failed to launch on this host in one
            // minute" is a question about an instance, and this row is where it is answered.
            if (diagnostics is not null)
            {
                await diagnostics.WriteAsync(
                    DiagnosticSeverity.Error,
                    DiagnosticKinds.ProcessLaunchFailed,
                    DiagnosticSources.Process,
                    exceptionType: ex.GetType().FullName,
                    message: ex.Message,
                    detail: $$"""
                              {"container":{{JsonSerializer.Serialize(invocation.Container.ToString())}},
                               "agent":{{JsonSerializer.Serialize(invocation.Agent)}},
                               "fileName":{{JsonSerializer.Serialize(command.FileName)}}}
                              """,
                    ct: CancellationToken.None);
            }

            // A command that is not on PATH is the common case, and it is a LAUNCH error rather than
            // an exit code: the process never ran, so reporting "exit -1" would read as a program
            // that ran and failed.
            return new AgentResult(-1, string.Empty, ex.Message);
        }
        finally
        {
            watchable?.Dispose();

            // Deleted whatever happened. A run measured in seconds leaves one of these per
            // invocation, and a container that runs all day would otherwise fill the temp directory.
            mcp?.Delete();

            foreach (var temp in new[] { systemFile, promptFile, usageFile })
            {
                if (temp is null) continue;

                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                }
            }
        }
    }
    /// <summary>
    /// Linux's bound on one argument (MAX_ARG_STRLEN), terminating NUL included. A prompt at or
    /// over it is handed over as a file.
    /// </summary>
    public const int MaxArgumentBytes = 128 * 1024;

    /// <summary>
    /// This run, as the live route sees it: the transcript its preset names under the child's
    /// HOME, or why there is none. A preset that finds its transcript after launch is looked
    /// for in the background, for <see cref="LiveView.FindFor"/>.
    /// </summary>
    private Watch BeginLive(AgentInvocation invocation, ProcessStartInfo start, string sessionId, DateTimeOffset launchedAt)
    {
        // Recorded even where nothing is registered to watch, so the terminal row still names the transcript.
        var runs = live ?? new LiveRuns();

        if (catalog.Definition(invocation.Agent)?.LiveView is not { } view)
        {
            return new Watch(runs.Begin(invocation.Container, null, null,
                $"This member's agent ({invocation.Agent}) has no live view; only its progress lines show what it is doing."), null, runAs);
        }

        var home = start.Environment.TryGetValue("HOME", out var set) && !string.IsNullOrEmpty(set)
            ? set
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (LiveView.Refusal(view) is null && !string.IsNullOrEmpty(home) && view.Find is { } find)
        {
            var run = runs.Begin(invocation.Container, null, view.Format, "", finding: true);
            var stopLooking = new CancellationTokenSource();

            // The file system's clock is coarse: a file written just after launch can read a few
            // milliseconds before it.
            _ = LocateAsync(run, find, home, start.WorkingDirectory, launchedAt - FindSlack, invocation.Agent, stopLooking.Token);
            return new Watch(run, stopLooking, runAs);
        }

        return LiveView.Resolve(view, home, start.WorkingDirectory, sessionId) is { } transcript
            ? new Watch(runs.Begin(invocation.Container, transcript, view.Format, ""), null, runAs)
            : new Watch(runs.Begin(invocation.Container, null, null,
                $"This member's agent ({invocation.Agent}) has a live view this platform cannot read: {LiveView.Refusal(view) ?? "its agent has no home folder"}."), null, runAs);
    }

    private static readonly TimeSpan FindSlack = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan FindPoll = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Looks for the transcript until it is found, <see cref="LiveView.FindFor"/> has passed since
    /// launch, or the run ends (<paramref name="stop"/>) and one last look finds nothing. Never throws:
    /// it is nobody's task to observe.
    /// </summary>
    private async Task LocateAsync(
        LiveRun run, AgentLiveViewFind find, string home, string workspace, DateTimeOffset since, string agent,
        CancellationToken stop)
    {
        var deadline = since + LiveView.FindFor;

        while (true)
        {
            var last = stop.IsCancellationRequested || DateTimeOffset.UtcNow >= deadline;

            try
            {
                if (await AgentFiles.FindAsync(find, home, workspace, since, runAs, CancellationToken.None) is { } found)
                {
                    run.Found(found);
                    return;
                }
            }
            catch (Exception exception)
            {
                log?.LogWarning("Looking for {Agent}'s session transcript failed: {Message}", agent, exception.Message);
            }

            if (last)
            {
                run.Found(null,
                    $"This member's agent ({agent}) wrote no session transcript this platform could find within "
                    + $"{LiveView.FindFor.TotalSeconds:0} seconds of launch.");
                return;
            }

            try { await Task.Delay(FindPoll, stop); }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>One run's <see cref="LiveRun"/>, and the search for its transcript when there is one.</summary>
    private sealed class Watch(LiveRun run, CancellationTokenSource? stopLooking, AgentLaunchUser? runAs) : IDisposable
    {
        /// <summary>How long the run's end waits for the last look to finish.</summary>
        private static readonly TimeSpan LastLook = TimeSpan.FromSeconds(10);

        /// <summary>
        /// The transcript this run wrote, or null: the search is told to take its last look, and a
        /// path named before launch counts only when the agent created the file.
        /// </summary>
        public async Task<AgentTranscript?> TranscriptAsync()
        {
            stopLooking?.Cancel();

            var located = run.Located;
            if (await Task.WhenAny(located, Task.Delay(LastLook)) != located || await located is not { } path
                || run.Format is not { } format)
            {
                return null;
            }

            try
            {
                return await AgentFiles.ExistsAsync(path, runAs, CancellationToken.None) ? new AgentTranscript(path, format) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void Dispose()
        {
            stopLooking?.Cancel();
            run.Dispose();
        }
    }

    /// <summary>
    /// BOTH STREAMS ARE HANDED IN AND EACH FORMAT PICKS, rather than one concatenated blob. The JSON
    /// formats parse stdout AS a document, so appending stderr to it would make every claude and grok
    /// run a parse failure - while codex prints its total to stderr and nowhere else, which is how it
    /// reported nothing for its whole life here.
    /// </summary>
    private InvocationUsage? ParseUsage(
        string? usageFormat, string stdout, string stderr, string? usageFile)
    {
        if (string.IsNullOrWhiteSpace(usageFormat)) return null;

        try
        {
            var parsed = usageFormat switch
            {
                ClaudeJson => ParseClaude(stdout),
                GrokJson => ParseGrok(stdout),
                CopilotUsageFile => ParseCopilot(usageFile),
                CodexTotal => ParseCodex(stdout, stderr),
                AntigravityJson => ParseAntigravity(stdout),
                _ => null,
            };

            // A KNOWN FORMAT THAT FINDS NOTHING IS A PARSE FAILURE AND MUST SAY SO. Only the throwing
            // paths below logged, so a parser that simply returned null was silent - which is most of
            // why codex reported nothing for months with a `UsageFormat` set and nobody looking. An
            // UNKNOWN format stays quiet: it has no parser, so there is nothing to have failed.
            if (parsed is null
                && usageFormat is ClaudeJson or GrokJson or CopilotUsageFile or CodexTotal
                    or AntigravityJson)
            {
                LogUsageParseFailure(usageFormat, stdout, usageFile);
            }

            return parsed;
        }
        catch (JsonException)
        {
            LogUsageParseFailure(usageFormat, stdout, usageFile);
            return null;
        }
        catch (IOException)
        {
            LogUsageParseFailure(usageFormat, stdout, usageFile);
            return null;
        }
        catch (KeyNotFoundException)
        {
            LogUsageParseFailure(usageFormat, stdout, usageFile);
            return null;
        }
        catch (InvalidOperationException)
        {
            LogUsageParseFailure(usageFormat, stdout, usageFile);
            return null;
        }

        // **A USAGE PROBLEM MUST NEVER DESTROY A RUN.**
        //
        // `InvocationUsage` refuses a shape it cannot file - a cached count larger than the input it
        // is part of, a negative figure - by THROWING, which is right for a record that guards its
        // own invariants. Uncaught, an exception from the ACCOUNTING would become a `launchError`,
        // and an agent that had finished its work would be published as FAILED with an empty output
        // and an empty transcript - the work thrown away because the numbers would not add up.
        //
        // `(unknown)` is a real state here and a cheap one; losing the work is neither.
        catch (ArgumentException)
        {
            LogUsageParseFailure(usageFormat, stdout, usageFile);
            return null;
        }
        catch (OverflowException)
        {
            LogUsageParseFailure(usageFormat, stdout, usageFile);
            return null;
        }
    }

    /// <summary>
    /// What a CLI said went wrong INSIDE an envelope it returned with exit 0, or null when it
    /// reported no failure.
    ///
    /// ONLY `antigravity-json` TODAY, because it is the only seeded format with a status field. The
    /// others report failure the ordinary way, through the exit code, and asking them a question
    /// their envelope does not answer would invent a state they never reported.
    ///
    /// SWALLOWS A MALFORMED ENVELOPE rather than throwing. This runs on the completion path of every
    /// run: an exception here would turn output that merely could not be parsed into a lost run,
    /// which is worse than any unparsed envelope.
    /// </summary>
    private static string? EnvelopeFailure(string? usageFormat, string stdout)
    {
        if (usageFormat != AntigravityJson || string.IsNullOrWhiteSpace(stdout)) return null;

        try
        {
            using var document = JsonDocument.Parse(stdout);

            if (!document.RootElement.TryGetProperty("status", out var status)
                || status.ValueKind != JsonValueKind.String
                || string.Equals(status.GetString(), "SUCCESS", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // IT CARRIES WHAT THE PROVIDER SAID. "Something failed" sends a manager to retry the
            // same thing; a 429 names a wait and a quota names a person.
            var detail = document.RootElement.TryGetProperty("error", out var error)
                    && error.ValueKind == JsonValueKind.String
                ? error.GetString()
                : null;

            return string.IsNullOrWhiteSpace(detail)
                ? $"The agent reported status '{status.GetString()}' and gave no reason."
                : detail;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryUnwrapOutput(string? usageFormat, string stdout)
    {
        var proseKey = usageFormat switch
        {
            ClaudeJson => "result",
            GrokJson => "text",
            AntigravityJson => "response",
            _ => null,
        };

        if (proseKey is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(stdout);

        return document.RootElement.TryGetProperty(proseKey, out var result)
               && result.ValueKind == JsonValueKind.String
            ? result.GetString() ?? string.Empty
            : null;
    }

    private void LogUsageParseFailure(string usageFormat, string stdout, string? usageFile)
    {
        if (log is null) return;

        var line = usageFormat == CopilotUsageFile
            ? FirstLineFromFile(usageFile)
            : FirstLine(stdout);

        log.LogWarning(
            "Usage parse failed for format '{UsageFormat}'. First line: {FirstLine}",
            usageFormat,
            line);
    }

    private static string FirstLineFromFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return "(missing usage file)";
        }

        try
        {
            return FirstLine(File.ReadAllText(path));
        }
        catch (IOException)
        {
            return "(usage file unreadable)";
        }
    }

    private static string FirstLine(string text)
    {
        const int maxLength = 200;
        var first = (text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? string.Empty;
        return first.Length > maxLength ? first[..maxLength] : first;
    }

    private static InvocationUsage ParseClaude(string stdout)
    {
        using var document = JsonDocument.Parse(stdout);
        var usage = document.RootElement.GetProperty("usage");

        var input = usage.GetProperty("input_tokens").GetInt32();
        var output = usage.GetProperty("output_tokens").GetInt32();
        var cacheRead = OptionalInt(usage, "cache_read_input_tokens");
        var cacheCreation = OptionalInt(usage, "cache_creation_input_tokens");
        var reasoning = usage.TryGetProperty("output_tokens_details", out var details)
            ? OptionalInt(details, "thinking_tokens")
            : null;

        return new InvocationUsage(
            input,
            output,
            "claude",
            cacheRead,
            reasoning,
            cacheCreation);
    }

    /// <summary>
    /// Antigravity prints ONE JSON object on stdout and nothing on stderr, which is the cleanest
    /// envelope of the four. Measured against the real CLI.
    ///
    /// ITS USAGE BLOCK IS THE RICHEST HERE - input, output, thinking and cache-read all broken out,
    /// where codex reports a single combined figure. Everything it reports is carried and nothing is
    /// inferred: `total_tokens` is present too, and is deliberately NOT used, because
    /// `InvocationUsage` derives what it needs from the split and a second store of one fact is two
    /// answers waiting to disagree.
    ///
    /// **`cache_read_tokens` IS BESIDE `input_tokens`, NOT INSIDE IT**, so it is ADDED - the same
    /// shape `ParseGrok` and `ParseClaude` already handle.
    ///
    /// **A field that is zero in your sample tells you nothing about its relationship to the
    /// others.** Treating `cache_read_tokens` as part of `input_tokens` makes a real run fail with
    /// `CachedIn cannot be greater than TokensIn`, its output and transcript gone.
    /// </summary>
    private static InvocationUsage ParseAntigravity(string stdout)
    {
        using var document = JsonDocument.Parse(stdout);
        var usage = document.RootElement.GetProperty("usage");

        var input = usage.GetProperty("input_tokens").GetInt32();
        var cacheRead = OptionalInt(usage, "cache_read_tokens");

        return new InvocationUsage(
            input,
            usage.GetProperty("output_tokens").GetInt32(),
            "antigravity",
            cacheRead,
            OptionalInt(usage, "thinking_tokens"));
    }

    private static InvocationUsage ParseGrok(string stdout)
    {
        using var document = JsonDocument.Parse(stdout);
        var usage = document.RootElement.GetProperty("usage");

        var input = usage.GetProperty("input_tokens").GetInt32();
        var output = usage.GetProperty("output_tokens").GetInt32();
        var cacheRead = OptionalInt(usage, "cache_read_input_tokens");
        var cacheCreation = OptionalInt(usage, "cache_creation_input_tokens");
        var reasoning = OptionalInt(usage, "reasoning_tokens");

        return new InvocationUsage(
            input,
            output,
            "grok",
            cacheRead,
            reasoning,
            cacheCreation);
    }

    /// <summary>
    /// Codex prints `tokens used N` and nothing else about usage - no JSON envelope, no split.
    ///
    /// THE LAST MATCH, NEVER THE FIRST. The figure is printed as the run goes, so an early one is
    /// a partial count of work still in progress; taking it would understate exactly the long runs
    /// that cost the most, which is exactly how an estimate goes wrong.
    ///
    /// No match is NULL, not zero. A run whose output never named a total did not report nothing -
    /// it reported nothing measurable, and `(unknown)` is the honest rendering of that.
    /// </summary>
    /// <summary>
    /// **CODEX PRINTS ITS TOTAL ON STDERR, AND READING ONLY STDOUT NEVER FINDS IT.** A codex
    /// transcript ends `tokens used\n14,038`, and stdout alone carries no usage at all.
    ///
    /// In `exec` mode codex writes its session log to stderr and only the final assistant message to
    /// stdout, which any transcript shows: the banner, the `workdir:` header and every `exec` line
    /// sit AFTER the answer, where <see cref="RunAsync"/> appends stderr. The token total is the last
    /// line of that stream.
    ///
    /// NOT A DASHBOARD PROBLEM ONLY. The team budget and the instance spend limit are both computed
    /// from recorded usage, so without this a team running codex could not be bounded by either - the runaway
    /// spend those bounds exist to stop would be invisible on the preset most likely to be managing it.
    ///
    /// STDERR IS ASKED FIRST because that is where the real CLI prints. Stdout stays as a fallback
    /// rather than being dropped: a wrapper, a shim or a future codex may put it there, and every
    /// stand-in in the suite does.
    /// </summary>
    private static InvocationUsage? ParseCodex(string stdout, string stderr)
    {
        return Total(stderr) ?? Total(stdout);

        static InvocationUsage? Total(string stream)
        {
            var matches = CodexTokens().Matches(stream);

            if (matches.Count == 0) return null;

            // THE LAST ONE WINS. Codex prints a running figure as it goes, so an earlier match is a
            // partial count of a run still in progress.
            var digits = matches[^1].Groups[1].Value.Replace(",", string.Empty, StringComparison.Ordinal);

            return int.TryParse(digits, out var total) ? InvocationUsage.Combined(total, "codex") : null;
        }
    }

    /// <summary>Thousands separators are optional because codex prints them and a test may not.</summary>
    [GeneratedRegex(@"tokens used\s+([\d,]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CodexTokens();

    private static InvocationUsage? ParseCopilot(string? usageFile)
    {
        if (string.IsNullOrWhiteSpace(usageFile) || !File.Exists(usageFile)) return null;

        using var document = JsonDocument.Parse(File.ReadAllText(usageFile));

        if (!document.RootElement.TryGetProperty("modelMetrics", out var modelMetrics)
            || modelMetrics.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("modelMetrics missing");
        }

        var input = 0;
        var output = 0;
        var hasCached = false;
        var cached = 0;
        var hasReasoning = false;
        var reasoning = 0;
        var measured = false;

        foreach (var model in modelMetrics.EnumerateObject())
        {
            if (!model.Value.TryGetProperty("usage", out var usage)
                || usage.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            input = checked(input + usage.GetProperty("inputTokens").GetInt32());
            output = checked(output + usage.GetProperty("outputTokens").GetInt32());
            measured = true;

            if (OptionalInt(usage, "cacheReadTokens") is { } modelCached)
            {
                cached = checked(cached + modelCached);
                hasCached = true;
            }

            if (OptionalInt(usage, "reasoningTokens") is { } modelReasoning)
            {
                reasoning = checked(reasoning + modelReasoning);
                hasReasoning = true;
            }
        }

        if (!measured) throw new InvalidOperationException("No modelMetrics usage entries");

        return new InvocationUsage(
            input,
            output,
            "copilot",
            hasCached ? cached : null,
            hasReasoning ? reasoning : null);
    }

    private static int? OptionalInt(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value)) return null;

        return value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;
    }

    /// <summary>What a pump has written so far, read under the lock it appends under.</summary>
    /// <summary>
    /// Whether an argument carries either spelling of a token.
    ///
    /// The alias spellings - `{prompt}`, `{promptFile}`, `{systemFile}` - are PERMANENT rather than a
    /// deprecation. Existing catalogs hold them, and the two call sites that DETECT a token matter as
    /// much as the one that substitutes it: a preset on the alias spelling that was not detected here would quietly lose its prompt file, or quietly
    /// stop being capped, with nothing failing either way.
    /// </summary>
    private static bool HasToken(string argument, string current, string alias) =>
        argument.Contains($"{{{current}}}", StringComparison.Ordinal)
        || argument.Contains($"{{{alias}}}", StringComparison.Ordinal);
}

/// <param name="SystemPromptArguments">
/// How this agent takes a system prompt, with <c>{systemPromptFile}</c> substituted for a temp file the
/// runner writes. Null means it has no such mechanism and the system prompt is not passed at all -
/// which is honest, and far better than the alternative of prepending it to the user prompt, where
/// it comes back out in everything the agent echoes.
/// </param>
public sealed record AgentCommand(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<string>? SystemPromptArguments = null,
    string? InstructionsFile = null,
    string? UsageFormat = null,
    bool LanguageModel = true);

/// <summary>
/// Which command each container's agent is. Configuration, not code - the reason a container is data
/// and adding one needs no plugin loader.
/// </summary>
public sealed class AgentCatalog(
    IReadOnlyList<AgentDefinition> definitions,
    Func<IReadOnlyDictionary<string, IReadOnlyList<string>>>? tagOverrides = null)
{
    // The definitions and NOTHING ELSE - in particular no per-container map of resolved commands.
    // Every reader can resolve the answer from a member's own agent name, and a second store of
    // that fact drifts apart from the first without anything failing. Do not add one: a cache keyed
    // on a container is a cache that has to be invalidated by every path that changes what a
    // container runs, and there is no such thing as remembering to do that forever.
    //
    // Built-in presets and the person's custom ones, in one list. Prompts are not here: the
    // prompt is chosen by role (BuiltInPrompts), never by the preset or a person.
    private IReadOnlyList<AgentDefinition> _definitions = definitions;

    // THE OPERATOR'S TAGS FOR A BUILT-IN, the tenant setting `agents.tags`. A delegate,
    // asked on every read and never captured, so a change reaches the next hire with no restart.
    // Only a built-in is overridden: a custom preset carries its own tags in agents.json.
    private readonly Func<IReadOnlyDictionary<string, IReadOnlyList<string>>> _tagOverrides =
        tagOverrides ?? (() => new Dictionary<string, IReadOnlyList<string>>());

    /// <summary>Every preset, built-in and custom, a built-in carrying the operator's tags when
    /// it has them: what the Agents screen renders.</summary>
    public IReadOnlyList<AgentDefinition> Definitions
    {
        get
        {
            var overrides = _tagOverrides();
            return overrides.Count == 0 ? _definitions : [.. _definitions.Select(d => WithOverride(d, overrides))];
        }
    }

    /// <summary>Whether this preset's tags are the operator's rather than the build's.</summary>
    public bool TagsFromOperator(string agent) =>
        AgentCatalogFile.IsBuiltIn(agent) && OverrideFor(agent, _tagOverrides()) is not null;

    /// <summary>The tags a built-in preset carries in the build, or null for a custom one.</summary>
    public IReadOnlyList<string>? BuildTags(string agent) =>
        AgentCatalogFile.BuiltIns()
            .FirstOrDefault(d => string.Equals(d.Name, agent, StringComparison.OrdinalIgnoreCase))
            ?.Tags ?? (AgentCatalogFile.IsBuiltIn(agent) ? [] : null);

    /// <summary>Entries of <c>agents.tags</c> that name no built-in preset. Ignored, never an
    /// error: a preset removed from the build strands its entry rather than failing a read.</summary>
    public IReadOnlyList<string> IgnoredTagOverrides =>
        [.. _tagOverrides().Keys.Where(name => !AgentCatalogFile.IsBuiltIn(name)).Order(StringComparer.Ordinal)];

    /// <summary>The custom presets alone: what agents.json holds and a PUT replaces.</summary>
    public IReadOnlyList<AgentDefinition> Custom =>
        [.. _definitions.Where(d => !AgentCatalogFile.IsBuiltIn(d.Name))];

    public void Replace(IReadOnlyList<AgentDefinition> definitions) => _definitions = definitions;

    /// <summary>
    /// The headless command for a preset, or null.
    ///
    /// TWO reasons for null and both are refusals: this tenant has no such preset, or it has one
    /// that is INTERACTIVE. The mode check is what stops `shell` being offered as a member's Agent
    /// and `echo` as a Concierge - failures the previous shape could only refuse at launch,
    /// after someone had already chosen.
    ///
    /// This was a `switch` whose default arm returned `claude`, so any name it did not recognise
    /// launched Claude Code: a typo was an Agent that appeared to work, running a program nobody
    /// asked for. Null is what lets every caller refuse by name instead.
    /// </summary>
    public AgentCommand? For(string agent) => Launch(agent, AgentMode.Headless);

    /// <summary>
    /// The interactive command, or null on the same two conditions. Not the headless command with a
    /// flag removed: `claude -p` prints one answer and exits, which is right for an AC woken by a
    /// message and useless for a person at a terminal.
    /// </summary>
    public AgentCommand? Interactive(string agent) => Launch(agent, AgentMode.Interactive);

    // Guards `Launch` being null in ADDITION to `LoadCustom` refusing to hand out a definition
    // shaped that way - defence in depth for one `is not null`, matching the write side's own
    // guard in AgentEndpoints.RefusalForLaunch. `Replace` and the two-arg AgentCatalog
    // constructor both take a raw IReadOnlyList<AgentDefinition> with no validation of their
    // own, so a caller that bypasses LoadCustom - a test, or code added later - can still hand
    // this a definition with a null Launch, and an NRE here is the failure this guards.
    private AgentCommand? Launch(string agent, AgentMode mode)
    {
        var definition = Definition(agent);

        return definition is not null && definition.Mode == mode && definition.Launch is { } launch
            ? launch.ToCommand()
            : null;
    }

    /// <summary>Case-insensitive, matching how a stored `team_members.agent` is read back.</summary>
    public AgentDefinition? Definition(string agent) =>
        _definitions.FirstOrDefault(
            d => string.Equals(d.Name, agent, StringComparison.OrdinalIgnoreCase)) is { } definition
            ? WithOverride(definition, _tagOverrides())
            : null;

    private static AgentDefinition WithOverride(
        AgentDefinition definition, IReadOnlyDictionary<string, IReadOnlyList<string>> overrides) =>
        AgentCatalogFile.IsBuiltIn(definition.Name) && OverrideFor(definition.Name, overrides) is { } tags
            ? definition with { Tags = tags }
            : definition;

    private static IReadOnlyList<string>? OverrideFor(
        string agent, IReadOnlyDictionary<string, IReadOnlyList<string>> overrides) =>
        overrides.FirstOrDefault(e => string.Equals(e.Key, agent, StringComparison.OrdinalIgnoreCase)).Value;

}
