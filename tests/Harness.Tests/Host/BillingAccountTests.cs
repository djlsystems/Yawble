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
/// THE BILLING ACCOUNTS, through the Host's routes: a person creates, edits, deactivates and reactivates
/// one, each real write with its tenant row and a write that changes nothing with none; a machine
/// principal is refused every route with the humans-only sentence, a key holding every permit included,
/// before a missing id can be told from a real one; and a member PATCH stores a billing account id only
/// when it names an account.
/// </summary>
public sealed class BillingAccountTests(HostFixture host) : IClassFixture<HostFixture>
{
    private const string Person = "person@example.test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object NewAccount(string name = "Anthropic API") =>
        new { name, vendor = "Anthropic", billingType = "metered", currency = "usd", billingPeriod = "monthly", recurringAmount = 20, effectiveFrom = "2026-11-01" };

    [Fact]
    public async Task A_person_creates_edits_deactivates_and_reactivates_an_account_and_each_real_write_has_its_tenant_row()
    {
        using var person = await host.PersonAsync();

        var created = await person.PostAsJsonAsync("/api/billing-accounts", NewAccount("Lifecycle"), Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var account = await created.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var id = account.GetProperty("id").GetString()!;
        Assert.Equal($"/api/billing-accounts/{id}", created.Headers.Location?.OriginalString);
        Assert.Equal(
            ("Lifecycle", "metered", "USD", "20", true, "tenant", Person),
            (account.GetProperty("name").GetString(), account.GetProperty("billingType").GetString(), account.GetProperty("currency").GetString(),
             account.GetProperty("recurringAmount").GetString(), account.GetProperty("active").GetBoolean(), account.GetProperty("scope").GetString(),
             account.GetProperty("createdBy").GetString()));

        // The instance id is the ledger's instance.id, not a second one.
        var settings = await host.Services.GetRequiredService<Harness.Identity.ITenantSettingsStore>().ReadAllAsync(Ct);
        Assert.Equal(settings.Single(s => s.Name == LedgerStart.InstanceIdName).Value, account.GetProperty("instanceId").GetString());

        // An edit changes the sent fields; null clears the description; the same values again write nothing.
        var edited = await person.PostAsJsonAsync($"/api/billing-accounts/{id}", new { vendor = "Anthropic PBC", description = "Seats", recurringAmount = (string?)null }, Ct);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var after = await edited.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(("Anthropic PBC", "Seats", JsonValueKind.Null, "Lifecycle"),
            (after.GetProperty("vendor").GetString(), after.GetProperty("description").GetString(), after.GetProperty("recurringAmount").ValueKind, after.GetProperty("name").GetString()));

        var same = await person.PostAsJsonAsync($"/api/billing-accounts/{id}", new { vendor = "Anthropic PBC", id, active = true }, Ct);
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        Assert.Equal(after.GetRawText(), (await same.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetRawText());

        Assert.False((await PostAsync(person, $"/api/billing-accounts/{id}/deactivate")).GetProperty("active").GetBoolean());
        Assert.False((await PostAsync(person, $"/api/billing-accounts/{id}/deactivate")).GetProperty("active").GetBoolean());
        Assert.True((await PostAsync(person, $"/api/billing-accounts/{id}/reactivate")).GetProperty("active").GetBoolean());
        Assert.True((await PostAsync(person, $"/api/billing-accounts/{id}/reactivate")).GetProperty("active").GetBoolean());

        var listed = await person.GetFromJsonAsync<JsonElement>("/api/billing-accounts", Ct);
        Assert.Contains(listed.EnumerateArray(), a => a.GetProperty("id").GetString() == id);
        Assert.Equal(id, (await person.GetFromJsonAsync<JsonElement>($"/api/billing-accounts/{id.ToUpperInvariant()}", Ct)).GetProperty("id").GetString());

        var rows = (await EventsAsync()).Where(e => e.Subject == id).Reverse().ToList();
        Assert.Equal(
            [TenantActions.BillingAccountCreated, TenantActions.BillingAccountChanged, TenantActions.BillingAccountDeactivated, TenantActions.BillingAccountReactivated],
            rows.Select(r => r.Action));
        Assert.All(rows, r => Assert.Equal((Person, "Lifecycle"), (r.ActorEmail, r.SubjectName)));
        using var detail = JsonDocument.Parse(rows[1].Detail!);
        Assert.Equal(id, detail.RootElement.GetProperty("account").GetString());
        Assert.Equal(["vendor", "recurringAmount", "description"], detail.RootElement.GetProperty("changes").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task A_refused_body_or_a_fixed_field_is_one_sentence_and_writes_nothing()
    {
        using var person = await host.PersonAsync();
        var id = (await (await person.PostAsJsonAsync("/api/billing-accounts", NewAccount("Refusals"), Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;
        var before = (await EventsAsync()).Count;

        (string Route, string Body, HttpStatusCode Status, string Sentence)[] cases =
        [
            ("/api/billing-accounts", """{"name":"X","vendor":"V","billingType":"prepaid","currency":"USD","billingPeriod":"monthly","effectiveFrom":"2026-11-01"}""", HttpStatusCode.BadRequest, BillingAccountRules.BillingTypeRefused),
            ("/api/billing-accounts", """{"name":"X","vendor":"V","billingType":"metered","currency":"USD","billingPeriod":"monthly","effectiveFrom":"2026-11-01","scope":"shared"}""", HttpStatusCode.BadRequest, BillingAccountRules.ScopeRefused),
            ("/api/billing-accounts", """{"name":"X","vendor":"V","billingType":"metered","currency":"USD","billingPeriod":"monthly","effectiveFrom":"2026-11-01","recurringAmount":-5}""", HttpStatusCode.BadRequest, BillingAccountRules.RecurringAmountRefused),
            ("/api/billing-accounts", """{"name":true}""", HttpStatusCode.BadRequest, SpendEndpoints.NotText("name")),
            ("/api/billing-accounts", "[]", HttpStatusCode.BadRequest, SpendEndpoints.NotAnObject),
            ($"/api/billing-accounts/{id}", """{"active":false}""", HttpStatusCode.BadRequest, SpendEndpoints.ActiveOnEdit),
            ($"/api/billing-accounts/{id}", """{"instanceId":"other"}""", HttpStatusCode.BadRequest, SpendEndpoints.Fixed("instanceId")),
            ($"/api/billing-accounts/{id}", """{"createdBy":"someone@example.test"}""", HttpStatusCode.BadRequest, SpendEndpoints.Fixed("createdBy")),
            ($"/api/billing-accounts/{id}", """{"name":null}""", HttpStatusCode.BadRequest, BillingAccountRules.NameRequired),
            ($"/api/billing-accounts/{id}", """{"effectiveTo":"2026-10-01"}""", HttpStatusCode.BadRequest, BillingAccountRules.EffectiveToBeforeFrom),
        ];

        foreach (var (route, body, status, sentence) in cases)
        {
            using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            var refused = await person.PostAsync(route, content, Ct);
            Assert.Equal(status, refused.StatusCode);
            Assert.Equal(sentence, (await refused.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
        }

        var missing = Guid.NewGuid().ToString();
        foreach (var route in new[] { $"/api/billing-accounts/{missing}", $"/api/billing-accounts/{missing}/deactivate", $"/api/billing-accounts/{missing}/reactivate" })
        {
            var notFound = await person.PostAsJsonAsync(route, new { name = "X" }, Ct);
            Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
            Assert.Equal(BillingAccountRules.Missing(missing), (await notFound.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
        }

        var get = await person.GetAsync($"/api/billing-accounts/{missing}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(BillingAccountRules.Missing(missing), (await get.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());

        Assert.Equal(before, (await EventsAsync()).Count);
        Assert.Equal("Refusals", (await person.GetFromJsonAsync<JsonElement>($"/api/billing-accounts/{id}", Ct)).GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_machine_principal_is_refused_every_route_with_the_humans_only_sentence_even_holding_every_permit()
    {
        using var person = await host.PersonAsync();
        var id = (await (await person.PostAsJsonAsync("/api/billing-accounts", NewAccount("Guarded"), Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;
        var before = (await EventsAsync()).Count;

        var principals = host.Services.GetRequiredService<IPrincipalStore>();
        var apiKey = await principals.MintAsync($"apikey-{Guid.NewGuid():N}", PrincipalKind.ApiKey, null, Permits.All, ct: Ct);
        var manager = await principals.MintAsync(
            new ContainerId(host.Alpha, TeamRegistry.DefaultManagerName).ToString(), PrincipalKind.Container, host.Alpha, Permits.All, ct: Ct);

        foreach (var key in new[] { apiKey, manager, host.AlphaContainerKey })
        {
            using var machine = host.Container(key);
            foreach (var target in new[] { id, Guid.NewGuid().ToString() })
            {
                var answers = new[]
                {
                    await machine.GetAsync("/api/billing-accounts", Ct),
                    await machine.GetAsync($"/api/billing-accounts/{target}", Ct),
                    await machine.PostAsJsonAsync("/api/billing-accounts", NewAccount("Machine's"), Ct),
                    await machine.PostAsJsonAsync($"/api/billing-accounts/{target}", new { name = "Machine's" }, Ct),
                    await machine.PostAsync($"/api/billing-accounts/{target}/deactivate", null, Ct),
                    await machine.PostAsync($"/api/billing-accounts/{target}/reactivate", null, Ct),
                };

                foreach (var answer in answers)
                {
                    Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
                    Assert.Equal(PermitGate.HumansOnlyMessage, (await answer.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
                }
            }
        }

        Assert.Equal(before, (await EventsAsync()).Count);
        var account = await person.GetFromJsonAsync<JsonElement>($"/api/billing-accounts/{id}", Ct);
        Assert.Equal(("Guarded", true), (account.GetProperty("name").GetString(), account.GetProperty("active").GetBoolean()));
    }

    [Fact]
    public async Task A_member_billing_account_id_is_stored_only_when_it_names_an_account_and_an_unknown_one_writes_nothing()
    {
        using var person = await host.PersonAsync();
        var route = await HireAsync(person, "Billed");
        var subject = $"{host.Alpha}/Billed";

        var id = (await (await person.PostAsJsonAsync("/api/billing-accounts", NewAccount("For members"), Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;
        await person.PatchAsJsonAsync(route, new { hourlyRate = 40, billingAccountId = id }, Ct);
        var tenantRows = (await EventsAsync()).Count;

        // An id that names no account is refused with one sentence: no member row, no tenant row.
        var unknown = Guid.NewGuid().ToString();
        var refused = await person.PatchAsJsonAsync(route, new { name = "Renamed", hourlyRate = 50, billingAccountId = unknown.ToUpperInvariant() }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(MemberBilling.AccountMissing(unknown), (await refused.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
        var read = await person.GetFromJsonAsync<JsonElement>(route, Ct);
        Assert.Equal((id, "40", "Billed"), (read.GetProperty("billingAccountId").GetString(), read.GetProperty("hourlyRate").GetString(), read.GetProperty("name").GetString()));
        Assert.Equal(tenantRows, (await EventsAsync()).Count);

        // A deactivated account is still an account, and may be stored; deactivating clears no member.
        var other = (await (await person.PostAsJsonAsync("/api/billing-accounts", NewAccount("Deactivated"), Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;
        await PostAsync(person, $"/api/billing-accounts/{id}/deactivate");
        Assert.Equal(id, (await person.GetFromJsonAsync<JsonElement>(route, Ct)).GetProperty("billingAccountId").GetString());
        await PostAsync(person, $"/api/billing-accounts/{other}/deactivate");
        var stored = await person.PatchAsJsonAsync(route, new { billingAccountId = other }, Ct);
        Assert.Equal(HttpStatusCode.OK, stored.StatusCode);
        Assert.Equal(other, (await stored.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("billingAccountId").GetString());

        // Blank still clears.
        await person.PatchAsJsonAsync(route, new { billingAccountId = " " }, Ct);
        Assert.Equal(JsonValueKind.Null, (await person.GetFromJsonAsync<JsonElement>(route, Ct)).GetProperty("billingAccountId").ValueKind);

        // A dangling id stored before any account existed stays through a save that does not send it.
        var dangling = Guid.NewGuid().ToString();
        await host.Services.GetRequiredService<TeamRegistry>().UpdateMemberAsync(
            host.Alpha, "Billed", null, null, null, null, Ct, billing: new MemberBillingEdit(false, null, true, dangling));
        var saved = await person.PatchAsJsonAsync(route, new { name = "Billed Two", hourlyRate = 45 }, Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var kept = await person.GetFromJsonAsync<JsonElement>(route, Ct);
        Assert.Equal((dangling, "45", "Billed Two"), (kept.GetProperty("billingAccountId").GetString(), kept.GetProperty("hourlyRate").GetString(), kept.GetProperty("name").GetString()));
        Assert.Contains(await EventsAsync(), e => e.Action == TenantActions.MemberBillingChanged && string.Equals(e.Subject, subject, StringComparison.OrdinalIgnoreCase));
    }

    // ---- fixtures ----

    private async Task<string> HireAsync(HttpClient person, string name)
    {
        var hired = await person.PostAsJsonAsync($"/api/teams/{host.Alpha}/containers", new { name, systemPrompt = "Work." }, Ct);
        Assert.Equal(HttpStatusCode.OK, hired.StatusCode);
        return $"/api/teams/{host.Alpha}/containers/{name}";
    }

    private static async Task<JsonElement> PostAsync(HttpClient person, string route)
    {
        var answer = await person.PostAsync(route, null, Ct);
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        return await answer.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private async Task<IReadOnlyList<TenantEvent>> EventsAsync() =>
        (await host.Services.GetRequiredService<ITenantLog>().ReadAsync(take: ITenantLog.MaxTake, ct: Ct)).Events;
}

/// <summary>A fresh volume seeds no billing account: the list is 200 and empty.</summary>
public sealed class BillingAccountEmptyTests(HostFixture host) : IClassFixture<HostFixture>
{
    [Fact]
    public async Task A_fresh_volume_lists_no_billing_account()
    {
        using var person = await host.PersonAsync();

        var listed = await person.GetAsync("/api/billing-accounts", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.Equal("[]", await listed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
