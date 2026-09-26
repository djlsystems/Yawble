using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// The four lists the screens scroll are walked with a cursor, never an offset.
///
/// <para>
/// Each test stands up its OWN host rather than sharing a class fixture, because the property under
/// test is exactly what a stray row from a neighbouring test would disturb: that the older pages a
/// reader has not reached yet do not move when something is appended above them.
/// </para>
///
/// <para>
/// Every walk asserts the same four things: the first page is the newest rows, newest first;
/// following the last row's key reaches every row exactly once; a cursor at the bottom answers an
/// empty page; and a row appended after the first page is read never enters or shifts the rest.
/// </para>
/// </summary>
public sealed class CursorPagingTests
{
    [Fact]
    public async Task Diagnostics_pages_by_seq_with_every_filter_still_applied()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await HostAsync();
        var log = host.Services.GetRequiredService<IDiagnosticsLog>();

        // Twelve rows under one search token, alternating severity, with unrelated rows between
        // them: the filter has to hold on every page, not only the first.
        for (var i = 0; i < 12; i++)
        {
            await log.WriteAsync(
                i % 2 == 0 ? DiagnosticSeverity.Error : DiagnosticSeverity.Info,
                DiagnosticKinds.HttpServerError, message: $"cursor-token row {i}", ct: ct);
            await log.WriteAsync(
                DiagnosticSeverity.Error, DiagnosticKinds.HttpServerError, message: $"noise {i}", ct: ct);
        }

        using var person = await host.PersonAsync();

        string Url(string query, long? before, int take) =>
            $"/api/diagnostics?{query}&take={take}" + (before is { } b ? $"&before={b}" : "");

        static IEnumerable<JsonElement> Rows(JsonElement view) =>
            view.GetProperty("page").GetProperty("events").EnumerateArray();

        // First page: newest first, the filtered total beside it.
        var first = await GetAsync(person, Url("search=cursor-token", null, 5), ct);
        Assert.Equal(12, first.GetProperty("page").GetProperty("total").GetInt64());
        Assert.Equal(5, Rows(first).Count());
        Assert.Equal("cursor-token row 11", Rows(first).First().GetProperty("message").GetString());

        // Next pages via the cursor: every matching row once, in seq order descending.
        var all = await WalkAsync(person, before => Url("search=cursor-token", before, 5), Rows, "seq", ct);
        Assert.Equal(12, all.Count);
        AssertStrictlyDescending(all);
        Assert.Equal(
            Enumerable.Range(0, 12).Reverse().Select(i => $"cursor-token row {i}"),
            all.Select(row => row.Message));

        // Exhaustion: a cursor below the last row answers an empty page, and the total is unchanged.
        var past = await GetAsync(person, Url("search=cursor-token", all[^1].Key, 5), ct);
        Assert.Empty(Rows(past));
        Assert.Equal(12, past.GetProperty("page").GetProperty("total").GetInt64());

        // Filter + cursor: severity AND search, walked two at a time.
        var errors = await WalkAsync(
            person, before => Url("severity=Error&search=cursor-token", before, 2), Rows, "seq", ct);
        Assert.Equal(6, errors.Count);
        Assert.All(errors, row => Assert.Equal("Error", row.Element.GetProperty("severity").GetString()));
        AssertStrictlyDescending(errors);

        // A row written after the first page does not appear in, or shift, the older pages.
        var top = Rows(await GetAsync(person, Url("search=cursor-token", null, 5), ct)).ToList();
        await log.WriteAsync(
            DiagnosticSeverity.Error, DiagnosticKinds.HttpServerError, message: "cursor-token late", ct: ct);
        var rest = await WalkAsync(
            person, before => Url("search=cursor-token", before, 5), Rows, "seq", ct,
            start: top[^1].GetProperty("seq").GetInt64());
        Assert.Equal(all.Skip(5).Select(row => row.Key), rest.Select(row => row.Key));
        Assert.DoesNotContain(rest, row => row.Message == "cursor-token late");
        Assert.Equal(
            "cursor-token late",
            Rows(await GetAsync(person, Url("search=cursor-token", null, 5), ct)).First()
                .GetProperty("message").GetString());

        // Take clamp: past the ceiling answers 200, zero answers one, absent answers 50.
        for (var i = 0; i < 205; i++)
        {
            await log.WriteAsync(DiagnosticSeverity.Info, DiagnosticKinds.HttpRefused, message: $"bulk {i}", ct: ct);
        }

        Assert.Equal(200, Rows(await GetAsync(person, Url("search=bulk", null, 1000), ct)).Count());
        Assert.Single(Rows(await GetAsync(person, Url("search=bulk", null, 0), ct)));
        Assert.Equal(50, Rows(await GetAsync(person, "/api/diagnostics?search=bulk", ct)).Count());
    }

    [Fact]
    public async Task The_tenant_log_pages_by_seq()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await HostAsync();
        var log = host.Services.GetRequiredService<ITenantLog>();

        for (var i = 0; i < 23; i++)
        {
            await log.WriteAsync("actor", "actor@example.test", "test.cursor", subject: $"row {i}", ct: ct);
        }

        using var person = await host.PersonAsync();

        static string Url(long? before, int take) =>
            $"/api/tenant-log?take={take}" + (before is { } b ? $"&before={b}" : "");

        static IEnumerable<JsonElement> Rows(JsonElement page) => page.GetProperty("events").EnumerateArray();

        // First page: the newest rows, and `total` is the whole log.
        var first = await GetAsync(person, Url(null, 4), ct);
        var total = first.GetProperty("total").GetInt64();
        Assert.True(total >= 23);
        Assert.Equal(4, Rows(first).Count());

        // Walking the cursor reaches every row once, newest first, and agrees with the total.
        var all = await WalkAsync(person, before => Url(before, 4), Rows, "seq", ct);
        Assert.Equal(total, all.Count);
        AssertStrictlyDescending(all);
        Assert.Equal(
            Enumerable.Range(0, 23).Reverse().Select(i => $"row {i}"),
            all.Where(row => row.Element.GetProperty("action").GetString() == "test.cursor")
                .Select(row => row.Element.GetProperty("subject").GetString()));

        // Exhaustion.
        Assert.Empty(Rows(await GetAsync(person, Url(all[^1].Key, 4), ct)));

        // A row appended after the first page stays out of the older pages and shifts none of them.
        var top = Rows(await GetAsync(person, Url(null, 4), ct)).ToList();
        await log.WriteAsync("actor", "actor@example.test", "test.cursor", subject: "late", ct: ct);
        var rest = await WalkAsync(
            person, before => Url(before, 4), Rows, "seq", ct, start: top[^1].GetProperty("seq").GetInt64());
        Assert.Equal(all.Skip(4).Select(row => row.Key), rest.Select(row => row.Key));
        Assert.Equal(
            "late", Rows(await GetAsync(person, Url(null, 4), ct)).First().GetProperty("subject").GetString());

        // Take clamp.
        for (var i = 0; i < 205; i++)
        {
            await log.WriteAsync("actor", "actor@example.test", "test.bulk", subject: $"bulk {i}", ct: ct);
        }

        Assert.Equal(200, Rows(await GetAsync(person, Url(null, 1000), ct)).Count());
        Assert.Single(Rows(await GetAsync(person, Url(null, 0), ct)));
        Assert.Equal(50, Rows(await GetAsync(person, "/api/tenant-log", ct)).Count());
    }

    [Fact]
    public async Task A_members_history_walks_back_to_the_first_message_it_received()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await HostAsync();
        var log = host.Services.GetRequiredService<IMessageLog>();

        // PAUSED, so the instructions below are rows on the log and not runs: a delivered
        // instruction would launch an agent whose own rows would land in this member's history.
        var registry = host.Services.GetRequiredService<TeamRegistry>();
        await registry.SetPausedAsync(host.Alpha, paused: true, ct);
        await registry.SetPausedAsync(host.Beta, paused: true, ct);

        var manager = new ContainerId(host.Alpha, TeamRegistry.DefaultManagerName);
        var other = new ContainerId(host.Beta, TeamRegistry.DefaultManagerName);

        // The first thing this member ever received, then a history interleaved with another
        // team's rows - the filter has to be in the query, above the cap.
        var told = await log.AppendAsync(
            new NewMessage(MessageTypes.InstructionFor(manager), """{"text":"first"}""", "console"), ct);

        for (var i = 0; i < 10; i++)
        {
            await log.AppendAsync(new NewMessage(MessageTypes.Completed, $$"""{"n":{{i}}}""", manager.ToString()), ct);
            await log.AppendAsync(new NewMessage(MessageTypes.Completed, $$"""{"n":{{i}}}""", other.ToString()), ct);
            await log.AppendAsync(
                new NewMessage(MessageTypes.InstructionFor(other), """{"text":"not yours"}""", "console"), ct);
        }

        var head = await log.HighestSeqAsync(ct) + 1;
        using var person = await host.PersonAsync();

        string Url(long? before, int take, string team = "", string member = "") =>
            $"/api/messages?team={Uri.EscapeDataString(team == "" ? manager.Team : team)}"
            + $"&member={Uri.EscapeDataString(member == "" ? manager.Name : member)}"
            + $"&take={take}&before={before ?? head}";

        static IEnumerable<JsonElement> Rows(JsonElement page) => page.EnumerateArray();

        // First page: this member's newest rows, newest first.
        var first = Rows(await GetAsync(person, Url(null, 3), ct)).ToList();
        Assert.Equal(3, first.Count);
        Assert.All(first, row => Assert.Equal(manager.ToString(), row.GetProperty("source").GetString()));

        // The walk reaches every row of this member's once and ends at the first thing it was told.
        var all = await WalkAsync(person, before => Url(before, 3), Rows, "seq", ct);
        Assert.Equal(11, all.Count);
        AssertStrictlyDescending(all);
        Assert.Equal(told.Seq, all[^1].Key);
        Assert.All(all, row => Assert.True(
            row.Element.GetProperty("source").GetString() == manager.ToString()
            || row.Element.GetProperty("type").GetString() == MessageTypes.InstructionFor(manager)));

        // Exhaustion.
        Assert.Empty(Rows(await GetAsync(person, Url(told.Seq, 3), ct)));

        // A row published after the first page stays out of the older pages.
        await log.AppendAsync(new NewMessage(MessageTypes.Completed, """{"n":"late"}""", manager.ToString()), ct);
        var rest = await WalkAsync(
            person, before => Url(before, 3), Rows, "seq", ct, start: first[^1].GetProperty("seq").GetInt64());
        Assert.Equal(all.Skip(3).Select(row => row.Key), rest.Select(row => row.Key));

        // Take clamp.
        for (var i = 0; i < 205; i++)
        {
            await log.AppendAsync(new NewMessage(MessageTypes.Completed, "{}", manager.ToString()), ct);
        }

        head = await log.HighestSeqAsync(ct) + 1;
        Assert.Equal(200, Rows(await GetAsync(person, Url(null, 1000), ct)).Count());
        Assert.Single(Rows(await GetAsync(person, Url(null, 0), ct)));
        Assert.Equal(50, Rows(await GetAsync(
            person, $"/api/messages?team={manager.Team}&member={manager.Name}&before={head}", ct)).Count());

        // The shape of a bad ask: incomplete, mixed with the forward cursor, unknown, or not yours.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await person.GetAsync($"/api/messages?before={head}", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await person.GetAsync($"/api/messages?team={manager.Team}&member={manager.Name}", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await person.GetAsync(Url(null, 5) + "&after=1", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await person.GetAsync(Url(null, 5, member: "Nobody"), ct)).StatusCode);

        using var alphaContainer = host.Container(host.AlphaContainerKey);
        Assert.Equal(HttpStatusCode.OK,
            (await alphaContainer.GetAsync(Url(null, 5), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await alphaContainer.GetAsync(Url(null, 5, team: other.Team, member: other.Name), ct)).StatusCode);

        // The forward cursor is untouched: still oldest first.
        var forward = Rows(await GetAsync(person, "/api/messages?after=0", ct)).Select(r => r.GetProperty("seq").GetInt64()).ToList();
        Assert.Equal(forward.Order(), forward);
    }

    [Fact]
    public async Task The_archive_pages_by_id_and_the_backlog_does_not_page()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await HostAsync();
        using var person = await host.PersonAsync();

        var archived = new List<long>();
        for (var i = 0; i < 8; i++)
        {
            var id = await NewItemAsync(person, $"archived {i}", ct);
            await ArchiveAsync(person, id, ct);
            archived.Add(id);

            // Live items between them: the archive must never show one.
            await NewItemAsync(person, $"live {i}", ct);
        }

        static string Url(long? before, int take) =>
            $"/api/backlog?archived=true&take={take}" + (before is { } b ? $"&before={b}" : "");

        static IEnumerable<JsonElement> Rows(JsonElement page) => page.EnumerateArray();

        // First page: the highest ids, newest first.
        var first = Rows(await GetAsync(person, Url(null, 3), ct)).ToList();
        Assert.Equal(
            archived.OrderDescending().Take(3),
            first.Select(item => item.GetProperty("id").GetInt64()));

        // Every archived item once, and nothing live.
        var all = await WalkAsync(person, before => Url(before, 3), Rows, "id", ct);
        Assert.Equal(archived.OrderDescending(), all.Select(row => row.Key));

        // Exhaustion.
        Assert.Empty(Rows(await GetAsync(person, Url(all[^1].Key, 3), ct)));

        // An item archived after the first page stays out of the older pages.
        var late = await NewItemAsync(person, "late", ct);
        await ArchiveAsync(person, late, ct);
        var rest = await WalkAsync(
            person, before => Url(before, 3), Rows, "id", ct, start: first[^1].GetProperty("id").GetInt64());
        Assert.Equal(all.Skip(3).Select(row => row.Key), rest.Select(row => row.Key));
        Assert.Equal(late, Rows(await GetAsync(person, Url(null, 3), ct)).First().GetProperty("id").GetInt64());

        // Filter + cursor: the only filter on this list is the archive axis itself, and the live
        // backlog is not paged at all.
        Assert.Equal(HttpStatusCode.BadRequest, (await person.GetAsync("/api/backlog?before=5", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await person.GetAsync("/api/backlog?take=5", ct)).StatusCode);
        Assert.Equal(8, Rows(await GetAsync(person, "/api/backlog", ct)).Count());

        // Take clamp.
        for (var i = 0; i < 205; i++) await ArchiveAsync(person, await NewItemAsync(person, $"bulk {i}", ct), ct);

        Assert.Equal(200, Rows(await GetAsync(person, Url(null, 1000), ct)).Count());
        Assert.Single(Rows(await GetAsync(person, Url(null, 0), ct)));
        Assert.Equal(50, Rows(await GetAsync(person, "/api/backlog?archived=true", ct)).Count());
    }

    private sealed record Row(long Key, JsonElement Element)
    {
        public string? Message =>
            Element.TryGetProperty("message", out var message) ? message.GetString() : null;
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

    /// <summary>
    /// Follows the cursor from <paramref name="start"/> (or the top) until an empty page, the way the
    /// client does: the next cursor is the last row's key. Bounded, so a cursor that failed to
    /// advance fails the test rather than hanging it.
    /// </summary>
    private static async Task<List<Row>> WalkAsync(
        HttpClient client, Func<long?, string> url, Func<JsonElement, IEnumerable<JsonElement>> rows,
        string key, CancellationToken ct, long? start = null)
    {
        var all = new List<Row>();
        var before = start;

        for (var page = 0; page < 500; page++)
        {
            var got = rows(await GetAsync(client, url(before), ct))
                .Select(row => new Row(row.GetProperty(key).GetInt64(), row))
                .ToList();

            if (got.Count == 0) return all;

            all.AddRange(got);
            before = got[^1].Key;
        }

        throw new InvalidOperationException("The cursor never reached an empty page.");
    }

    private static void AssertStrictlyDescending(IReadOnlyList<Row> rows)
    {
        for (var i = 1; i < rows.Count; i++) Assert.True(rows[i].Key < rows[i - 1].Key);
    }

    private static async Task<long> NewItemAsync(HttpClient person, string title, CancellationToken ct)
    {
        var created = await person.PostAsJsonAsync("/api/backlog", new { title, body = "A spec." }, ct);
        created.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await created.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("id").GetInt64();
    }

    private static async Task ArchiveAsync(HttpClient person, long id, CancellationToken ct) =>
        (await person.PostAsync($"/api/backlog/{id}/archive", null, ct)).EnsureSuccessStatusCode();
}
