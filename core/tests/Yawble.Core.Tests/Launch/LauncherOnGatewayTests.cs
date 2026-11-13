using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Credentials;
using Yawble.Core.Gateway;
using Yawble.Core.Launch;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Gateway;
using Yawble.Core.Transcripts;
using Xunit;

namespace Yawble.Core.Tests.Launch;

/// <summary>
/// The launcher on the real gateway pieces: the per-run gateway and its log, run tokens, the MCP bus server, the
/// credential service (0600 file, fake secrets only) with the host Grok home, the transcripts, and the real
/// yawble-bridge and yawble-grok-auth processes. Only the sandbox is scripted: the "sandbox" runs the bridge and makes the
/// model calls a CLI would, through the run's gateway, with only what the compiled request gives it. No real sandbox.
/// </summary>
public class LauncherOnGatewayTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Kit : IAsyncDisposable
    {
        public required GatewayFixture Gateway { get; init; }
        public required LaunchKit Launch { get; init; }
        public required TranscriptStore Transcripts { get; init; }
        public List<IRunGateway> Started { get; } = [];

        // These tests are not about the watchdogs. Run time is 60x real here, so the policy floors (60 s idle,
        // 120 s wall) would be 1 s and 2 s of real time: less than a Windows runner takes to start the real
        // yawble-bridge process and make the first proxied call. One and two hours of run time is 1 and 2 real minutes.
        public static async Task<Kit> StartAsync(Action<GatewayFixture> credentials, int idleSec = 3600, int wallSec = 7200, bool realLoopback = false)
        {
            var f = await GatewayFixture.StartAsync(realLoopback: realLoopback);
            credentials(f);
            var transcripts = new TranscriptStore(Path.Combine(f.Db.Dir, "data"), f.Db.ConnectionString);
            var factory = new RecordingFactory(new GatewayRunFactory(f.Host));
            var launch = await LaunchKit.StartAsync(idleSec: idleSec, wallSec: wallSec,
                credentials: new ServiceCredentialModes(f.Credentials, f.GrokHome), db: f.Db, gateways: factory,
                transcripts: new StoreTranscripts(transcripts));
            var kit = new Kit { Gateway = f, Launch = launch, Transcripts = transcripts };
            factory.Started = kit.Started;
            return kit;
        }

        public async ValueTask DisposeAsync()
        {
            await Launch.DisposeAsync();
            await Gateway.DisposeAsync();
        }
    }

    /// <summary>Keeps each real gateway so a test can read its token after the run.</summary>
    private sealed class RecordingFactory(IRunGatewayFactory inner) : IRunGatewayFactory
    {
        public List<IRunGateway> Started { get; set; } = [];

        public async Task<IRunGateway> StartAsync(RunIdentity run, RunActivity activity, CancellationToken ct)
        {
            var g = await inner.StartAsync(run, activity, ct);
            lock (Started) Started.Add(g);
            return g;
        }
    }

    private static JsonElement Run(Message m) => JsonDocument.Parse(m.Payload).RootElement.GetProperty("run");

    private static Uri Proxy(JsonObject request) => new(request["runtimeConfig"]!["networkProxy"]!.GetValue<string>());

    /// <summary>The CLI's HTTP client: the compiled request's proxy (the run's gateway; in memory unless real loopback).</summary>
    private static HttpClient Through(Uri proxy) => ProxyClient.ForUrl(proxy);

    /// <summary>Runs yawble-grok-auth with only YAWBLE_RUN_TOKEN, as grok does through GROK_AUTH_PROVIDER_COMMAND.</summary>
    private static async Task<string> GrokAuthAsync(string runToken)
    {
        var psi = Binaries.GrokAuth();
        psi.Environment["YAWBLE_RUN_TOKEN"] = runToken;
        using var p = Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.Equal(0, p.ExitCode);
        return stdout.Trim();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AClaudeMemberRunsThroughTheRealGateway_BridgeRecordsProgressAndHandback_TokenIsRevokedAfter(bool realLoopback)
    {
        if (realLoopback) GatewayFixture.SkipUnlessRealLoopback("the gateway on a real port, reached by the real yawble-bridge process");
        await using var kit = await Kit.StartAsync(f => f.Credentials.Set(CredentialSlot.ClaudeSetupToken, FakeSecrets.SetupToken), realLoopback: realLoopback);
        string? modelAnswer = null, placeholder = null;
        var bridgeTexts = new List<string>();
        kit.Launch.Backend.Stdout = "claude says hello\n";
        kit.Launch.Backend.Script = async (req, p) =>
        {
            try
            {
                var env = LaunchKit.Env(req);
                if (!env["CLAUDE_CONFIG_DIR"].Contains("dev1")) { p.Exit(0); return; }
                placeholder = env["CLAUDE_CODE_OAUTH_TOKEN"];
                Assert.Equal(placeholder, env["YAWBLE_RUN_TOKEN"]);
                // The CLI's model call: the placeholder as Bearer, to model.yawble.internal through the proxy.
                using var http = Through(Proxy(req));
                using var msg = new HttpRequestMessage(HttpMethod.Get, "http://model.yawble.internal/v1/models");
                msg.Headers.Authorization = new("Bearer", placeholder);
                using var resp = await http.SendAsync(msg);
                modelAnswer = $"{(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}";
                // The CLI's MCP server: the real yawble-bridge, with only the proxy and the run token.
                await using (var bridge = BridgeProcess.Start(Proxy(req), env["YAWBLE_RUN_TOKEN"]))
                {
                    await bridge.InitializeAsync();
                    bridgeTexts.Add((await bridge.CallAsync("progress", new JsonObject { ["status"] = "writing hello.txt" })).Text);
                    bridgeTexts.Add((await bridge.CallAsync("handback", new JsonObject { ["delivered"] = "hello.txt written" })).Text);
                }
                p.Exit(0);
            }
            catch (Exception ex)
            {
                bridgeTexts.Add("SCRIPT FAILED: " + ex);
                p.Exit(99);
            }
        };

        await kit.Launch.Bus.PersonTellAsync("alpha", "dev1", "write hello.txt", ct: Ct);
        var outcome = await kit.Launch.OutcomeAsync("dev1");

        Assert.True(MessageTypes.Completed == outcome.Type, string.Join(" | ", bridgeTexts) + " / " + outcome.Payload);
        Assert.StartsWith("200 ", modelAnswer);
        Assert.Contains("claude-subscription", modelAnswer); // the upstream saw the fake setup-token, swapped in
        Assert.StartsWith(RunTokenRegistry.Prefix, placeholder);
        // The upstream never saw the run token; the sandbox never saw the secret.
        Assert.All(kit.Gateway.Upstream.Requests, r => Assert.DoesNotContain(placeholder!, r.Authorization ?? ""));
        // The handback wakes the team's Manager, whose run is a second sandbox; this test looks at dev1's.
        var request = kit.Launch.Backend.Requests.Single(r => LaunchKit.IsFor(r.Json, "dev1")).Json;
        Assert.All(FakeSecrets.All, s => Assert.DoesNotContain(s, request));

        // progress and handback landed on the bus as alpha/dev1, caused by the person's tell.
        var messages = kit.Launch.Messages();
        var tell = messages.Single(m => m.Type == MessageTypes.InstructionPrefix + "alpha/dev1");
        var progress = messages.Single(m => m.Type == MessageTypes.Progress && m.Source == "alpha/dev1");
        var handback = messages.Single(m => m.Type == MessageTypes.Handback && m.Source == "alpha/dev1");
        Assert.Equal(tell.Seq, progress.CausationSeq);
        Assert.Equal(tell.Seq, handback.CausationSeq);

        // The gateway log has this run's forward and bus calls under its bus run id; the token is revoked now.
        var runId = JsonDocument.Parse(outcome.Payload).RootElement.GetProperty("runId").GetInt64();
        var events = kit.Gateway.Log.Read(runId);
        Assert.Contains(events, e => e.Kind == GatewayEventKinds.Forward && e.Vhost == Vhosts.Model && e.Status == 200);
        Assert.Equal(TokenCheck.Revoked, kit.Gateway.Tokens.Check(runId, placeholder));

        // The transcript holds the CLI's output, under the bus run id.
        Assert.Equal("claude says hello\n", Encoding.UTF8.GetString(kit.Transcripts.Read(runId)!));
        Assert.NotNull(kit.Transcripts.Find(runId)!.EndedAt);
    }

    [Fact]
    public async Task AGrokLoginRunGetsThePlaceholderFromSandboxGrokAuth_AndItsAuthJsonIsAcceptedAfterTheRun()
    {
        await using var kit = await Kit.StartAsync(f =>
            FakeSecrets.WriteGrokAuth(f.GrokHome, FakeSecrets.GrokSession1, f.Clock.GetUtcNow() + TimeSpan.FromHours(6)));
        string? answer = null;
        var layout = kit.Launch.Layout("dev2", AgentCli.Grok);
        kit.Launch.Backend.Script = async (req, p) =>
        {
            var env = LaunchKit.Env(req);
            Assert.False(env.ContainsKey("XAI_API_KEY"));
            var token = await GrokAuthAsync(env["YAWBLE_RUN_TOKEN"]);
            // grok 1.0.70 in login mode writes the provider's token as an external-mode auth.json.
            await File.WriteAllTextAsync(Path.Combine(AgentPresets.GrokHome(layout), "auth.json"),
                $$"""{"key":"{{token}}","auth_mode":"external"}""");
            using var http = Through(Proxy(req));
            using var msg = new HttpRequestMessage(HttpMethod.Get, "http://grok.yawble.internal/v1/models");
            msg.Headers.Authorization = new("Bearer", token);
            using var resp = await http.SendAsync(msg);
            answer = $"{(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}";
            p.Exit(0);
        };

        await kit.Launch.Bus.PersonTellAsync("alpha", "dev2", "anything", ct: Ct);
        var outcome = await kit.Launch.OutcomeAsync("dev2");

        Assert.Equal(MessageTypes.Completed, outcome.Type);
        Assert.StartsWith("200 ", answer);
        Assert.Contains("grok-session-1", answer);
        Assert.Equal(0, Run(outcome).GetProperty("guardrails").GetArrayLength());
    }

    [Fact]
    public async Task AGrokRunThatExitsZeroWhenTheKeeperCannotRefresh_FailsWithTheGatewaysReason()
    {
        // The host Grok home's session has expired and no keeper can refresh it: the gateway answers 401
        // "Grok login expired: sign in again". grok -p exits 0 anyway, so the verdict comes from the log.
        await using var kit = await Kit.StartAsync(f =>
            FakeSecrets.WriteGrokAuth(f.GrokHome, FakeSecrets.GrokSession1, f.Clock.GetUtcNow() - TimeSpan.FromMinutes(1)));
        int? status = null;
        kit.Launch.Backend.Script = async (req, p) =>
        {
            var token = await GrokAuthAsync(LaunchKit.Env(req)["YAWBLE_RUN_TOKEN"]);
            using var http = Through(Proxy(req));
            using var msg = new HttpRequestMessage(HttpMethod.Post, "http://grok.yawble.internal/v1/chat/completions") { Content = new StringContent("{}") };
            msg.Headers.Authorization = new("Bearer", token);
            using var resp = await http.SendAsync(msg);
            status = (int)resp.StatusCode;
            p.Exit(0);
        };

        await kit.Launch.Bus.PersonTellAsync("alpha", "dev2", "anything", ct: Ct);
        var outcome = await kit.Launch.OutcomeAsync("dev2");

        Assert.Equal(401, status);
        Assert.Equal(MessageTypes.Failed, outcome.Type);
        Assert.Equal(FailureClasses.Credential, Payload.Field(outcome, "failureClass"));
        Assert.Equal("Grok is not signed in on this computer, or its sign-in could not be renewed: run 'grok' in your own terminal and sign in", Payload.Field(outcome, "reason"));
        Assert.Equal(0, Run(outcome).GetProperty("exitCode").GetInt32());
        Assert.Empty(kit.Gateway.Upstream.Requests); // nothing reached xAI
    }

    [Fact]
    public async Task ACredentialTheUpstreamRefuses_IsTheCardReason()
    {
        await using var kit = await Kit.StartAsync(f => f.Credentials.Set(CredentialSlot.ClaudeSetupToken, FakeSecrets.SetupToken));
        kit.Gateway.Upstream.RefuseAll = true; // revoked at Anthropic
        kit.Launch.Backend.Script = async (req, p) =>
        {
            using var http = Through(Proxy(req));
            using var msg = new HttpRequestMessage(HttpMethod.Get, "http://model.yawble.internal/v1/models");
            msg.Headers.Authorization = new("Bearer", LaunchKit.Env(req)["CLAUDE_CODE_OAUTH_TOKEN"]);
            using var _ = await http.SendAsync(msg);
            p.Exit(1);
        };
        await kit.Launch.Bus.PersonTellAsync("alpha", "dev1", "anything", ct: Ct);
        var outcome = await kit.Launch.OutcomeAsync("dev1");
        Assert.Equal(FailureClasses.Credential, Payload.Field(outcome, "failureClass"));
        Assert.Equal("Claude credential expired or revoked: reconnect", Payload.Field(outcome, "reason"));
    }

    [Fact]
    public async Task NoCredentialInTheService_FailsAtOnce_AndAForbiddenConnectIsInTheLog()
    {
        await using var kit = await Kit.StartAsync(_ => { });
        await kit.Launch.Bus.PersonTellAsync("alpha", "dev1", "anything", ct: Ct);
        var outcome = await kit.Launch.OutcomeAsync("dev1");
        Assert.Equal(FailureClasses.StartFailed, Payload.Field(outcome, "failureClass"));
        Assert.Contains("no model credential is set up for claude", Payload.Field(outcome, "reason"));
        Assert.Empty(kit.Launch.Backend.Requests);
    }

    [Fact]
    public async Task ARunsGatewayRefusesAConnectToTheVendorAndCountsItInTheVerdict()
    {
        await using var kit = await Kit.StartAsync(f => f.Credentials.Set(CredentialSlot.ClaudeSetupToken, FakeSecrets.SetupToken));
        string? statusLine = null;
        kit.Launch.Backend.Script = async (req, p) =>
        {
            var proxy = Proxy(req);
            await using var s = await ProxyClient.ConnectAsync(proxy.Port);
            await s.WriteAsync(Encoding.ASCII.GetBytes("CONNECT api.anthropic.com:443 HTTP/1.1\r\nHost: api.anthropic.com:443\r\n\r\n"));
            var buf = new byte[256];
            var n = await s.ReadAsync(buf);
            statusLine = Encoding.ASCII.GetString(buf, 0, n).Split("\r\n")[0];
            p.Exit(0);
        };
        await kit.Launch.Bus.PersonTellAsync("alpha", "dev1", "anything", ct: Ct);
        var outcome = await kit.Launch.OutcomeAsync("dev1");
        Assert.Contains("403", statusLine);
        Assert.Equal(MessageTypes.Completed, outcome.Type);
        var runId = JsonDocument.Parse(outcome.Payload).RootElement.GetProperty("runId").GetInt64();
        var verdict = GatewayVerdicts.From(kit.Gateway.Log.Read(runId));
        Assert.Equal(1, verdict.DeniedConnects);
        Assert.False(verdict.CredentialRejected);
    }
}
