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
/// THE EXPENSES, through the Host's routes: a person records, voids and replaces one, each write a new
/// row with its tenant row; no route changes or deletes a row; a machine principal is refused every
/// route with the humans-only sentence, a key holding every permit included, before a missing id can
/// be told from a real one.
/// </summary>
public sealed class ExpenseTests(HostFixture host) : IClassFixture<HostFixture>
{
    private const string Person = "person@example.test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> AccountAsync(HttpClient person, string name)
    {
        var created = await person.PostAsJsonAsync(
            "/api/billing-accounts",
            new { name, vendor = "Anthropic", billingType = "subscription", currency = "usd", billingPeriod = "monthly", recurringAmount = 30, effectiveFrom = "2026-11-01" },
            Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;
    }

    private static object Charge(string accountId, object? amount = null) =>
        new { accountId, amount = amount ?? 30, currency = "USD", servicePeriodStart = "2026-11-01", servicePeriodEnd = "2026-12-01", kind = "subscription-charge", reference = "INV-1" };

    [Fact]
    public async Task A_person_records_voids_and_replaces_an_expense_and_each_write_is_a_new_row_with_its_tenant_row()
    {
        using var person = await host.PersonAsync();
        var account = await AccountAsync(person, "Expensed");

        // The client's own id, status, supersedes and recorder are not read: the store sets them.
        var recorded = await person.PostAsJsonAsync(
            "/api/expenses",
            new { accountId = account, amount = 30, currency = "usd", servicePeriodStart = "2026-11-01", servicePeriodEnd = "2026-12-01", kind = "invoice",
                  id = "mine", status = "void", supersedes = Guid.NewGuid().ToString(), recordedBy = "someone@example.test", recordedAt = "2020-01-01" },
            Ct);
        Assert.Equal(HttpStatusCode.Created, recorded.StatusCode);
        var first = await recorded.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var firstId = first.GetProperty("id").GetString()!;
        Assert.Equal($"/api/expenses/{firstId}", recorded.Headers.Location?.OriginalString);
        Assert.Equal(
            (account, "30", "USD", "invoice", "confirmed", JsonValueKind.Null, Person, true, JsonValueKind.Null),
            (first.GetProperty("accountId").GetString(), first.GetProperty("amount").GetString(), first.GetProperty("currency").GetString(),
             first.GetProperty("kind").GetString(), first.GetProperty("status").GetString(), first.GetProperty("supersedes").ValueKind,
             first.GetProperty("recordedBy").GetString(), first.GetProperty("current").GetBoolean(), first.GetProperty("supersededBy").ValueKind));
        Assert.NotEqual("mine", firstId);

        var replaced = await person.PostAsJsonAsync($"/api/expenses/{firstId}/replace", new { amount = "32.50", note = "Seat added" }, Ct);
        Assert.Equal(HttpStatusCode.Created, replaced.StatusCode);
        var second = await replaced.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var secondId = second.GetProperty("id").GetString()!;
        Assert.Equal(("32.5", firstId, "confirmed", "Seat added"),
            (second.GetProperty("amount").GetString(), second.GetProperty("supersedes").GetString(), second.GetProperty("status").GetString(), second.GetProperty("note").GetString()));

        var voided = await person.PostAsJsonAsync($"/api/expenses/{secondId}/void", new { note = "Refunded" }, Ct);
        Assert.Equal(HttpStatusCode.Created, voided.StatusCode);
        var third = await voided.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var thirdId = third.GetProperty("id").GetString()!;
        Assert.Equal(("void", secondId, "32.5", "Refunded"),
            (third.GetProperty("status").GetString(), third.GetProperty("supersedes").GetString(), third.GetProperty("amount").GetString(), third.GetProperty("note").GetString()));

        // A void without a body is allowed too, on a fresh row.
        var other = (await (await person.PostAsJsonAsync("/api/expenses", Charge(account, 5), Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.Created, (await person.PostAsync($"/api/expenses/{other}/void", null, Ct)).StatusCode);

        // Every row is listed, current and superseded, with who supersedes it.
        var listed = (await person.GetFromJsonAsync<JsonElement>($"/api/expenses?accountId={account}", Ct)).EnumerateArray().ToList();
        Assert.Equal(5, listed.Count);
        Assert.Equal([firstId, secondId, thirdId], listed.Take(3).Select(e => e.GetProperty("id").GetString()));
        Assert.Equal([secondId, thirdId, null], listed.Take(3).Select(e => e.GetProperty("supersededBy").GetString()));
        Assert.Equal([false, false, true], listed.Take(3).Select(e => e.GetProperty("current").GetBoolean()));
        Assert.Contains((await person.GetFromJsonAsync<JsonElement>("/api/expenses", Ct)).EnumerateArray(), e => e.GetProperty("id").GetString() == thirdId);
        Assert.Equal(secondId, (await person.GetFromJsonAsync<JsonElement>($"/api/expenses/{firstId.ToUpperInvariant()}", Ct)).GetProperty("supersededBy").GetString());

        var rows = (await EventsAsync()).Where(e => e.Subject == firstId || e.Subject == secondId || e.Subject == thirdId).Reverse().ToList();
        Assert.Equal(
            [TenantActions.BillingExpenseRecorded, TenantActions.BillingExpenseReplaced, TenantActions.BillingExpenseVoided],
            rows.Select(r => r.Action));
        Assert.All(rows, r => Assert.Equal((Person, "Expensed"), (r.ActorEmail, r.SubjectName)));
        using var detail = JsonDocument.Parse(rows[2].Detail!);
        Assert.Equal((thirdId, account, secondId), (
            detail.RootElement.GetProperty("expense").GetString(), detail.RootElement.GetProperty("account").GetString(),
            detail.RootElement.GetProperty("supersedes").GetString()));
    }

    [Fact]
    public async Task A_refused_expense_or_correction_is_one_sentence_and_writes_nothing()
    {
        using var person = await host.PersonAsync();
        var account = await AccountAsync(person, "Refused expenses");
        var otherAccount = await AccountAsync(person, "Other");
        var id = (await (await person.PostAsJsonAsync("/api/expenses", Charge(account), Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;
        var voidId = (await (await person.PostAsync($"/api/expenses/{id}/void", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;
        var live = (await (await person.PostAsJsonAsync("/api/expenses", Charge(account), Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;
        var missingAccount = Guid.NewGuid().ToString();
        var before = (await EventsAsync()).Count;
        var rowsBefore = (await person.GetFromJsonAsync<JsonElement>("/api/expenses", Ct)).GetArrayLength();

        (string Route, string Body, HttpStatusCode Status, string Sentence)[] cases =
        [
            ("/api/expenses", "[]", HttpStatusCode.BadRequest, SpendEndpoints.ExpenseNotAnObject),
            ("/api/expenses", $$"""{"accountId":"{{missingAccount}}","amount":1,"currency":"USD","servicePeriodStart":"2026-11-01","servicePeriodEnd":"2026-12-01","kind":"invoice"}""", HttpStatusCode.BadRequest, BillingAccountRules.Missing(missingAccount)),
            ("/api/expenses", $$"""{"accountId":"{{account}}","amount":1,"currency":"EUR","servicePeriodStart":"2026-11-01","servicePeriodEnd":"2026-12-01","kind":"invoice"}""", HttpStatusCode.BadRequest, ExpenseRules.WrongCurrency("USD")),
            ("/api/expenses", $$"""{"accountId":"{{account}}","amount":-1,"currency":"USD","servicePeriodStart":"2026-11-01","servicePeriodEnd":"2026-12-01","kind":"invoice"}""", HttpStatusCode.BadRequest, ExpenseRules.NegativeRefused),
            ("/api/expenses", $$"""{"accountId":"{{account}}","amount":1,"currency":"USD","servicePeriodStart":"2026-11-01","servicePeriodEnd":"2026-12-01","kind":"provider-reported"}""", HttpStatusCode.BadRequest, ExpenseRules.KindRefused),
            ("/api/expenses", $$"""{"accountId":"{{account}}","amount":true}""", HttpStatusCode.BadRequest, ExpenseRules.AmountRefused),
            ("/api/expenses", $$"""{"accountId":"{{account}}","note":5}""", HttpStatusCode.BadRequest, SpendEndpoints.ExpenseNotText("note")),
            ($"/api/expenses/{live}/replace", $$"""{"accountId":"{{otherAccount}}"}""", HttpStatusCode.BadRequest, ExpenseRules.OtherAccount),
            ($"/api/expenses/{live}/replace", """{"servicePeriodEnd":"2026-10-01"}""", HttpStatusCode.BadRequest, ExpenseRules.EndNotAfterStart),
            ($"/api/expenses/{live}/void", """{"note":7}""", HttpStatusCode.BadRequest, SpendEndpoints.ExpenseNotText("note")),
            ($"/api/expenses/{id}/void", "{}", HttpStatusCode.Conflict, ExpenseRules.AlreadyCorrected),
            ($"/api/expenses/{id}/replace", """{"amount":1}""", HttpStatusCode.Conflict, ExpenseRules.AlreadyCorrected),
            ($"/api/expenses/{voidId}/void", "{}", HttpStatusCode.Conflict, ExpenseRules.VoidIsFinal),
            ($"/api/expenses/{voidId}/replace", """{"amount":1}""", HttpStatusCode.Conflict, ExpenseRules.VoidIsFinal),
        ];

        foreach (var (route, body, status, sentence) in cases)
        {
            using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            var refused = await person.PostAsync(route, content, Ct);
            Assert.Equal(status, refused.StatusCode);
            Assert.Equal(sentence, (await refused.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
        }

        var missing = Guid.NewGuid().ToString();
        foreach (var route in new[] { $"/api/expenses/{missing}/void", $"/api/expenses/{missing}/replace" })
        {
            var notFound = await person.PostAsJsonAsync(route, new { amount = 1 }, Ct);
            Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
            Assert.Equal(ExpenseRules.Missing(missing), (await notFound.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
        }

        var get = await person.GetAsync($"/api/expenses/{missing}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(ExpenseRules.Missing(missing), (await get.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());

        // There is no route that changes or deletes a row.
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await person.DeleteAsync($"/api/expenses/{live}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await person.PostAsJsonAsync($"/api/expenses/{live}", new { amount = 1 }, Ct)).StatusCode);

        Assert.Equal(before, (await EventsAsync()).Count);
        Assert.Equal(rowsBefore, (await person.GetFromJsonAsync<JsonElement>("/api/expenses", Ct)).GetArrayLength());
    }

    [Fact]
    public async Task A_machine_principal_is_refused_every_expense_route_with_the_humans_only_sentence_even_holding_every_permit()
    {
        using var person = await host.PersonAsync();
        var account = await AccountAsync(person, "Guarded expenses");
        var id = (await (await person.PostAsJsonAsync("/api/expenses", Charge(account), Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;
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
                    await machine.GetAsync("/api/expenses", Ct),
                    await machine.GetAsync($"/api/expenses?accountId={account}", Ct),
                    await machine.GetAsync($"/api/expenses/{target}", Ct),
                    await machine.PostAsJsonAsync("/api/expenses", Charge(account), Ct),
                    await machine.PostAsJsonAsync($"/api/expenses/{target}/void", new { note = "machine" }, Ct),
                    await machine.PostAsJsonAsync($"/api/expenses/{target}/replace", new { amount = 1 }, Ct),
                };

                foreach (var answer in answers)
                {
                    Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
                    Assert.Equal(PermitGate.HumansOnlyMessage, (await answer.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
                }
            }
        }

        Assert.Equal(before, (await EventsAsync()).Count);
        var expense = await person.GetFromJsonAsync<JsonElement>($"/api/expenses/{id}", Ct);
        Assert.Equal(("30", true), (expense.GetProperty("amount").GetString(), expense.GetProperty("current").GetBoolean()));
    }

    private async Task<IReadOnlyList<TenantEvent>> EventsAsync() =>
        (await host.Services.GetRequiredService<ITenantLog>().ReadAsync(take: ITenantLog.MaxTake, ct: Ct)).Events;
}

/// <summary>A fresh volume seeds no expense: the list is 200 and empty.</summary>
public sealed class ExpenseEmptyTests(HostFixture host) : IClassFixture<HostFixture>
{
    [Fact]
    public async Task A_fresh_volume_lists_no_expense()
    {
        using var person = await host.PersonAsync();

        var listed = await person.GetAsync("/api/expenses", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.Equal("[]", await listed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
