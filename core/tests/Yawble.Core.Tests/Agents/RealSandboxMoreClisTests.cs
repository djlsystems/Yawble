using Yawble.Core.Agents;
using Yawble.Core.Launch;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;
using Yawble.Core.Tests.Guardrail;
using Xunit;

namespace Yawble.Core.Tests.Agents;

/// <summary>
/// Codex and GitHub Copilot as members, under a REAL sandbox with the person's REAL CLIs and sign-ins (the launch
/// a member run gets: preset arguments, environment, seeded config, the CLI's vendor hosts by CONNECT): the CLI
/// answers a prompt. Codex runs on a COPY of ~/.codex/auth.json; a copy the run refreshed is written back. A real model
/// call on the person's account: only with YAWBLE_REAL_SIGNIN_PROMPT=1.
/// </summary>
[Collection(RealSandboxGuardrailCollection.Name)]
[Trait("Category", "HostCli")]
public class RealSandboxMoreClisTests(ITestOutputHelper output)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("codex", "Reply with exactly the single word: pong", "pong")]
    [InlineData("copilot", "Reply with exactly the single word: pong", "pong")]
    // A prompt that needs the CLI's shell tool (Grok's tools failed with "workspace session not found" in the sandbox).
    [InlineData("grok", "Use your shell tool to run the command: echo yawble-tool-ok . Then reply with only the command's output.", "yawble-tool-ok")]
    [InlineData("codex", "Use your shell tool to run the command: echo yawble-tool-ok . Then reply with only the command's output.", "yawble-tool-ok")]
    // Antigravity 1.5.2: its own command tool cannot run in an app container (exebox calls CreateDesktop), so it is
    // denied and commands go through the shell MCP server (yawble-bridge --shell).
    // "[exit code 0]" is written only by run_shell, so the model cannot answer from the prompt alone.
    [InlineData("antigravity", "Run the command: echo yawble-tool-ok . Then reply with the tool's complete output, verbatim.", "[exit code 0]")]
    [InlineData("antigravity", "Without running any command, use your file tools: create the file note.txt containing yawble-file-ok, then read it back and reply with only its content.", "yawble-file-ok")]
    public async Task TheCliAnswersAHeadlessPromptAsAMember(string id, string prompt, string expected)
    {
        if (Environment.GetEnvironmentVariable("YAWBLE_REAL_SIGNIN_PROMPT") != "1")
            Assert.Skip("a real model call on the person's account: set YAWBLE_REAL_SIGNIN_PROMPT=1 to run it");
        if (Environment.GetEnvironmentVariable("YAWBLE_REAL_ONLY") is { Length: > 0 } only && only != id)
            Assert.Skip($"YAWBLE_REAL_ONLY={only}");
        var detector = CliDetectorRegistry.ForThisHost();
        var cli = (await detector.DetectAllAsync(Ct)).Single(c => c.CliId == id);
        if (!cli.Found) Assert.Skip($"{id} is not installed on this host");
        if (cli.SignIn is not { Status: SignInStatus.SignedIn }) Assert.Skip($"{id} is not signed in on this host: {cli.SignIn?.Detail}");
        if (Launch.RealSandboxHost.Refusal is { } why) Assert.Skip(why);

        await using var kit = GuardrailKit.Create(realHome: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), clis: detector, realGateway: true);
        var registry = new RunRegistry();
        LiveRun? live = null;
        registry.Added += r => live = r;
        var subject = new LaunchMember(0, "_real-more", id, AgentClis.Parse(id));
        var outcome = await kit.Launcher(Launch.RealSandboxHost.Backend(), new HostSignInModes(), registry)
            .RunAsync(new LaunchRequest("_real-more", id, RunKind.Headless, prompt, null, Subject: subject), Ct);
        var text = RealSandboxHostSignInTests.Redact(System.Text.Encoding.UTF8.GetString(live?.Output.Snapshot() ?? []));
        var events = kit.Gateways.Log.Read();
        output.WriteLine($"{id} ({cli.Version}): end {outcome.End} {outcome.Reason}\n{text}");
        foreach (var e in events.Where(e => e.Kind == Yawble.Core.Gateway.GatewayEventKinds.ConnectDenied))
            output.WriteLine($"  denied: {e.Target} {e.Detail}");
        foreach (var e in events.Where(e => e.Kind != Yawble.Core.Gateway.GatewayEventKinds.ConnectDenied))
            output.WriteLine($"  {e.Kind} {e.Vhost} {e.Method} {e.Target} -> {e.Status} {e.Detail}");
        Launch.RealSandbox.Report($"{id} ({cli.Version}) as a member: end {outcome.End} {outcome.Reason}\n{text}");
        foreach (var g in outcome.Guardrails) output.WriteLine($"  guardrail: {g.Kind} {g.Action} {g.Path}");
        foreach (var f in Directory.EnumerateFiles(kit.App, "*", SearchOption.AllDirectories).Where(f => f.Contains("_real-more") && f.Contains(".codex")))
            output.WriteLine($"  codex home file: {f[kit.App.Length..]} ({new FileInfo(f).Length} bytes)");
        Assert.Equal(RunEnd.Completed, outcome.End);
        foreach (var e in events.Where(e => e.Kind == "path-denied")) output.WriteLine($"  path denied: {e.Method} {e.Target}");
        Assert.DoesNotContain("workspace session not found", text);
        Assert.Contains(expected, text, StringComparison.OrdinalIgnoreCase);
        // Copilot's and Codex's sign-ins are held by the gateway: the run had a placeholder only, and the gateway put the
        // person's token on.
        if (id is "copilot" or "codex")
            Assert.Contains(events, e => e.Kind == Yawble.Core.Gateway.GatewayEventKinds.Forward && e.Status is >= 200 and < 300
                                         && (e.Detail ?? "").Contains($"credential swapped: bearer (host {(id == "codex" ? "Codex" : "Copilot")} sign-in)"));
    }

    /// <summary>The CLI's TUI on the run's PTY (the Agents dialog's Test), given an initial prompt that needs its shell tool.</summary>
    [Theory]
    [InlineData("grok")]
    [InlineData("codex")]
    public async Task TheTuiRunsAShellToolWithAnInitialPrompt(string id)
    {
        if (Environment.GetEnvironmentVariable("YAWBLE_REAL_SIGNIN_PROMPT") != "1")
            Assert.Skip("a real model call on the person's account: set YAWBLE_REAL_SIGNIN_PROMPT=1 to run it");
        if (Environment.GetEnvironmentVariable("YAWBLE_REAL_ONLY") is { Length: > 0 } only && only != id)
            Assert.Skip($"YAWBLE_REAL_ONLY={only}");
        var detector = CliDetectorRegistry.ForThisHost();
        var cli = (await detector.DetectAllAsync(Ct)).Single(c => c.CliId == id);
        if (!cli.Found || cli.SignIn is not { Status: SignInStatus.SignedIn }) Assert.Skip($"{id} is not installed and signed in");
        if (Launch.RealSandboxHost.Refusal is { } why) Assert.Skip(why);

        await using var kit = GuardrailKit.Create(realHome: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), clis: detector, realGateway: true);
        var registry = new RunRegistry();
        LiveRun? live = null;
        registry.Added += r => live = r;
        var subject = new LaunchMember(0, "_real-more", id, AgentClis.Parse(id));
        var run = kit.Launcher(Launch.RealSandboxHost.Backend(), new HostSignInModes(), registry)
            .RunAsync(new LaunchRequest("_real-more", id, RunKind.Interactive,
                "Use your shell tool to run the command: echo yawble-tool-ok . Then reply with only the command's output.", null, new PtySize(40, 140), Subject: subject), Ct);
        string Screen() => System.Text.Encoding.UTF8.GetString(live?.Output.Snapshot() ?? []);
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < deadline && !run.IsCompleted
               && !Screen().Contains("workspace session not found", StringComparison.OrdinalIgnoreCase)
               && System.Text.RegularExpressions.Regex.Matches(Screen(), "yawble-tool-ok").Count < 3)
            await Task.Delay(500, Ct);
        await Task.Delay(3000, Ct);
        var screen = Screen();
        live?.Stop();
        await run.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        var path = Path.Combine(Path.GetTempPath(), $"yawble-tui-{id}.log");
        File.WriteAllText(path, screen);
        foreach (var f in Directory.EnumerateFiles(kit.App, "*.log", SearchOption.AllDirectories).Where(f => f.Contains("_real-more")))
            File.Copy(f, Path.Combine(Path.GetTempPath(), $"yawble-tui-{id}-{Path.GetFileName(f)}"), overwrite: true);
        output.WriteLine($"{id}: screen saved to {path}");
        Assert.DoesNotContain("workspace session not found", screen);
        Assert.DoesNotContain("program not found", screen);
        Assert.Contains("Worked for", screen); // the turn finished (Grok's TUI footer)
    }
}
