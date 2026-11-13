using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Gateway;
using Yawble.Core.Integrations;
using Yawble.Core.Ipc;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Integrations;

/// <summary>The universal credential floor, in front of every rule, for every CLI.</summary>
public class CredentialFloorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The seven refusals the person named: CLI, the command line, and the command path Discover would list.</summary>
    internal static readonly (string Cli, string Line, string Path)[] SevenList =
    [
        ("aws", "secretsmanager get-secret-value --secret-id x", "secretsmanager get-secret-value"),
        ("aws", "sts get-session-token", "sts get-session-token"),
        ("aws", "configure get aws_secret_access_key", "configure get"),
        ("gcloud", "auth print-access-token", "auth print-access-token"),
        ("kubectl", "get secret db -o yaml", "get secret"),
        ("az", "account get-access-token", "account get-access-token"),
        ("gh", "auth token", "auth token"),
    ];

    public static TheoryData<string, string, string> Seven()
    {
        var d = new TheoryData<string, string, string>();
        foreach (var (cli, line, path) in SevenList) d.Add(cli, line, path);
        return d;
    }

    private static string[] Args(string line) => line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static CliRule Allow(string pattern, string layer) => new(pattern, Decisions.Allow, layer);

    [Theory, MemberData(nameof(Seven))]
    public void TheSevenNamedCommandsAreRefused_AlsoWhenARuleAllowsThem(string cli, string line, string path)
    {
        var args = Args(line);
        var everythingAllowed = new CliEntry(cli, cli, "", ["--version"], [], [("*", Decisions.Allow)]);
        foreach (var entry in new CliEntry?[] { null, CliCatalog.Generic(cli), CliCatalog.Of(cli), everythingAllowed })
        {
            // no rule at all, a team allow, a global allow, the exact command allowed, and a prefix allowed
            foreach (var (team, global) in new (CliRule[], CliRule[])[]
            {
                ([], []), ([Allow("*", "team")], []), ([], [Allow("*", "global")]),
                ([Allow(line, "team")], [Allow(line + "*", "global")]), ([Allow(args[0] + "*", "team")], [Allow(string.Join(' ', args.Take(2)) + "*", "global")]),
            })
            {
                var (decision, rule) = CliRules.Decide(args, entry, team, global);
                Assert.Equal(Decisions.Deny, decision);
                Assert.Equal("floor", rule!.Layer);
            }
        }
        // the log says why: the word that hit
        Assert.StartsWith("floor-word:", CliRules.Decide(args, null, [], []).Rule!.Pattern);
        // Discover cannot offer it: classified credential, source floor
        var cls = CommandClassifier.Classify(cli, CliCatalog.Of(cli), path, false);
        Assert.Equal(CommandClass.Credential, cls.Class);
        Assert.Equal(ClassSource.Floor, cls.Source);
    }

    [Fact]
    public void ExtraNegatives_AreRefused()
    {
        foreach (var line in new[]
        {
            "ssm get-parameter --name x --with-decryption", "kms decrypt --key-id k", "kubectl get secrets", "kubectl -n prod get secret db",
            "iam create-access-key --user-name u", "ecr get-login-password", "config view --raw", "auth print-identity-token", "ad sp create-for-rbac",
        })
            Assert.Equal(Decisions.Deny, CliRules.Decide(Args(line), null, [Allow("*", "team")], [Allow("*", "global")]).Decision);
    }

    [Fact]
    public void WordsNotSubstrings_FlagValuesAreNotChecked_ShortStemsAreExact()
    {
        foreach (var line in new[] { "s3 ls", "get pods", "compute instances list", "vm list", "pr list", "status", "sasha show", "otpt list", "config view" })
            Assert.Null(CredentialFloor.Check(Args(line)));
        // a quoted free-text value is a value, not a command word; --flag=value cuts the value off
        Assert.Null(CredentialFloor.Check(["search", "issues", "token refresh"]));
        Assert.Null(CredentialFloor.Check(["log", "--author=tokenizer"]));
        // the flag NAME counts
        Assert.NotNull(CredentialFloor.Check(["run", "--max-tokens", "5"]));
        Assert.NotNull(CredentialFloor.Check(["x", "--with-decryption"]));
        Assert.Equal("sas", CredentialFloor.Check(["storage", "container", "generate-sas"]));
        Assert.Equal("secret", CredentialFloor.Check(["GET-SECRET-VALUE"]));
        // a rule that is not about credentials is not touched
        Assert.Equal(Decisions.Allow, CliRules.Decide(["s3", "ls"], null, [Allow("s3 ls*", "team")], []).Decision);
    }

    [Fact]
    public void PerCliFloorsStillApply_AndTheUniversalOneComesFirst()
    {
        var gh = CliCatalog.Of("gh")!;
        Assert.Equal("floor", CliRules.Decide(["pr", "checkout", "5"], gh, [Allow("*", "team")], []).Rule!.Layer);
        Assert.Equal("floor-word:token", CliRules.Decide(["auth", "token"], gh, [], []).Rule!.Pattern);
        Assert.Equal("auth login*", CliRules.Decide(["auth", "login"], gh, [], []).Rule!.Pattern); // per-CLI floor, no word hit
    }

    [Fact]
    public void EveryCredentialCommandTheGhAndAzPresetsRefuseIsRefusedByDiscover()
    {
        var gh = new[]
        {
            "auth token", "auth status --show-token", "auth login", "auth logout", "auth refresh", "auth setup-git", "auth switch", "secret list", "secret set X",
            "ssh-key add", "ssh-key list", "gpg-key add", "config set editor vim", "extension install x", "alias set a b",
        };
        var az = new[]
        {
            "account get-access-token", "storage account keys list", "servicebus namespace authorization-rule list-keys", "keyvault secret show", "keyvault secret download",
            "ad app credential list", "webapp show-connection-string", "webapp deployment list-publishing-credentials", "login", "logout", "account clear", "keyvault secret list",
        };
        foreach (var (cli, lines) in new[] { ("gh", gh), ("az", az) })
        {
            var entry = CliCatalog.Of(cli)!;
            foreach (var line in lines)
            {
                // the agent can never run it, whatever rules say
                Assert.Equal(Decisions.Deny, CliRules.Decide(Args(line), entry, [Allow("*", "team")], [Allow("*", "global")]).Decision);
                // and Discover classifies it locked (the path is the command words, as the walker records them)
                if (line.Contains("--")) continue; // a flag-only floor (auth status --show-token) is no node of the tree
                var path = string.Join(' ', Args(line).TakeWhile(a => !a.StartsWith('-')).Take(line.StartsWith("auth") || line.StartsWith("secret") || line.StartsWith("config") || line.StartsWith("extension") || line.StartsWith("alias") || line.StartsWith("ssh-key") || line.StartsWith("gpg-key") ? 2 : 99));
                var cls = CommandClassifier.Classify(cli, entry, path, false);
                Assert.True(cls.Class is CommandClass.Credential or CommandClass.Destructive, $"{cli} {line}: classified {cls.Class}");
                Assert.Equal(ClassSource.Floor, cls.Source);
            }
        }
        // the ones whose WORDS say it are refused even for a CLI the app has never heard of
        foreach (var line in new[] { "auth token", "secret list", "ssh-key add", "account get-access-token", "storage account keys list", "keyvault secret show", "ad app credential list",
                     "webapp show-connection-string", "webapp deployment list-publishing-credentials" })
            Assert.Equal("floor", CliRules.Decide(Args(line), null, [Allow("*", "team")], []).Rule!.Layer);
        // git has no preset of its own: its credential commands are caught by words
        foreach (var line in new[] { "credential fill", "credential-store get", "config credential.helper", "config --get-regexp ^credential" })
            Assert.Equal(Decisions.Deny, CliRules.Decide(Args(line), CliCatalog.Generic("git"), [Allow("*", "team")], []).Decision);
    }

    // ------------------------------------------------------------------ the agent's call and the person's manual rule

    private static async Task<string> Call(RunContext run, string tool, object args)
    {
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = System.Text.Json.JsonSerializer.SerializeToNode(args) },
        };
        var reply = await new McpBusServer(run).HandleAsync(Encoding.UTF8.GetBytes(body.ToJsonString()), Ct);
        return JsonNode.Parse(reply.Json!)!["result"]!["content"]![0]!["text"]!.GetValue<string>();
    }

    [Fact]
    public async Task AtEveryEntryPoint_TheAgentsCallIsRefusedEvenWithAnAllowRule_AndAManualAllowRuleIsRejected()
    {
        using var db = new TempDb();
        var texts = new List<string>();
        var done = false;
        var runner = new ScriptedRunner().On("Alpha/Ada", async (run, ct) =>
        {
            // git is the enabled CLI here; the floor does not care which CLI the words are for
            foreach (var (_, line, _) in SevenList)
                texts.Add(await Call(run, "git", new { args = Args(line), reason = "x" }));
            done = true;
            return RunResult.Ok;
        });
        await using var engine = new BusEngine(db.OpenStore(), runner, new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false });
        var integrations = new IntegrationService(db.ConnectionString, engine);
        engine.Integrations = integrations;
        var alpha = await engine.CreateTeamAsync("Alpha", ct: Ct);
        await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
        await integrations.DispatchAsync("integrations.enable", new JsonObject { ["name"] = "git", ["teamId"] = alpha, ["on"] = true }, Ct);
        // a rule that allows everything, for the team and for every team
        await integrations.DispatchAsync("integrations.saveRule", new JsonObject { ["name"] = "git", ["teamId"] = alpha, ["pattern"] = "*", ["decision"] = "allow" }, Ct);
        await integrations.DispatchAsync("integrations.saveRule", new JsonObject { ["name"] = "git", ["pattern"] = "*", ["decision"] = "allow" }, Ct);
        await engine.StartAsync(Ct);
        await engine.PersonTellAsync("Alpha", "Ada", "go", ct: Ct);
        await Wait.UntilAsync(() => done, "Ada's calls");
        Assert.Equal(7, texts.Count);
        Assert.All(texts, t => Assert.StartsWith("refused:", t));

        // manual rules: an allow rule that names a credential word is rejected with the reason
        foreach (var (_, line, _) in SevenList)
        {
            var ex = await Assert.ThrowsAsync<RpcException>(() => integrations.DispatchAsync("integrations.saveRule",
                new JsonObject { ["name"] = "git", ["teamId"] = alpha, ["pattern"] = line + "*", ["decision"] = "allow" }, Ct));
            Assert.Contains("never allowed", ex.Message);
        }
        // a deny rule is fine
        await integrations.DispatchAsync("integrations.saveRule", new JsonObject { ["name"] = "git", ["pattern"] = "auth token*", ["decision"] = "deny" }, Ct);
    }
}
