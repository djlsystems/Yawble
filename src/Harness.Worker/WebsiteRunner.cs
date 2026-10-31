using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// A WEBSITE CONNECTION'S RUN IN THE PLATFORM'S BROWSER, ON THIS WORKER (CON 6): the Team Connections
/// plugin's website runner (<c>plugins/team-connections/website/run.mjs</c>, the Web Credentials
/// plugin's Node and Playwright code moved there) started by this worker as ITS OWN USER - never the
/// agent's, so the connection's values and its profile stay where no agent can read them - in the
/// platform's own Google Chrome (<see cref="ChromiumSignInBrowser.Root"/>, the sign-in page's) and the
/// connection's own profile (<see cref="SignInProfiles"/>, the one Sign in uses), whose lock it holds
/// while the run lasts, so a run and a person's sign-in never share it.
/// </summary>
/// <remarks>
/// The request goes in on stdin and one JSON answer comes back on stdout. Every value of the
/// connection - its masked fields, its username and each part of its saved session - is taken out of
/// the answer's words again here; the handoff of a <c>session</c> alone carries one, sealed by the
/// connection. Nothing the browser saw is logged. Tests point it at a fake site and a Chrome of their own.
/// </remarks>
public sealed class WebsiteRunner(
    string? runner = null, string? chromeRoot = null, string? profileRoot = null, IReadOnlyList<string>? extraArgs = null,
    IReadOnlyDictionary<string, string>? environment = null, string? node = null)
{
    /// <summary>The runner beside the Host: the built-in plugin's folder.</summary>
    public static string DefaultRunner() =>
        Path.Combine(AppContext.BaseDirectory, "plugins", "team-connections", "website", "run.mjs");

    /// <summary>The longest a website run may take: a sign-in typed at a person's pace, then a page.</summary>
    public static readonly TimeSpan Bound = TimeSpan.FromMinutes(3);

    /// <summary>The most of the runner's answer read: a screenshot of a 1920x1080 page and its report.</summary>
    private const int MaximumAnswer = 12 * 1024 * 1024;

    public const string NoNodeText = "This worker has no Node.js to run the website's browser with.";
    public const string NoRunnerText = "This worker has no website runner (plugins/team-connections/website) beside the Host.";
    public const string InUseText = "This connection's browser is open for a sign-in or another run right now. Try again once it has closed.";

    private readonly Lazy<string> _profileRoot = new(() => profileRoot ?? SignInProfiles.Root(WorkerSignInBrowsers.DefaultProfileRoot()));

    /// <summary>The run's answer, as a test's answer is. Never throws for what a site does.</summary>
    public async Task<ConnectionTested> RunAsync(string request, WebsiteProbe probe, CancellationToken ct = default)
    {
        var redactor = Redactor(probe);
        ConnectionTested Answer(string outcome, string sentence, string? failure = null) =>
            new(request, outcome, redactor.Apply(sentence), Failure: failure);

        if (!SignInProfiles.IsKey(probe.Profile)) return Answer(ConnectionTested.Failed, "The website run reached the worker without its profile.");
        if ((node ?? SystemCommand.Find("node")) is not { } nodePath) return Answer(ConnectionTested.Failed, NoNodeText, WebsiteFailures.Unreachable);
        var script = runner ?? DefaultRunner();
        if (!File.Exists(script)) return Answer(ConnectionTested.Failed, NoRunnerText, WebsiteFailures.Unreachable);

        var root = _profileRoot.Value;
        SignInProfiles.MakePrivate(root);
        var folder = SignInProfiles.Folder(root, probe.Profile);
        SignInProfiles.MakePrivate(Path.GetDirectoryName(folder)!);
        SignInProfiles.MakePrivate(folder);

        FileStream held;
        try
        {
            held = SignInProfiles.Lock(folder);
        }
        catch (IOException)
        {
            return Answer(ConnectionTested.Failed, InUseText, WebsiteFailures.Unreachable);
        }

        await using (held)
        {
            // The same preparation as a sign-in's: the last exit clean, a lock left by a browser on
            // another worker gone. The languages are set again by the runner from the identity.
            SignInProfiles.MakePrivate(Path.Combine(folder, "chrome"));

            var input = new JsonObject
            {
                ["command"] = probe.Command,
                ["connection"] = new JsonObject
                {
                    ["name"] = probe.Name,
                    ["fields"] = Object(probe.Fields),
                    ["secrets"] = Object(probe.Secrets),
                },
                ["profile"] = folder,
                ["chromeRoot"] = chromeRoot ?? ChromiumSignInBrowser.Root(),
                ["path"] = probe.Path,
                ["expect"] = probe.Expect,
                ["minutes"] = probe.Minutes,
                ["steps"] = probe.Steps is { } steps
                    ? new JsonArray([.. steps.Select(s => (JsonNode)new JsonObject { ["action"] = s.Action, ["selector"] = s.Selector, ["value"] = s.Value })])
                    : null,
                ["extraArgs"] = extraArgs is { Count: > 0 } extra ? new JsonArray([.. extra.Select(a => (JsonNode)JsonValue.Create(a)!)]) : null,
            };

            var (answer, why) = await StartAsync(nodePath, script, folder, input.ToJsonString(), ct);
            if (answer is null) return Answer(ConnectionTested.Failed, why ?? "The website run gave no answer.", WebsiteFailures.Unreachable);

            return Read(request, answer.Value, redactor);
        }
    }

    /// <summary>The runner's answer as a test's: its status as the outcome, and its words redacted.</summary>
    internal static ConnectionTested Read(string request, JsonElement answer, ValueRedactor redactor)
    {
        string? Text(string name) => answer.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool Flag(string name) => answer.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        var status = Text("status") ?? "Failed";
        var reason = redactor.Apply(Text("reason") ?? "The website run gave no reason.");
        var report = Text("report") is { } words ? redactor.Apply(words) : null;
        byte[]? screenshot = null;
        try
        {
            if (Text("screenshot") is { Length: > 0 } png) screenshot = Convert.FromBase64String(png);
        }
        catch (FormatException)
        {
        }

        ConnectionTestSecret? handoff = answer.TryGetProperty("handoff", out var given) && given.ValueKind == JsonValueKind.Object
            ? new ConnectionTestSecret(given.GetRawText())
            : null;

        var (outcome, failure) = status switch
        {
            _ when Flag("refusal") => (ConnectionTested.Ok, WebsiteFailures.Refusal),
            "Verified" => (ConnectionTested.Ok, (string?)null),
            _ when Flag("captcha") => (ConnectionTested.Refused, WebsiteFailures.Captcha),
            _ when Flag("badPassword") => (ConnectionTested.Refused, WebsiteFailures.SignInRefused),
            _ when Flag("offHost") => (ConnectionTested.Refused, WebsiteFailures.OffHost),
            "Expired" => (ConnectionTested.Refused, WebsiteFailures.Expired),
            "Needs you" => (ConnectionTested.Refused, WebsiteFailures.NeedsYou),
            _ => (ConnectionTested.Failed, WebsiteFailures.Unreachable),
        };

        return new ConnectionTested(request, outcome, reason, Failure: failure, Report: report, Screenshot: screenshot,
            Handoff: outcome == ConnectionTested.Ok && failure is null ? handoff : null);
    }

    /// <summary>
    /// Every value of the connection no answer may carry: its masked fields, its username, and each part
    /// of its saved session (<see cref="SignInSessionValue.SecretParts"/>, the short ones as whole tokens).
    /// </summary>
    public static ValueRedactor Redactor(WebsiteProbe probe)
    {
        var values = new List<string?>(probe.Secrets.Values);
        var tokens = new List<string?>();
        if (probe.Fields.TryGetValue("username", out var username)) values.Add(username);
        if (probe.Secrets.TryGetValue("session", out var session))
        {
            var parts = SignInSessionValue.SecretParts(session);
            values.AddRange(parts.Anywhere);
            tokens.AddRange(parts.WholeTokens);
        }

        return ValueRedactor.For(values.Where(v => v is { Length: >= ValueRedactor.MinimumLength }), tokens);
    }

    private static JsonObject Object(IReadOnlyDictionary<string, string> values)
    {
        var node = new JsonObject();
        foreach (var (name, value) in values) node[name] = value;
        return node;
    }

    /// <summary>The runner, started with a minimal environment of its own, and its one line of answer.</summary>
    private async Task<(JsonElement? Answer, string? Why)> StartAsync(string nodePath, string script, string folder, string stdin, CancellationToken ct)
    {
        var start = new ProcessStartInfo(nodePath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.Combine(folder, "home"),
        };
        Directory.CreateDirectory(start.WorkingDirectory);
        start.ArgumentList.Add(script);

        // THE WORKER'S ENVIRONMENT - its key among it - IS NOT THE RUNNER'S.
        start.Environment.Clear();
        start.Environment["PATH"] = "/usr/local/bin:/usr/bin:/bin";
        start.Environment["HOME"] = start.WorkingDirectory;
        start.Environment["LANG"] = "en_US.UTF-8";
        foreach (var (name, value) in environment ?? new Dictionary<string, string>()) start.Environment[name] = value;

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Node did not start.");
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bound.CancelAfter(Bound);

        try
        {
            await process.StandardInput.WriteAsync(stdin.AsMemory(), bound.Token);
            process.StandardInput.Close();

            // Stderr is drained and dropped: Playwright's and Chrome's words may hold what a page showed.
            var errors = process.StandardError.ReadToEndAsync(bound.Token);
            var output = new StringBuilder();
            var buffer = new char[64 * 1024];
            while (await process.StandardOutput.ReadAsync(buffer.AsMemory(), bound.Token) is var n and > 0)
            {
                if (output.Length + n > MaximumAnswer) return (null, "The website run's answer was too large.");
                output.Append(buffer, 0, n);
            }

            await process.WaitForExitAsync(bound.Token);
            await errors;

            var line = output.ToString().Trim().Split('\n').LastOrDefault() ?? "";
            try
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.ValueKind == JsonValueKind.Object ? (document.RootElement.Clone(), null) : (null, "The website run's answer was not one.");
            }
            catch (JsonException)
            {
                return (null, $"The website run ended (exit {process.ExitCode}) without an answer.");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, $"The website run took longer than {Bound.TotalMinutes:0} minutes and was stopped.");
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    // This run's own process and the browser it started: nothing else.
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
    }
}
