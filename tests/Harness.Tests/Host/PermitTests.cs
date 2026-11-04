using System.Net;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host.Auth;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// AGENTS.md, Authorization: a member credential must not perform tenant administration; a person
/// can. The container here holds every permit, so what refuses it is HumansOnly and nothing else -
/// asserted by the refusal's own text, because users, catalog and skills also carry
/// RequireTenantAdmin and a 403 from THAT wall would hide HumansOnly's removal.
/// </summary>
public sealed class PermitTests(HostFixture host) : IClassFixture<HostFixture>
{
    public static TheoryData<string, string> AdministrationRoutes => new()
    {
        { "GET", "/api/users" },
        { "POST", "/api/users" },
        { "DELETE", "/api/users/someone" },
        { "POST", "/api/users/someone/password" },
        { "GET", "/api/keys" },
        { "POST", "/api/keys" },
        { "DELETE", "/api/keys/some-key" },
        { "POST", "/api/users/someone/revoke-keys" },
        { "PUT", "/api/agents" },
        { "GET", "/api/skills" },
        { "GET", "/api/skills/member" },
        { "POST", "/api/skills/some-skill" },
        { "PUT", "/api/skills/some-skill" },
        { "DELETE", "/api/skills/some-skill" },
    };

    // Reads only: a person's success is shown without the test administering anything.
    public static TheoryData<string> PersonReads => new()
    {
        "/api/users", "/api/keys", "/api/skills", "/api/skills/member",
    };

    [Theory]
    [MemberData(nameof(AdministrationRoutes))]
    public async Task A_container_key_is_refused_tenant_administration(string method, string route)
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = host.Container(host.AlphaContainerKey);
        using var request = new HttpRequestMessage(new HttpMethod(method), route.Replace("{own}", host.Alpha));

        if (method is "POST" or "PUT")
        {
            request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        }

        var response = await client.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal(PermitGate.HumansOnlyMessage, body.RootElement.GetProperty("error").GetString());
    }

    /// <summary>
    /// A MACHINE PRINCIPAL'S PERMITS ARE READ ON EVERY REQUEST: the key's stored row loses <c>read</c> and the very next
    /// request is refused, naming it. PermitGate trusts the permit claims only because the auth path rebuilds them from
    /// the row each time; if a machine principal ever became cookie-authenticated, this fails.
    /// </summary>
    [Fact]
    public async Task A_revoked_permit_is_refused_on_the_next_request()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = new ContainerId(host.Alpha, "Revoked" + Guid.NewGuid().ToString("N")[..8]).ToString();
        var key = await host.Services.GetRequiredService<IPrincipalStore>().MintAsync(
            id, PrincipalKind.Container, host.Alpha, new HashSet<string>([Permits.Read], StringComparer.Ordinal), ct: ct);
        using var client = host.Container(key);
        var route = $"/api/teams/{host.Alpha}/wakes";

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(route, ct)).StatusCode);

        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(host.DataRoot, "messages.db")};Pooling=false"))
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE principals SET permits = '[]' WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            Assert.Equal(1, await command.ExecuteNonQueryAsync(ct));
        }

        var refused = await client.GetAsync(route, ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        using var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(ct));
        Assert.Equal(PermitGate.Missing(Permits.Read), body.RootElement.GetProperty("error").GetString());
    }

    [Theory]
    [MemberData(nameof(PersonReads))]
    public async Task A_person_may(string route)
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = await host.PersonAsync();

        var response = await client.GetAsync(route, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
