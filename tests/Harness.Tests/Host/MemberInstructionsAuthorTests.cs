using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// WHO WROTE A MEMBER'S OWN INSTRUCTIONS. The hire records the hiring principal - a Manager's
/// member id through the `member` tool, or a person through the route - and every PATCH that
/// changes the text records the person and appends `member.instructions-changed`. After hire only a
/// person edits them: the update route is HumansOnly, and a Manager's credential is refused there
/// the way the permit gate refuses a missing permit.
/// </summary>
public sealed class MemberInstructionsAuthorTests(HostFixture host) : IClassFixture<HostFixture>
{
    private const string Person = "person@example.test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_managers_hire_records_the_manager_as_who_set_the_instructions()
    {
        var before = DateTimeOffset.UtcNow;

        var result = await Tools(await ManagerKeyAsync()).Hire(
            "Scribe", team: host.Alpha, prompt: "Write the docs.", cancellationToken: Ct);
        Assert.StartsWith("HTTP 200", result);

        var member = await ReadAsync("Scribe");

        Assert.Equal("Write the docs.", member.GetProperty("systemPrompt").GetString());
        Assert.Equal("Manager", member.GetProperty("systemPromptSetBy").GetString());
        Assert.Equal("manager", member.GetProperty("systemPromptSetByKind").GetString());
        AssertUtcAtOrAfter(before, member.GetProperty("systemPromptSetAt").GetString());
    }

    [Fact]
    public async Task A_persons_hire_records_the_person_as_who_set_the_instructions()
    {
        var before = DateTimeOffset.UtcNow;
        using var person = await host.PersonAsync();

        var hired = await person.PostAsJsonAsync(
            $"/api/teams/{host.Alpha}/containers", new { name = "Reviewer", systemPrompt = "Review it." }, Ct);
        Assert.Equal(HttpStatusCode.OK, hired.StatusCode);

        var member = await ReadAsync("Reviewer");

        Assert.Equal(Person, member.GetProperty("systemPromptSetBy").GetString());
        Assert.Equal("person", member.GetProperty("systemPromptSetByKind").GetString());
        AssertUtcAtOrAfter(before, member.GetProperty("systemPromptSetAt").GetString());
    }

    [Fact]
    public async Task A_patch_that_changes_the_text_records_the_person_and_appends_a_tenant_event()
    {
        await Tools(await ManagerKeyAsync()).Hire(
            "Tester", team: host.Alpha, prompt: "Test it.", cancellationToken: Ct);
        using var person = await host.PersonAsync();
        var route = $"/api/teams/{host.Alpha}/containers/Tester";

        var before = DateTimeOffset.UtcNow;
        var changed = await person.PatchAsJsonAsync(route, new { systemPrompt = "Test it twice." }, Ct);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        var answered = await changed.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(Person, answered.GetProperty("systemPromptSetBy").GetString());

        var member = await ReadAsync("Tester");
        Assert.Equal("Test it twice.", member.GetProperty("systemPrompt").GetString());
        Assert.Equal(Person, member.GetProperty("systemPromptSetBy").GetString());
        Assert.Equal("person", member.GetProperty("systemPromptSetByKind").GetString());
        var setAt = member.GetProperty("systemPromptSetAt").GetString();
        AssertUtcAtOrAfter(before, setAt);

        var row = Assert.Single(await InstructionEventsAsync("Tester"));
        Assert.Equal(Person, row.ActorEmail);
        using (var detail = JsonDocument.Parse(row.Detail!))
        {
            Assert.Equal(Person, detail.RootElement.GetProperty("setBy").GetString());
            Assert.Equal("person", detail.RootElement.GetProperty("setByKind").GetString());
            Assert.False(detail.RootElement.GetProperty("cleared").GetBoolean());
            Assert.DoesNotContain("Test it twice.", row.Detail!, StringComparison.Ordinal);
        }

        // The same words again, and a label-only edit, are not changes: nothing moves.
        await person.PatchAsJsonAsync(route, new { systemPrompt = "Test it twice." }, Ct);
        await person.PatchAsJsonAsync(route, new { name = "Tester Two" }, Ct);
        Assert.Equal(setAt, (await ReadAsync("Tester")).GetProperty("systemPromptSetAt").GetString());
        Assert.Single(await InstructionEventsAsync("Tester"));

        // A clear IS a change.
        await person.PatchAsJsonAsync(route, new { systemPrompt = "" }, Ct);
        var cleared = await ReadAsync("Tester");
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("systemPrompt").ValueKind);
        Assert.Equal(Person, cleared.GetProperty("systemPromptSetBy").GetString());

        var events = await InstructionEventsAsync("Tester");
        Assert.Equal(2, events.Count);
        using var clearDetail = JsonDocument.Parse(events[0].Detail!);
        Assert.True(clearDetail.RootElement.GetProperty("cleared").GetBoolean());
    }

    [Fact]
    public async Task A_managers_patch_is_refused_with_the_body_a_missing_permit_gets()
    {
        await Tools(await ManagerKeyAsync()).Hire(
            "Builder", team: host.Alpha, prompt: "Build it.", cancellationToken: Ct);
        var route = $"/api/teams/{host.Alpha}/containers/Builder";

        using var manager = host.Container(await ManagerKeyAsync());
        var refused = await manager.PatchAsJsonAsync(route, new { systemPrompt = "Build something else." }, Ct);

        // The shape the permit gate answers a credential missing its permit with: 403 and one
        // `error` field, here saying a person has to do this.
        using var bare = host.Container(await Mint("Alpha-Nothing", new HashSet<string>()));
        var missing = await bare.PostAsJsonAsync(
            $"/api/teams/{host.Alpha}/containers", new { name = "Nobody", systemPrompt = "x" }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
        Assert.Equal(missing.StatusCode, refused.StatusCode);
        Assert.Equal(missing.Content.Headers.ContentType?.MediaType, refused.Content.Headers.ContentType?.MediaType);

        var refusedBody = await refused.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var missingBody = await missing.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(
            missingBody.EnumerateObject().Select(p => p.Name),
            refusedBody.EnumerateObject().Select(p => p.Name));
        Assert.Equal(PermitGate.HumansOnlyMessage, refusedBody.GetProperty("error").GetString());

        var member = await ReadAsync("Builder");
        Assert.Equal("Build it.", member.GetProperty("systemPrompt").GetString());
        Assert.Equal("manager", member.GetProperty("systemPromptSetByKind").GetString());
        Assert.Empty(await InstructionEventsAsync("Builder"));
    }

    private async Task<JsonElement> ReadAsync(string member)
    {
        using var person = await host.PersonAsync();
        return await person.GetFromJsonAsync<JsonElement>($"/api/teams/{host.Alpha}/containers/{member}", Ct);
    }

    /// <summary>This member's `member.instructions-changed` rows, newest first.</summary>
    private async Task<List<TenantEvent>> InstructionEventsAsync(string member)
    {
        var page = await host.Services.GetRequiredService<ITenantLog>().ReadAsync(take: ITenantLog.MaxTake, ct: Ct);
        return page.Events
            .Where(e => e.Action == TenantActions.MemberInstructionsChanged
                && string.Equals(e.Subject, $"{host.Alpha}/{member}", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static void AssertUtcAtOrAfter(DateTimeOffset before, string? value)
    {
        Assert.NotNull(value);
        Assert.EndsWith("Z", value, StringComparison.Ordinal);
        var at = DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        Assert.InRange(at, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
    }

    private string? _managerKey;

    /// <summary>The Alpha Manager's credential, minted as `AgentEnvironment` mints one.</summary>
    private async Task<string> ManagerKeyAsync() =>
        _managerKey ??= await Mint(TeamRegistry.DefaultManagerName, Permits.All);

    private Task<string> Mint(string member, IReadOnlySet<string> permits) =>
        host.Services.GetRequiredService<IPrincipalStore>().MintAsync(
            new ContainerId(host.Alpha, member).ToString(), PrincipalKind.Container, host.Alpha, permits);

    private PlatformMcpTools Tools(string key)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[ApiKeyAuthenticationHandler.Header] = key;
        context.User = PrincipalClaims.ToClaimsPrincipal(
            new Principal(
                new ContainerId(host.Alpha, TeamRegistry.DefaultManagerName).ToString(),
                PrincipalKind.Container, new HashSet<string>()),
            "test");

        return new PlatformMcpTools(
            new HttpContextAccessor { HttpContext = context },
            host.Services.GetRequiredService<IPrincipalStore>(),
            host.Services.GetRequiredService<AgentCatalog>(),
            new ServerClients(host.ServerHandler()));
    }

    private sealed class ServerClients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost") };
    }
}
