using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// A PLUGIN'S RUN SAYS WHICH CONNECTION FAILED IT, AND HOW (<see cref="RunConnectionFailures"/>), on the
/// real Host with a real plugin process. <c>refused</c> and <c>sign-in</c> mark the connection
/// needs-reconnect: every member using it, on every team, reads Needs you, Needs you lists the connection
/// once, and later runs wait; a passing Test connection (or an edit) clears all of it and tells each
/// team's Manager, once, in the workflow its run was held in. <c>unreachable</c>, a kind not named, and a
/// slot the run was not handed fail only the run.
/// </summary>
public sealed class RunConnectionFailureTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";
    private const string StripeKey = "sk_test_right_51Habc";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-run-conn-{Guid.NewGuid():N}");
    private readonly string _outside = Path.Combine(Path.GetTempPath(), $"harness-run-conn-out-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;
    private string _alpha = "";
    private string _beta = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    private string ResultFile => Path.Combine(_outside, "result.json");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(_outside);

        // A PLUGIN WITH ONE API KEY SLOT that prints whatever result record the test wrote for it.
        PluginInstall.Write(_dataRoot, "biller",
            script: $"cat > /dev/null; cat '{ResultFile}'",
            manifest: PluginInstall.Manifest("biller", edit: m => m["connections"] = JsonNode.Parse(
                """{"billing":{"description":"The billing account.","providers":["apikey"],"required":true}}""")));

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentRunner>(new FakeAgent());
                // Test connection's Stripe, faked: the right key reads the balance, any other is refused.
                services.AddSingleton(new ConnectionTester(new SocketMailLogin(), new FakeStripe()));
            }));

        var teams = Services.GetRequiredService<TeamRegistry>();
        _alpha = (await teams.CreateAsync("Alpha", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;
        _beta = (await teams.CreateAsync("Beta", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        _person = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await _person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        _person.Dispose();
        await _factory.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var directory in new[] { _dataRoot, _outside })
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task A_refused_key_marks_both_teams_members_and_one_needs_you_row_and_a_passing_test_clears_all_three_and_tells_both_managers()
    {
        // The key is good by the time anyone looks: Stripe refused it for a while (a revoked session, say).
        var stripe = await StripeAsync(StripeKey);
        await HireAsync(_alpha, "Biller", stripe);
        await HireAsync(_beta, "Clerk", stripe);

        // Alpha's member is told and its plugin reports the key refused.
        Result(false, "Stripe refused the key: it was revoked.", "billing", RunConnectionFailures.Refused);
        var refused = await TellAndAwaitAsync(_alpha, "Biller");

        // THE RUN IS STILL A FAILURE, not relabelled a wait, and it names the connection that held it.
        Assert.Equal(MessageTypes.Failed, refused.Type);
        Assert.NotEqual(FailureClasses.NeedsPerson, Field(refused, PayloadFields.FailureClass));
        Assert.Equal(stripe, Field(refused, PayloadFields.HeldByConnection));
        Assert.Contains("Stripe refused the key: it was revoked.", refused.Payload);

        // THE CONNECTION needs reconnecting, with the refusal's sentence.
        var connection = await Services.GetRequiredService<IConnectionStore>().GetAsync(stripe, Ct);
        Assert.Equal((ConnectionRecord.NeedsReconnect, "Stripe refused the key: it was revoked."), (connection!.Status, connection.StatusReason));

        // BOTH MEMBERS read Needs you - the one that never ran too - with the refusal's sentence.
        foreach (var (team, member) in new[] { (_alpha, "Biller"), (_beta, "Clerk") })
        {
            Assert.Contains("Stripe refused the key: it was revoked.", NeedsPerson(team, member));
        }

        // NEEDS YOU: ONE ROW for the connection, naming both teams and members; no failed row beside it.
        var rows = await NeedsYouAsync();
        var row = Assert.Single(rows);
        Assert.Equal(NeedsYouKinds.Connection, (string?)row["kind"]);
        Assert.Equal(stripe, (string?)row["connection"]!["id"]);
        Assert.Equal("connections", (string?)row["where"]!["view"]);
        Assert.Contains("Stripe refused the key: it was revoked.", (string?)row["words"]);
        Assert.Equal(
            [$"{_alpha}:Biller", $"{_beta}:Clerk"],
            row["connection"]!["holds"]!.AsArray()
                .SelectMany(h => h!["members"]!.AsArray().Select(m => $"{(string?)h["team"]!["id"]}:{(string?)m}"))
                .Order(StringComparer.Ordinal));

        // A LATER RUN of the other member waits, as needs-reconnect already made it, and is held by it too.
        var held = await TellAndAwaitAsync(_beta, "Clerk");
        Assert.Equal(MessageTypes.Failed, held.Type);
        Assert.Equal(FailureClasses.NeedsPerson, Field(held, PayloadFields.FailureClass));
        Assert.Equal(stripe, Field(held, PayloadFields.HeldByConnection));
        Assert.Single(await NeedsYouAsync());

        // Both Managers have run on their failures, and the workflows are still open for the notice.
        await ManagerRanAsync(_alpha, refused);
        await ManagerRanAsync(_beta, held);
        Assert.Equal(
            [refused.CorrelationId, held.CorrelationId],
            (await Services.GetRequiredService<IMessageLog>().OpenWorkflowsAmongAsync([refused.CorrelationId, held.CorrelationId], Ct)).Order());

        // The person tests the connection: it passes.
        var tested = await _person.PostAsync($"/api/connections/{stripe}/test", null, Ct);
        Assert.Equal(HttpStatusCode.OK, tested.StatusCode);
        Assert.Equal(ConnectionRecord.Ok, (string?)JsonNode.Parse(await tested.Content.ReadAsStringAsync(Ct))!["health"]);

        // ALL THREE CLEARED: both members, and the one row.
        foreach (var (team, member) in new[] { (_alpha, "Biller"), (_beta, "Clerk") })
        {
            Assert.Null(NeedsPerson(team, member));
            Assert.Null(Find(team, member).Snapshot().Failed);
        }
        Assert.Empty(await NeedsYouAsync());

        // EACH MANAGER TOLD ONCE, in the workflow its member's run was held in.
        var alphaNotice = Assert.Single(await NoticesAsync(_alpha));
        Assert.Equal(refused.CorrelationId, alphaNotice.CorrelationId);
        Assert.Contains("Biller is ready now", alphaNotice.Payload);
        var betaNotice = Assert.Single(await NoticesAsync(_beta));
        Assert.Equal(held.CorrelationId, betaNotice.CorrelationId);
        Assert.Contains("Clerk is ready now", betaNotice.Payload);

        // Another passing test says nothing more, and the members run.
        Assert.Equal(HttpStatusCode.OK, (await _person.PostAsync($"/api/connections/{stripe}/test", null, Ct)).StatusCode);
        Assert.Single(await NoticesAsync(_alpha));
        Assert.Single(await NoticesAsync(_beta));
        Result(true, null, null, null);
        Assert.Equal(MessageTypes.Completed, (await TellAndAwaitAsync(_alpha, "Biller")).Type);
        Assert.Equal(MessageTypes.Completed, (await TellAndAwaitAsync(_beta, "Clerk")).Type);
    }

    [Fact]
    public async Task An_unreachable_service_fails_the_run_and_leaves_the_connection_ok()
    {
        var stripe = await StripeAsync(StripeKey);
        await HireAsync(_alpha, "Biller", stripe);

        Result(false, "Stripe did not answer within 30 seconds.", "billing", RunConnectionFailures.Unreachable);
        var failed = await TellAndAwaitAsync(_alpha, "Biller");

        Assert.Equal(MessageTypes.Failed, failed.Type);
        Assert.Contains("Stripe did not answer within 30 seconds.", failed.Payload);
        Assert.Null(Field(failed, PayloadFields.HeldByConnection));
        await AssertNotMarkedAsync(stripe, "Biller");
    }

    [Fact]
    public async Task A_plugin_that_names_no_failure_kind_fails_the_run_and_does_not_mark_the_connection()
    {
        var stripe = await StripeAsync(StripeKey);
        await HireAsync(_alpha, "Biller", stripe);

        // No kind; a slot named alone; and a kind that is not one of the three.
        foreach (var (slot, kind) in new (string?, string?)[] { (null, null), ("billing", null), ("billing", "forbidden") })
        {
            Result(false, "Stripe refused the key.", slot, kind);
            var failed = await TellAndAwaitAsync(_alpha, "Biller");

            Assert.Equal(MessageTypes.Failed, failed.Type);
            Assert.Contains("Stripe refused the key.", failed.Payload);
            Assert.Null(Field(failed, PayloadFields.HeldByConnection));
            await AssertNotMarkedAsync(stripe, "Biller");
        }
    }

    [Fact]
    public async Task A_slot_the_run_was_not_handed_marks_nothing_and_the_run_still_fails_with_the_plugins_sentence()
    {
        var stripe = await StripeAsync(StripeKey);
        await HireAsync(_alpha, "Biller", stripe);

        Result(false, "The other account refused us.", "elsewhere", RunConnectionFailures.Refused);
        var failed = await TellAndAwaitAsync(_alpha, "Biller");

        Assert.Equal(MessageTypes.Failed, failed.Type);
        Assert.Contains("The other account refused us.", failed.Payload);
        await AssertNotMarkedAsync(stripe, "Biller");
    }

    [Fact]
    public async Task A_sign_in_failure_marks_needs_you_with_a_sentence_that_the_person_signs_in_themselves_and_an_edit_clears_it()
    {
        var stripe = await StripeAsync(StripeKey);
        await HireAsync(_alpha, "Biller", stripe);

        // THE PLUGIN'S SENTENCE DOES NOT SAY IT: the fixed sentence does, and the plugin's follows.
        Result(false, "The dashboard showed a challenge page.", "billing", RunConnectionFailures.SignIn);
        var failed = await TellAndAwaitAsync(_alpha, "Biller");
        Assert.Equal(stripe, Field(failed, PayloadFields.HeldByConnection));

        var connection = await Services.GetRequiredService<IConnectionStore>().GetAsync(stripe, Ct);
        Assert.Equal(ConnectionRecord.NeedsReconnect, connection!.Status);
        Assert.StartsWith(RunConnectionFailures.SignInSentence, connection.StatusReason);
        Assert.Contains("The dashboard showed a challenge page.", connection.StatusReason);
        Assert.Contains("Sign in to it yourself", NeedsPerson(_alpha, "Biller"));
        var row = Assert.Single(await NeedsYouAsync());
        Assert.Equal(NeedsYouKinds.Connection, (string?)row["kind"]);
        Assert.Contains("sign in to it themselves", (string?)row["words"]);

        // AN EDIT CLEARS IT, as a Reconnect or a passing test does.
        var edit = await _person.PutAsJsonAsync($"/api/connections/{stripe}/credentials", new { fields = new[] { new { name = "key", value = StripeKey } } }, Ct);
        Assert.True(edit.IsSuccessStatusCode, await edit.Content.ReadAsStringAsync(Ct));
        await AssertNotMarkedAsync(stripe, "Biller");

        // THE PLUGIN'S SENTENCE SAYS IT: it is kept as it is.
        const string Says = "Stripe wants a device approval: sign in to the dashboard yourself once.";
        Result(false, Says, "billing", RunConnectionFailures.SignIn);
        await TellAndAwaitAsync(_alpha, "Biller");
        Assert.Equal(Says, (await Services.GetRequiredService<IConnectionStore>().GetAsync(stripe, Ct))!.StatusReason);
    }

    [Fact]
    public void The_result_records_failure_fields_are_named_once_and_the_kind_is_never_read_from_the_sentence()
    {
        Assert.Equal("connection", RunConnectionFailures.SlotField);
        Assert.Equal("connectionFailure", RunConnectionFailures.KindField);
        Assert.Equal(["refused", "unreachable", "sign-in"], RunConnectionFailures.Kinds);
        Assert.Equal("heldByConnection", PayloadFields.HeldByConnection);
        Assert.Equal("connection", NeedsYouKinds.Connection);

        Assert.True(RunConnectionFailures.Holds(RunConnectionFailures.Refused));
        Assert.True(RunConnectionFailures.Holds(RunConnectionFailures.SignIn));
        Assert.False(RunConnectionFailures.Holds(RunConnectionFailures.Unreachable));
        Assert.False(RunConnectionFailures.Holds(null));
        Assert.False(RunConnectionFailures.Holds("CAPTCHA"));
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private async Task AssertNotMarkedAsync(string connectionId, string member)
    {
        var connection = await Services.GetRequiredService<IConnectionStore>().GetAsync(connectionId, Ct);
        Assert.Equal(ConnectionRecord.Ok, connection!.Status);
        Assert.Null(NeedsPerson(_alpha, member));
        Assert.DoesNotContain(await NeedsYouAsync(), r => (string?)r["kind"] == NeedsYouKinds.Connection);
    }

    private void Result(bool ok, string? error, string? slot, string? kind)
    {
        var record = new JsonObject { ["t"] = "result", ["ok"] = ok, ["output"] = ok ? "billed" : "" };
        if (error is not null) record["error"] = error;
        if (slot is not null) record[RunConnectionFailures.SlotField] = slot;
        if (kind is not null) record[RunConnectionFailures.KindField] = kind;
        File.WriteAllText(ResultFile, record.ToJsonString() + "\n");
    }

    private async Task<string> StripeAsync(string key)
    {
        var added = await _person.PostAsJsonAsync("/api/connections/credentials", new
        {
            kind = "apikey", preset = "stripe", name = "Stripe", fields = new[] { new { name = "key", value = key } },
        }, Ct);
        var body = await added.Content.ReadAsStringAsync(Ct);
        Assert.True(added.IsSuccessStatusCode, body);
        return (string)JsonNode.Parse(body)!["connection"]!["id"]!;
    }

    private async Task HireAsync(string team, string name, string connectionId)
    {
        var hired = await _person.PostAsJsonAsync($"/api/teams/{team}/containers", new
        {
            name,
            agent = "plugin:biller",
            connections = new Dictionary<string, string> { ["billing"] = connectionId },
        }, Ct);
        Assert.True(hired.StatusCode == HttpStatusCode.OK, await hired.Content.ReadAsStringAsync(Ct));
    }

    private Harness.Containers.MemberRuntime Find(string team, string member) =>
        Services.GetRequiredService<Harness.Containers.ContainerHost>().Find(new ContainerId(team, member))!;

    private string? NeedsPerson(string team, string member) => Find(team, member).Snapshot().NeedsPerson;

    private async Task<IReadOnlyList<JsonNode>> NeedsYouAsync() =>
        [.. (await _person.GetFromJsonAsync<JsonObject>("/api/needs-you?part=live", Ct))!["items"]!.AsArray().Select(i => i!)];

    private static string? Field(Message row, string field) =>
        JsonNode.Parse(row.Payload)![field] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private async Task<IReadOnlyList<Message>> NoticesAsync(string team)
    {
        var manager = MessageTypes.InstructionFor(new ContainerId(team, TeamRegistry.DefaultManagerName));
        var rows = await Services.GetRequiredService<IMessageLog>().ReadAfterAsync(0, [manager], int.MaxValue, Ct);
        return [.. rows.Where(m => m.Payload.Contains(PluginReadyNotice.ReadyField, StringComparison.Ordinal))];
    }

    /// <summary>Waits until the Manager the failure woke has run and is idle again.</summary>
    private async Task ManagerRanAsync(string team, Message failed)
    {
        var manager = new ContainerId(team, TeamRegistry.DefaultManagerName);
        var log = Services.GetRequiredService<IMessageLog>();
        var host = Services.GetRequiredService<Harness.Containers.ContainerHost>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var ended = (await log.ReadAfterAsync(failed.Seq, [MessageTypes.Completed, MessageTypes.Failed], int.MaxValue, Ct))
                .Any(m => m.Source == manager.ToString() && m.CorrelationId == failed.CorrelationId);
            if (ended && host.Find(manager)!.State == ContainerState.Idle) return;
            await Task.Delay(50, Ct);
        }

        Assert.Fail("The Manager was not woken by the failure, or its run did not end.");
    }

    private async Task<Message> TellAndAwaitAsync(string team, string member)
    {
        var who = new ContainerId(team, member);
        var log = Services.GetRequiredService<IMessageLog>();

        async Task<IReadOnlyList<Message>> Rows() =>
            [.. (await log.ReadAfterAsync(0, [MessageTypes.Completed, MessageTypes.Failed], int.MaxValue, Ct)).Where(m => m.Source == who.ToString())];

        var after = (await Rows()).Select(m => m.Seq).DefaultIfEmpty(0).Max();

        var told = await _person.PostAsJsonAsync($"/api/teams/{team}/containers/{member}/tell", new { instruction = "bill them" }, Ct);
        Assert.True(told.IsSuccessStatusCode, await told.Content.ReadAsStringAsync(Ct));

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if ((await Rows()).FirstOrDefault(m => m.Seq > after) is { } row)
            {
                var host = Services.GetRequiredService<Harness.Containers.ContainerHost>();
                while (host.Find(who)!.State != ContainerState.Idle && DateTime.UtcNow < deadline) await Task.Delay(20, Ct);
                return row;
            }

            await Task.Delay(50, Ct);
        }

        throw new TimeoutException($"{who} wrote no terminal row.");
    }

    /// <summary>Stripe for Test connection: the right key reads the balance; anything else is refused.</summary>
    private sealed class FakeStripe : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(request.RequestUri!.Host == "api.stripe.com" && request.Headers.Authorization?.ToString() == $"Bearer {StripeKey}"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"object":"balance"}""", Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"error":{"message":"Invalid API Key"}}""", Encoding.UTF8, "application/json") });
    }
}
