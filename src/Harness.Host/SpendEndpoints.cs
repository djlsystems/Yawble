using System.Text.Json;
using Harness.Contracts;
using Harness.Host.Auth;
using Harness.Messaging;

namespace Harness.Host;

/// <summary>
/// The billing accounts, tenant-wide, not under a team: a person's to read and write
/// (<c>HumansOnly</c> on each route; there is no Spend permit, so a machine principal is refused every
/// one of them, whatever it holds, before a missing id can be told from a real one). There is no
/// delete: an account is deactivated, and the table refuses a delete.
///
/// <para>
/// A billing account is a named vendor account in its own currency. It is not a connection and holds
/// no credential, and nothing here computes Actual Spend: hours are never Actual Spend.
/// </para>
/// </summary>
public static class SpendEndpoints
{
    public const string NotAnObject = "The body must be a JSON object of the billing account's fields.";

    public const string ActiveOnEdit =
        "Whether a billing account is active is changed by deactivating or reactivating it, not by an edit.";

    public static string Fixed(string field) => $"A billing account's {field} cannot be changed.";

    public static string NotText(string field) => $"A billing account's {field} must be text.";

    /// <summary>The body's fields a person may send, by their JSON name.</summary>
    private static readonly string[] Editable =
        ["name", "vendor", "billingType", "currency", "billingPeriod", "recurringAmount", "effectiveFrom", "effectiveTo", "description", "scope"];

    private const string Fields =
        "`name` and `vendor` (required text, neither unique), `billingType` (`subscription`, `metered` or "
        + "`service`), `currency` (three letters, stored upper-case, the account's own and never converted), "
        + "`billingPeriod` (`monthly` or `annual`), `recurringAmount` (a number of zero or more, stored as "
        + "canonical text, or null; it is stored only, never projected and never added to an estimate), "
        + "`effectiveFrom` (an ISO-8601 date, read as midnight UTC, or instant), `effectiveTo` (null, or at "
        + "or after `effectiveFrom`), `description` (a short note, or null) and `scope` (only `tenant`).";

    private const string Answer =
        "An account answers `id` (a lower-case GUID the store assigns), its fields, `active`, `scope`, "
        + "`instanceId` (this instance's `instance.id`, copied at create), `createdBy`, `createdAt` and "
        + "`updatedAt`.";

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/billing-accounts", async (ISpendStore spend, CancellationToken ct) =>
                Results.Ok(await spend.ListAccountsAsync(ct)))
            .WithTags("Billing")
            .HumansOnly()
            .WithSummary("List the billing accounts")
            .WithDescription(
                "Every billing account, active and deactivated, oldest created first, then by id; an empty "
                + "list when there is none. " + Answer + " A person's only: no permit lets an agent read one.");

        app.MapGet("/api/billing-accounts/{id}", async (string id, ISpendStore spend, CancellationToken ct) =>
                await spend.FindAccountAsync(id, ct) is { } account
                    ? Results.Ok(account)
                    : Results.NotFound(new { error = BillingAccountRules.Missing(id) }))
            .WithTags("Billing")
            .HumansOnly()
            .WithSummary("One billing account")
            .WithDescription(Answer + " 404, with one sentence, for an id that names no account.");

        app.MapPost("/api/billing-accounts", async (
                JsonElement body, HttpContext context, ISpendStore spend, CancellationToken ct) =>
            {
                var (fields, refusal) = Read(body);
                if (refusal is not null) return Refused(400, refusal);

                var (id, email) = ActorClaims.Of(context.User);
                var write = await spend.CreateAccountAsync(
                    fields!, email ?? id ?? "unknown",
                    TenantLogging.Row(context, TenantActions.BillingAccountCreated, null, null, null), ct);

                return write.Ok
                    ? Results.Created($"/api/billing-accounts/{write.Account!.Id}", write.Account)
                    : Refused(write.Status, write.Refusal!);
            })
            .WithTags("Billing")
            .HumansOnly()
            .WithSummary("Create a billing account")
            .WithDescription(
                "A named vendor account the instance is billed through: " + Fields + " A new account is "
                + "active and tenant-scoped; the client does not choose its id. Anything else is refused "
                + "(400) with one sentence, and a create on an instance with no `instance.id` is refused "
                + "(409). Appends `billing.account-created`; nothing is created when that row cannot be "
                + "written. It is not a connection and holds no credential. " + Answer);

        app.MapPost("/api/billing-accounts/{id}", async (
                string id, JsonElement body, HttpContext context, ISpendStore spend, CancellationToken ct) =>
            {
                if (await spend.FindAccountAsync(id, ct) is not { } current)
                {
                    return Refused(404, BillingAccountRules.Missing(id));
                }

                var (fields, refusal) = Read(body, current);
                if (refusal is not null) return Refused(400, refusal);

                var write = await spend.EditAccountAsync(
                    id, fields!, TenantLogging.Row(context, TenantActions.BillingAccountChanged, null, null, null), ct);

                return write.Ok ? Results.Ok(write.Account) : Refused(write.Status, write.Refusal!);
            })
            .WithTags("Billing")
            .HumansOnly()
            .WithSummary("Edit a billing account")
            .WithDescription(
                "Changes the fields sent; an absent field stays. Null clears only `description`, "
                + "`effectiveTo` and `recurringAmount`; the other fields cannot be cleared. `id`, `scope`, "
                + "`instanceId`, `createdBy` and `createdAt` cannot be changed, and `active` is changed by "
                + "deactivate and reactivate only. " + Fields + " An edit that changes nothing writes "
                + "nothing and answers the account as it is; any other appends `billing.account-changed`, "
                + "naming each field that moved as old and new, in the same transaction. 404 for an id that "
                + "names no account.");

        app.MapPost("/api/billing-accounts/{id}/deactivate", (string id, HttpContext context, ISpendStore spend, CancellationToken ct) =>
                SetActiveAsync(id, false, context, spend, ct))
            .WithTags("Billing")
            .HumansOnly()
            .WithSummary("Deactivate a billing account")
            .WithDescription(
                "Sets `active` false and appends `billing.account-deactivated`, in one transaction. An "
                + "account already inactive is answered as it is, with nothing written. Members who point "
                + "at it keep it, and no run's snapshot is rewritten. 404 for an id that names no account.");

        app.MapPost("/api/billing-accounts/{id}/reactivate", (string id, HttpContext context, ISpendStore spend, CancellationToken ct) =>
                SetActiveAsync(id, true, context, spend, ct))
            .WithTags("Billing")
            .HumansOnly()
            .WithSummary("Reactivate a billing account")
            .WithDescription(
                "Sets `active` true and appends `billing.account-reactivated`, in one transaction. An "
                + "account already active is answered as it is, with nothing written. 404 for an id that "
                + "names no account.");
    }

    private static async Task<IResult> SetActiveAsync(
        string id, bool active, HttpContext context, ISpendStore spend, CancellationToken ct)
    {
        var action = active ? TenantActions.BillingAccountReactivated : TenantActions.BillingAccountDeactivated;
        var write = await spend.SetAccountActiveAsync(id, active, TenantLogging.Row(context, action, null, null, null), ct);
        return write.Ok ? Results.Ok(write.Account) : Refused(write.Status, write.Refusal!);
    }

    private static IResult Refused(int status, string sentence) => Results.Json(new { error = sentence }, statusCode: status);

    /// <summary>
    /// The body's fields, each absent, null or its text, or the one sentence that refuses the body. On an
    /// edit (<paramref name="current"/> given) a fixed field may only be sent as it is stored.
    /// </summary>
    internal static (BillingAccountFields? Fields, string? Refusal) Read(JsonElement body, BillingAccount? current = null)
    {
        if (body.ValueKind != JsonValueKind.Object) return (null, NotAnObject);

        if (current is not null)
        {
            string? Stored(string field) => field switch
            {
                "id" => current.Id,
                "instanceId" => current.InstanceId,
                "createdBy" => current.CreatedBy,
                "createdAt" => current.CreatedAt,
                _ => current.UpdatedAt,
            };

            foreach (var field in new[] { "id", "instanceId", "createdBy", "createdAt", "updatedAt" })
            {
                if (body.TryGetProperty(field, out var sent)
                    && !(sent.ValueKind == JsonValueKind.String && string.Equals(sent.GetString(), Stored(field), StringComparison.OrdinalIgnoreCase)))
                {
                    return (null, Fixed(field));
                }
            }

            if (body.TryGetProperty("active", out var active)
                && !((active.ValueKind == JsonValueKind.True && current.Active) || (active.ValueKind == JsonValueKind.False && !current.Active)))
            {
                return (null, ActiveOnEdit);
            }
        }

        var sents = new Dictionary<string, Sent?>();
        foreach (var field in Editable)
        {
            if (!body.TryGetProperty(field, out var value)) continue;

            switch (value.ValueKind)
            {
                case JsonValueKind.Null:
                    sents[field] = new Sent(null);
                    break;
                case JsonValueKind.String:
                    sents[field] = new Sent(value.GetString());
                    break;
                case JsonValueKind.Number when field == "recurringAmount":
                    sents[field] = new Sent(value.GetRawText());
                    break;
                default:
                    return (null, field == "recurringAmount" ? BillingAccountRules.RecurringAmountRefused : NotText(field));
            }
        }

        Sent? Of(string field) => sents.GetValueOrDefault(field);

        return (new BillingAccountFields(
            Of("name"), Of("vendor"), Of("billingType"), Of("currency"), Of("billingPeriod"), Of("recurringAmount"),
            Of("effectiveFrom"), Of("effectiveTo"), Of("description"), Of("scope")), null);
    }
}
