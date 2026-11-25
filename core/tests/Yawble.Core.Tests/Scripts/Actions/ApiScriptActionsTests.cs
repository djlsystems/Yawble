using System.Text.Json.Nodes;
using Xunit;
using Yawble.Core.Integrations;
using Yawble.Core.Scripts.Actions;
using Yawble.Core.Scripts.Engine;
using Yawble.Core.Scripts.Language;
using Yawble.Core.Scripts.Running;
using IntegrationCaller = Yawble.Core.Integrations.ScriptCaller;
using RunCaller = Yawble.Core.Scripts.Running.ScriptCaller;

namespace Yawble.Core.Tests.Scripts.Actions;

/// <summary>The "api" kind behind the script seam, against a fake of the integrations (no HTTP, no database).</summary>
public class ApiScriptActionsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ScriptOperation Op(string action, string method) => new(action, method, "/x", action, "allow", [], null);

    /// <summary>Teams and their operations; every call recorded and answered by <see cref="Answer"/> (or refused by <see cref="Refuse"/>).</summary>
    private sealed class FakeApi : IScriptApi
    {
        public Dictionary<string, List<ScriptOperation>> TeamOps { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Alpha"] = [Op("CreateMessage", "POST"), Op("ListMessage", "GET")],
            ["Beta"] = [Op("GetWeather", "get")],
        };
        public List<(IntegrationCaller Caller, string Integration, string Action, JsonObject Args)> Calls { get; } = [];
        public ScriptApiResult Answer { get; set; } = new(201, "application/json", """{"sid":"SM1","status":"queued"}""");
        public string? Refuse { get; set; }
        public int OperationReads { get; private set; }

        public Task<ScriptApiResult> CallAsync(IntegrationCaller caller, string integration, string action, JsonObject args, CancellationToken ct)
        {
            Calls.Add((caller, integration, action, args));
            if (Refuse is not null) throw new ScriptApiRefusedException(Refuse);
            if (caller.DryRun && action != "ListMessage")
                return Task.FromResult(new ScriptApiResult(0, "text/plain", "would POST /2010-04-01/Accounts/AC_TEST_ACCOUNT/Messages.json") { Sent = false });
            return Task.FromResult(Answer);
        }

        public IReadOnlyList<string> Teams() => TeamOps.Keys.ToList();

        public IReadOnlyList<ScriptOperation> Operations(string team)
        {
            OperationReads++;
            return TeamOps.TryGetValue(team, out var ops) ? ops : [];
        }
    }

    private static RunCaller Caller(bool dryRun = false, string? member = "Texter", string team = "Alpha") =>
        new(1, team, member is null ? null : 7, member, 42, new Dictionary<string, string> { ["sms"] = "twilio" }, dryRun);

    private static ScriptActionCall Call(string action = "CreateMessage", string use = "sms", bool dryRun = false) =>
        new(use, "api", action, new JsonObject { ["To"] = "+15550000001", ["Body"] = "hi" }, dryRun);

    // ---- the catalog -------------------------------------------------------------------------------------------------------

    [Fact]
    public void GlobalCatalog_HasEveryTeamsOperations_OfKindApiOnly()
    {
        var catalog = new ApiScriptActionsProvider(new FakeApi()).Catalog;
        Assert.True(catalog.HasAction("api", "CreateMessage"));
        Assert.True(catalog.HasAction("api", "GetWeather"));
        Assert.False(catalog.HasAction("api", "DeleteMessage"));
        Assert.False(catalog.HasAction("email", "CreateMessage"));
        Assert.False(catalog.HasAction("api", "createmessage"));
    }

    [Fact]
    public void Catalog_IsChanging_UnlessEveryOperationByThatNameIsAGet()
    {
        var catalog = new ApiScriptActionsProvider(new FakeApi()).Catalog;
        Assert.True(catalog.IsChanging("api", "CreateMessage"));
        Assert.False(catalog.IsChanging("api", "ListMessage"));
        Assert.False(catalog.IsChanging("api", "GetWeather")); // "get" in any case
        Assert.True(catalog.IsChanging("api", "Unknown")); // cannot tell: treated as changing
        Assert.True(new ApiScriptActionCatalog([Op("Same", "GET"), Op("Same", "PUT")]).IsChanging("api", "Same"));
    }

    [Fact]
    public void RunCatalog_IsTheCallersTeamsOperations_ReadOnce()
    {
        var api = new FakeApi();
        var actions = new ApiScriptActionsProvider(api).For(Caller(team: "Beta"));
        var catalog = Assert.IsAssignableFrom<IScriptActionCatalog>(actions);
        Assert.True(catalog.HasAction("api", "GetWeather"));
        Assert.False(catalog.HasAction("api", "CreateMessage"));
        Assert.False(catalog.IsChanging("api", "GetWeather"));
        Assert.Equal(1, api.OperationReads);
    }

    // ---- results -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Invoke_MapsTheCallToTheBoundIntegration_WithTheScriptCaller()
    {
        var api = new FakeApi();
        var result = await new ApiScriptActionsProvider(api).For(Caller()).InvokeAsync(Call(), Ct);

        var call = Assert.Single(api.Calls);
        Assert.Equal("twilio", call.Integration);
        Assert.Equal("CreateMessage", call.Action);
        Assert.Equal(new IntegrationCaller("Alpha", "Texter", "42", false), call.Caller);
        Assert.Equal("+15550000001", (string?)call.Args["To"]);
        Assert.Equal(201, (int?)result!["status"]);
        Assert.Equal("SM1", (string?)result["body"]!["sid"]);
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/problem+json")]
    [InlineData("APPLICATION/JSON; charset=utf-8")]
    public async Task JsonBody_IsParsed(string contentType)
    {
        var api = new FakeApi { Answer = new(200, contentType, """{"items":[1,2],"ok":true}""") };
        var result = await new ApiScriptActionsProvider(api).For(Caller()).InvokeAsync(Call(), Ct);
        Assert.IsType<JsonObject>(result!["body"]);
        Assert.Equal(2, result["body"]!["items"]!.AsArray().Count);
    }

    [Theory]
    [InlineData("text/plain", "queued")]
    [InlineData("text/html", "<p>{\"not\":\"json\"}</p>")]
    [InlineData("application/json", "{\"cut\":")] // said JSON but does not parse (e.g. cut at 64 KB): the text
    [InlineData("", "")]
    public async Task OtherBody_IsTheText(string contentType, string body)
    {
        var api = new FakeApi { Answer = new(200, contentType, body) };
        var result = await new ApiScriptActionsProvider(api).For(Caller()).InvokeAsync(Call(), Ct);
        Assert.Equal(200, (int?)result!["status"]);
        Assert.Equal(body, result["body"]!.GetValue<string>());
    }

    // ---- failures ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task NeedsApproval_FailsTheStep_WithTheApprovalText()
    {
        var api = new FakeApi { Refuse = "needs approval: allow UpdateMessage for Alpha" };
        var e = await Assert.ThrowsAsync<ScriptActionException>(() => new ApiScriptActionsProvider(api).For(Caller()).InvokeAsync(Call("UpdateMessage"), Ct));
        Assert.Equal("needs approval: allow UpdateMessage for Alpha", e.Message);
    }

    [Fact]
    public async Task Denied_FailsTheStep_WithTheDenyReason()
    {
        var api = new FakeApi { Refuse = "refused: twilio's DeleteMessage is not allowed for Alpha" };
        var e = await Assert.ThrowsAsync<ScriptActionException>(() => new ApiScriptActionsProvider(api).For(Caller()).InvokeAsync(Call("DeleteMessage"), Ct));
        Assert.Equal("refused: twilio's DeleteMessage is not allowed for Alpha", e.Message);
    }

    [Fact]
    public async Task Non2xx_FailsTheStep_WithStatusAndBody()
    {
        var api = new FakeApi { Answer = new(400, "application/json", """{"code":21211,"message":"invalid To"}""") };
        var e = await Assert.ThrowsAsync<ScriptActionException>(() => new ApiScriptActionsProvider(api).For(Caller()).InvokeAsync(Call(), Ct));
        Assert.Equal("""twilio CreateMessage (POST /x): HTTP 400: {"code":21211,"message":"invalid To"}""", e.Message);
    }

    [Fact]
    public async Task Non2xx_LongBody_IsCut()
    {
        var api = new FakeApi { Answer = new(503, "text/plain", new string('x', 2000)) };
        var e = await Assert.ThrowsAsync<ScriptActionException>(() => new ApiScriptActionsProvider(api).For(Caller()).InvokeAsync(Call(), Ct));
        Assert.StartsWith("twilio CreateMessage (POST /x): HTTP 503: xxx", e.Message);
        Assert.True(e.Message.Length < 600, e.Message.Length.ToString());
    }

    [Fact]
    public async Task NothingAnswered_FailsTheStep()
    {
        var api = new FakeApi { Answer = new(0, "", "timed out after 30 s") };
        var e = await Assert.ThrowsAsync<ScriptActionException>(() => new ApiScriptActionsProvider(api).For(Caller()).InvokeAsync(Call(), Ct));
        Assert.Equal("twilio CreateMessage (POST /x): no answer: timed out after 30 s", e.Message);
    }

    [Fact]
    public async Task UnboundUse_FailsTheStep_WithoutACall()
    {
        var api = new FakeApi();
        var e = await Assert.ThrowsAsync<ScriptActionException>(() => new ApiScriptActionsProvider(api).For(Caller()).InvokeAsync(Call(use: "mail"), Ct));
        Assert.Equal("'mail' is not bound to one of Alpha's APIs", e.Message);
        Assert.Empty(api.Calls);
    }

    // ---- dry run -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DryRun_ReturnsTheWouldResult_NotAFailure()
    {
        var api = new FakeApi();
        var result = await new ApiScriptActionsProvider(api).For(Caller(dryRun: true)).InvokeAsync(Call(), Ct);
        Assert.True(Assert.Single(api.Calls).Caller.DryRun);
        Assert.Equal(0, (int?)result!["status"]);
        Assert.Equal("would POST /2010-04-01/Accounts/AC_TEST_ACCOUNT/Messages.json", (string?)result["body"]);
    }

    [Fact]
    public async Task EngineDryRunFlag_WithoutACatalog_IsADryRunCall()
    {
        var api = new FakeApi();
        await new ApiScriptActionsProvider(api).For(Caller(dryRun: false)).InvokeAsync(Call(dryRun: true), Ct);
        Assert.True(Assert.Single(api.Calls).Caller.DryRun);
    }

    [Fact]
    public async Task TestRun_HasNoMember_SoTheCallerIsTheTestRun()
    {
        var api = new FakeApi();
        await new ApiScriptActionsProvider(api).For(Caller(member: null)).InvokeAsync(Call(), Ct);
        Assert.Equal(ApiScriptActions.TestRunMember, Assert.Single(api.Calls).Caller.Member);
    }

    // ---- routing by kind ---------------------------------------------------------------------------------------------------

    private static ScriptActionKinds Kinds(FakeApi api) =>
        new(new Dictionary<string, IScriptActionsProvider> { [ApiScriptActionsProvider.Kind] = new ApiScriptActionsProvider(api) });

    [Fact]
    public void Kinds_Catalog_RoutesByKind_AndAnUnservedKindHasNoActions()
    {
        var catalog = Kinds(new FakeApi()).Catalog;
        Assert.True(catalog.HasAction("api", "CreateMessage"));
        Assert.False(catalog.IsChanging("api", "ListMessage"));
        Assert.False(catalog.HasAction("email", "list"));
        Assert.True(catalog.IsChanging("email", "list"));
    }

    [Fact]
    public async Task Kinds_RunActions_RouteByKind_AndAreTheTeamsCatalog()
    {
        var api = new FakeApi();
        var kinds = Kinds(api);
        var actions = kinds.For(Caller(team: "Beta"));
        var catalog = ScriptActionKinds.CatalogFor(actions, kinds);
        Assert.True(catalog.HasAction("api", "GetWeather"));
        Assert.False(catalog.HasAction("api", "CreateMessage")); // Alpha's, not Beta's

        await actions.InvokeAsync(Call(), Ct);
        Assert.Equal("Beta", Assert.Single(api.Calls).Caller.Team);

        var e = await Assert.ThrowsAsync<ScriptActionException>(() =>
            actions.InvokeAsync(new ScriptActionCall("mail", "email", "list", [], false), Ct));
        Assert.Equal("mail: no 'email' integration is available to scripts", e.Message);
    }

    [Fact]
    public void CatalogFor_FallsBackToTheProvidersCatalog()
    {
        var provider = NoScriptActions.Instance;
        Assert.Same(provider, ScriptActionKinds.CatalogFor(new FakeActions(), provider));
    }
}
