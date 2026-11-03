using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Harness.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// A MEMBER'S OWN ESTIMATED HOURLY RATE AND BILLING ACCOUNT, set by a person on the member PATCH
/// (<c>auth-034</c>). Absent leaves the stored value, null or blank clears it; a rate is stored as
/// canonical text and "0" is explicitly not priced; an account id is stored lower-case; anything else
/// refuses the request with nothing written. A change appends <c>member.billing-changed</c> with the
/// stored old and new text, in the row's own transaction. The member read answers the effective rate
/// a run starting now is snapshotted at.
/// </summary>
public sealed class MemberRateTests(HostFixture host) : IClassFixture<HostFixture>
{
    private const string Person = "person@example.test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_rate_is_stored_as_canonical_text_and_zero_as_zero_and_a_clear_keeps_who_set_it()
    {
        using var person = await host.PersonAsync();
        var route = await HireAsync(person, "Rater");

        var before = DateTimeOffset.UtcNow;
        var saved = await person.PatchAsJsonAsync(route, new { hourlyRate = 62.50m }, Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var answered = await saved.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("62.5", answered.GetProperty("hourlyRate").GetString());
        Assert.Equal(Person, answered.GetProperty("billingSetBy").GetString());
        AssertUtcAtOrAfter(before, answered.GetProperty("billingSetAt").GetString());
        Assert.Equal(ExecutionSnapshotFacts.RateSourceMember, answered.GetProperty("effectiveRate").GetProperty("source").GetString());

        // A string is read the same way, and trailing zeros never reach the row.
        await person.PatchAsJsonAsync(route, new { hourlyRate = "60.00" }, Ct);
        Assert.Equal("60", (await ReadAsync(person, route)).GetProperty("hourlyRate").GetString());

        await person.PatchAsJsonAsync(route, new { hourlyRate = 0 }, Ct);
        var zero = await ReadAsync(person, route);
        Assert.Equal("0", zero.GetProperty("hourlyRate").GetString());
        Assert.Equal(
            [null, null, ExecutionSnapshotFacts.RateSourceNone],
            EffectiveOf(zero));

        // An absent field leaves it; a name-only save is no billing change.
        await person.PatchAsJsonAsync(route, new { name = "Rater Two" }, Ct);
        Assert.Equal("0", (await ReadAsync(person, route)).GetProperty("hourlyRate").GetString());

        // A clear is a change, and who made it stays.
        var cleared = await person.PatchAsJsonAsync(route, new { hourlyRate = (string?)null }, Ct);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var read = await ReadAsync(person, route);
        Assert.Equal(JsonValueKind.Null, read.GetProperty("hourlyRate").ValueKind);
        Assert.Equal(Person, read.GetProperty("billingSetBy").GetString());

        await person.PatchAsJsonAsync(route, new { hourlyRate = "75" }, Ct);
        await person.PatchAsJsonAsync(route, new { hourlyRate = "  " }, Ct);
        Assert.Equal(JsonValueKind.Null, (await ReadAsync(person, route)).GetProperty("hourlyRate").ValueKind);
    }

    [Fact]
    public async Task A_billing_account_is_stored_lower_case_and_null_or_blank_clears_it()
    {
        using var person = await host.PersonAsync();
        var account = await AccountAsync(person);
        var route = await HireAsync(person, "Accounted");

        await person.PatchAsJsonAsync(route, new { billingAccountId = account }, Ct);
        Assert.Equal(account.ToLowerInvariant(), (await ReadAsync(person, route)).GetProperty("billingAccountId").GetString());

        // Absent leaves it.
        await person.PatchAsJsonAsync(route, new { hourlyRate = 10 }, Ct);
        Assert.Equal(account.ToLowerInvariant(), (await ReadAsync(person, route)).GetProperty("billingAccountId").GetString());

        await person.PatchAsJsonAsync(route, new { billingAccountId = "" }, Ct);
        Assert.Equal(JsonValueKind.Null, (await ReadAsync(person, route)).GetProperty("billingAccountId").ValueKind);

        await person.PatchAsJsonAsync(route, new { billingAccountId = account }, Ct);
        await person.PatchAsJsonAsync(route, new { billingAccountId = (string?)null }, Ct);
        var read = await ReadAsync(person, route);
        Assert.Equal(JsonValueKind.Null, read.GetProperty("billingAccountId").ValueKind);
        Assert.Equal(Person, read.GetProperty("billingSetBy").GetString());
    }

    [Theory]
    [InlineData("""{"hourlyRate":-1}""", MemberBilling.RateRefused)]
    [InlineData("""{"hourlyRate":"-0.5"}""", MemberBilling.RateRefused)]
    [InlineData("""{"hourlyRate":"sixty"}""", MemberBilling.RateRefused)]
    [InlineData("""{"hourlyRate":"6e1"}""", MemberBilling.RateRefused)]
    [InlineData("""{"hourlyRate":true}""", MemberBilling.RateRefused)]
    [InlineData("""{"billingAccountId":"acct-1"}""", MemberBilling.AccountRefused)]
    [InlineData("""{"billingAccountId":"{3f2b8c1e-9a4d-4e2f-b6a1-0c5d7e8f9a10}"}""", MemberBilling.AccountRefused)]
    [InlineData("""{"billingAccountId":"3f2b8c1e9a4d4e2fb6a10c5d7e8f9a10"}""", MemberBilling.AccountRefused)]
    [InlineData("""{"billingAccountId":42}""", MemberBilling.AccountRefused)]
    [InlineData("""{"name":"Renamed","hourlyRate":-1}""", MemberBilling.RateRefused)]
    public async Task A_negative_or_unreadable_rate_or_account_refuses_the_request_and_writes_nothing(string body, string refusal)
    {
        using var person = await host.PersonAsync();
        var account = await AccountAsync(person);
        var member = "Refused" + Math.Abs(body.GetHashCode(StringComparison.Ordinal)).ToString(CultureInfo.InvariantCulture);
        var route = await HireAsync(person, member);
        await person.PatchAsJsonAsync(route, new { hourlyRate = 40, billingAccountId = account }, Ct);
        var tenantRows = (await EventsAsync()).Count;

        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        var refused = await person.PatchAsync(route, content, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(refusal, (await refused.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());

        var read = await ReadAsync(person, route);
        Assert.Equal("40", read.GetProperty("hourlyRate").GetString());
        Assert.Equal(account.ToLowerInvariant(), read.GetProperty("billingAccountId").GetString());
        Assert.Equal(member, read.GetProperty("name").GetString());
        Assert.Equal(tenantRows, (await EventsAsync()).Count);
    }

    [Fact]
    public async Task A_rate_or_account_change_writes_member_billing_changed_with_the_stored_old_and_new_and_a_name_only_save_does_not()
    {
        using var person = await host.PersonAsync();
        var account = await AccountAsync(person);
        var route = await HireAsync(person, "Audited");
        var subject = $"{host.Alpha}/Audited";

        await person.PatchAsJsonAsync(route, new { hourlyRate = 62.5m, billingAccountId = account }, Ct);

        var first = Assert.Single(Billing(await EventsAsync(), subject));
        Assert.Equal(Person, first.ActorEmail);
        using (var detail = JsonDocument.Parse(first.Detail!))
        {
            Assert.Equal(JsonValueKind.Null, detail.RootElement.GetProperty("rate").GetProperty("old").ValueKind);
            Assert.Equal("62.5", detail.RootElement.GetProperty("rate").GetProperty("new").GetString());
            Assert.Equal(JsonValueKind.Null, detail.RootElement.GetProperty("billingAccount").GetProperty("old").ValueKind);
            Assert.Equal(account.ToLowerInvariant(), detail.RootElement.GetProperty("billingAccount").GetProperty("new").GetString());
        }

        // A billing-only save is no member.changed.
        Assert.Empty(Changed(await EventsAsync(), subject));

        // The same values again change nothing and write nothing.
        await person.PatchAsJsonAsync(route, new { hourlyRate = "62.50", billingAccountId = account }, Ct);
        Assert.Single(Billing(await EventsAsync(), subject));

        // A name-only save writes member.changed and no billing row.
        await person.PatchAsJsonAsync(route, new { name = "Audited Two" }, Ct);
        Assert.Single(Changed(await EventsAsync(), subject));
        Assert.Single(Billing(await EventsAsync(), subject));

        // A clear is a change: old is the stored text, new is null.
        await person.PatchAsJsonAsync(route, new { hourlyRate = (string?)null }, Ct);
        var clear = Billing(await EventsAsync(), subject)[0];
        using var cleared = JsonDocument.Parse(clear.Detail!);
        Assert.Equal("62.5", cleared.RootElement.GetProperty("rate").GetProperty("old").GetString());
        Assert.Equal(JsonValueKind.Null, cleared.RootElement.GetProperty("rate").GetProperty("new").ValueKind);
        Assert.Equal(account.ToLowerInvariant(), cleared.RootElement.GetProperty("billingAccount").GetProperty("new").GetString());
        Assert.Equal(2, Billing(await EventsAsync(), subject).Count);
    }

    [Fact]
    public async Task The_read_answers_the_effective_rate_the_next_snapshot_records()
    {
        using var person = await host.PersonAsync();
        var route = await HireAsync(person, "Effective");

        try
        {
            // No tenant row: not priced, never a zero.
            Assert.Equal([null, null, ExecutionSnapshotFacts.RateSourceNone], EffectiveOf(await ReadAsync(person, route)));

            var set = await person.PutAsJsonAsync(
                "/api/tenant/settings", new Dictionary<string, object> { [ExecutionSnapshotFacts.AgentHourlyRateSetting] = 60 }, Ct);
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
            Assert.Equal(["60", "USD", ExecutionSnapshotFacts.RateSourceTenant], EffectiveOf(await ReadAsync(person, route)));

            await person.PatchAsJsonAsync(route, new { hourlyRate = 80.25m }, Ct);
            Assert.Equal(["80.25", "USD", ExecutionSnapshotFacts.RateSourceMember], EffectiveOf(await ReadAsync(person, route)));

            // An explicit zero does not fall through to the tenant rate.
            await person.PatchAsJsonAsync(route, new { hourlyRate = 0 }, Ct);
            Assert.Equal([null, null, ExecutionSnapshotFacts.RateSourceNone], EffectiveOf(await ReadAsync(person, route)));
        }
        finally
        {
            await person.PutAsJsonAsync(
                "/api/tenant/settings", new Dictionary<string, object?> { [ExecutionSnapshotFacts.AgentHourlyRateSetting] = null }, Ct);
        }
    }

    [Fact]
    public async Task An_agent_cannot_set_a_rate_or_an_account()
    {
        using var person = await host.PersonAsync();
        var account = await AccountAsync(person);
        var route = await HireAsync(person, "Guarded");

        using var manager = host.Container(await host.Services.GetRequiredService<IPrincipalStore>().MintAsync(
            new ContainerId(host.Alpha, "Rates-Manager").ToString(), PrincipalKind.Container, host.Alpha, Permits.All, ct: Ct));
        var refused = await manager.PatchAsJsonAsync(route, new { hourlyRate = 99, billingAccountId = account }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(PermitGate.HumansOnlyMessage, (await refused.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());

        var read = await ReadAsync(person, route);
        Assert.Equal(JsonValueKind.Null, read.GetProperty("hourlyRate").ValueKind);
        Assert.Equal(JsonValueKind.Null, read.GetProperty("billingAccountId").ValueKind);
        Assert.Empty(Billing(await EventsAsync(), $"{host.Alpha}/Guarded"));
    }

    // ---- fixtures ----

    /// <summary>A new billing account's id, upper-cased, so a test can see the PATCH store it lower-case.</summary>
    private static async Task<string> AccountAsync(HttpClient person)
    {
        var created = await person.PostAsJsonAsync(
            "/api/billing-accounts",
            new { name = "Rates", vendor = "Anthropic", billingType = "metered", currency = "USD", billingPeriod = "monthly", effectiveFrom = "2026-11-01" },
            Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!.ToUpperInvariant();
    }

    private async Task<string> HireAsync(HttpClient person, string name)
    {
        var hired = await person.PostAsJsonAsync($"/api/teams/{host.Alpha}/containers", new { name, systemPrompt = "Work." }, Ct);
        Assert.Equal(HttpStatusCode.OK, hired.StatusCode);
        return $"/api/teams/{host.Alpha}/containers/{name}";
    }

    private static Task<JsonElement> ReadAsync(HttpClient person, string route) =>
        person.GetFromJsonAsync<JsonElement>(route, Ct);

    private static string?[] EffectiveOf(JsonElement member)
    {
        var effective = member.GetProperty("effectiveRate");
        string? Text(string name) => effective.GetProperty(name).ValueKind == JsonValueKind.Null ? null : effective.GetProperty(name).GetString();
        return [Text("amount"), Text("currency"), Text("source")];
    }

    private async Task<IReadOnlyList<TenantEvent>> EventsAsync() =>
        (await host.Services.GetRequiredService<ITenantLog>().ReadAsync(take: ITenantLog.MaxTake, ct: Ct)).Events;

    /// <summary>This member's `member.billing-changed` rows, newest first.</summary>
    private static List<TenantEvent> Billing(IReadOnlyList<TenantEvent> events, string subject) =>
        [.. events.Where(e => e.Action == TenantActions.MemberBillingChanged && string.Equals(e.Subject, subject, StringComparison.OrdinalIgnoreCase))];

    private static List<TenantEvent> Changed(IReadOnlyList<TenantEvent> events, string subject) =>
        [.. events.Where(e => e.Action == TenantActions.MemberChanged && string.Equals(e.Subject, subject, StringComparison.OrdinalIgnoreCase))];

    private static void AssertUtcAtOrAfter(DateTimeOffset before, string? value)
    {
        Assert.NotNull(value);
        Assert.EndsWith("Z", value, StringComparison.Ordinal);
        var at = DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        Assert.InRange(at, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
    }
}
