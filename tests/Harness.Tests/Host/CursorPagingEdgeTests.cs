using System.Net;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// The edges <see cref="CursorPagingTests"/> leaves implicit: the tenant log's total ignores
/// the cursor, the diagnostics view keeps its whole shape on a cursor page, a negative take clamps
/// to one, a kind filter holds on every page, and the half-given asks answer 400.
/// </summary>
public sealed class CursorPagingEdgeTests
{
    [Fact]
    public async Task The_tenant_log_total_ignores_the_cursor_and_a_negative_take_is_one()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await HostAsync();
        var log = host.Services.GetRequiredService<ITenantLog>();

        for (var i = 0; i < 7; i++)
        {
            await log.WriteAsync("actor", "actor@example.test", "test.edge", subject: $"row {i}", ct: ct);
        }

        using var person = await host.PersonAsync();

        var first = await GetAsync(person, "/api/tenant-log?take=3", ct);
        var total = first.GetProperty("total").GetInt64();
        var cursor = first.GetProperty("events").EnumerateArray().Last().GetProperty("seq").GetInt64();

        var second = await GetAsync(person, $"/api/tenant-log?take=3&before={cursor}", ct);
        Assert.Equal(total, second.GetProperty("total").GetInt64());
        Assert.All(second.GetProperty("events").EnumerateArray(),
            row => Assert.True(row.GetProperty("seq").GetInt64() < cursor));

        Assert.Single((await GetAsync(person, "/api/tenant-log?take=-5", ct)).GetProperty("events").EnumerateArray());
    }

    [Fact]
    public async Task A_diagnostics_cursor_page_keeps_hasAny_and_kinds_and_the_kind_filter()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await HostAsync();
        var log = host.Services.GetRequiredService<IDiagnosticsLog>();

        for (var i = 0; i < 6; i++)
        {
            await log.WriteAsync(DiagnosticSeverity.Info, DiagnosticKinds.HttpRefused, message: $"edge refused {i}", ct: ct);
            await log.WriteAsync(DiagnosticSeverity.Info, DiagnosticKinds.HttpServerError, message: $"edge error {i}", ct: ct);
        }

        using var person = await host.PersonAsync();
        var kind = Uri.EscapeDataString(DiagnosticKinds.HttpRefused);

        var seen = new List<long>();
        long? before = null;
        long? total = null;

        for (var page = 0; page < 20; page++)
        {
            var view = await GetAsync(
                person, $"/api/diagnostics?kind={kind}&search=edge&take=4" + (before is { } b ? $"&before={b}" : ""), ct);

            Assert.True(view.GetProperty("hasAny").GetBoolean());
            Assert.Contains(DiagnosticKinds.HttpRefused,
                view.GetProperty("kinds").EnumerateArray().Select(k => k.GetString()));

            var pageTotal = view.GetProperty("page").GetProperty("total").GetInt64();
            total ??= pageTotal;
            Assert.Equal(total, pageTotal);

            var rows = view.GetProperty("page").GetProperty("events").EnumerateArray().ToList();
            if (rows.Count == 0) break;

            Assert.All(rows, row => Assert.Equal(DiagnosticKinds.HttpRefused, row.GetProperty("kind").GetString()));
            seen.AddRange(rows.Select(row => row.GetProperty("seq").GetInt64()));
            before = seen[^1];
        }

        Assert.Equal(6, total);
        Assert.Equal(6, seen.Count);
        Assert.Equal(seen.OrderDescending(), seen);

        Assert.Single((await GetAsync(person, "/api/diagnostics?search=edge&take=-1", ct))
            .GetProperty("page").GetProperty("events").EnumerateArray());
    }

    [Fact]
    public async Task Half_given_cursor_asks_are_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await HostAsync();
        using var person = await host.PersonAsync();

        var manager = new ContainerId(host.Alpha, TeamRegistry.DefaultManagerName);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await person.GetAsync("/api/backlog?archived=false&take=5", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await person.GetAsync("/api/backlog?archived=false&before=5", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await person.GetAsync($"/api/messages?team={manager.Team}&before=100", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await person.GetAsync($"/api/messages?member={manager.Name}&before=100", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await person.GetAsync($"/api/messages?team={manager.Team}&member={manager.Name}&before=100&after=0", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await person.GetAsync("/api/backlog?archived=true&take=5", ct)).StatusCode);
    }

    private static async Task<HostFixture> HostAsync()
    {
        var host = new HostFixture();
        await host.InitializeAsync();
        return host;
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string url, CancellationToken ct)
    {
        var response = await client.GetAsync(url, ct);
        Assert.True(response.IsSuccessStatusCode, $"{url} answered {(int)response.StatusCode}");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement.Clone();
    }
}
