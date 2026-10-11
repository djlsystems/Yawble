using System.ComponentModel;
using Harness.Contracts;

namespace Harness.Host.Auth;

/// <summary>
/// User API keys: the first credential in this system a human ever sees.
///
/// Every route here is HumansOnly, and that is a design property rather than a default. Minting
/// deliberately has no owner parameter - you mint for yourself - which closes impersonation from
/// one side; the marker closes it from the other, because a key that could mint a key could mint
/// itself a successor that outlives its own revocation.
///
/// Another person's key is theirs alone: only its owner mints it and only its owner revokes it.
/// Anybody who could mint a credential that acts as somebody else would hold an impersonation
/// primitive with no trace distinguishing it from that person's own key.
/// </summary>
public static class KeyEndpoints
{
    private const string Area = "Keys";

    /// <summary>What a person can call a key. Long enough for "my laptop, the CI box" and short
    /// enough to render in a table column beside five other fields.</summary>
    private const int LabelLimit = 60;

    /// <summary>
    /// Generated per key and NEVER derived from the owner. MintAsync upserts on the principal id,
    /// so a derived id would make minting a second key silently revoke the first - and the person
    /// would have no reason to look. The `apikey-` prefix keeps it out of the shape a ContainerId
    /// parses (`Team/Name`) and out of the Concierge shape (`concierge-<team>-<user>`).
    /// No slash, because it travels as a single route segment.
    /// </summary>
    private static string NewKeyId() => $"apikey-{Guid.NewGuid():N}";

    public static void Map(WebApplication app)
    {
        app.MapPost("/api/keys", async (
            MintKey request, HttpContext context, IPrincipalStore principals,
            TenantLogging audit, CancellationToken ct) =>
        {
            if (PrincipalClaims.From(context.User) is not { } caller) return Results.Unauthorized();

            var label = request.Label?.Trim();

            // Refused rather than stored blank. A roster of unnamed keys is a roster nobody can
            // revoke from with any confidence, and "which of these three is the CI box" is not a
            // question the prefix answers.
            if (string.IsNullOrWhiteSpace(label))
            {
                return Results.BadRequest(new { error = "Name this key." });
            }

            if (label.Length > LabelLimit)
            {
                return Results.BadRequest(
                    new { error = $"A key's name is at most {LabelLimit} characters." });
            }

            var id = NewKeyId();

            // team: null - a key is bounded by its owner alone, resolved per request. See
            // TeamAccess.EffectiveTeamsAsync.
            //
            // Permits.All - the full set.
            var credential = await principals.MintAsync(
                id, PrincipalKind.ApiKey, team: null, Permits.All,
                ownerUserId: caller.Id, label: label, ct: ct);

            // Read back rather than recomputed. SqlitePrincipalStore.MintAsync has already written
            // this key's prefix (its own private PrefixLength) and its own "O"-formatted
            // created_at - recomputing either here is two stores of one fact, and the recomputed
            // createdAt would already be a different instant from the one on the row.
            //
            // .First, not .FirstOrDefault with a fallback: the row cannot legitimately be missing -
            // MintAsync just wrote it for this very id - so a fallback here is an UNREACHABLE branch
            // that would never be seen to be wrong. It hard-coded an `8` duplicating the store's own
            // private PrefixLength, and a createdAt that is a DIFFERENT INSTANT from the stored one.
            // If the row really is missing, that is a bug worth an exception rather than a silently
            // wrong response.
            var row = (await principals.ListApiKeysForAsync(caller.Id, ct)).First(k => k.Id == id);

            var prefix = row.Prefix;
            var createdAt = row.CreatedAt;

            await audit.WriteAsync(
                context, TenantActions.KeyMinted, id, label,
                // The prefix, never the credential.
                new { prefix }, ct);

            return Results.Ok(new
            {
                id,
                label,
                prefix,
                createdAt,

                // The ONLY time this value exists outside the caller's hands. Only its SHA-256 hash
                // is stored, so this is enforced by the store rather than promised by a dialog.
                credential,
            });
        })
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Mint an API key for yourself")
            .WithDescription(
                "Creates a credential that acts as **you**, resolved against your account on every "
                + "request rather than copied now. The credential is returned **once** and "
                + "only its hash is stored - it cannot be read back.\n\n"
                + "The owner is never a parameter: you mint for yourself and nobody else. "
                + "**A person's action** - an API key cannot call this, so a stolen key cannot "
                + "mint itself a successor.\n\n"
                + "400 for a blank name or one over 60 characters.");

        app.MapGet("/api/keys", async (
            HttpContext context, IPrincipalStore principals, CancellationToken ct) =>
        {
            if (PrincipalClaims.From(context.User) is not { } caller) return Results.Unauthorized();

            return Results.Ok(await principals.ListApiKeysForAsync(caller.Id, ct));
        })
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("List your own API keys")
            .WithDescription(
                "Your keys, newest first. There is no credential field on this shape - the value "
                + "was shown once at mint and only its hash is kept. The 8-character `prefix` is "
                + "what lets you tell one row from another.\n\n"
                + "Yours only. Every key in the tenant is listed by `GET /api/admin/keys`.");

        app.MapDelete("/api/keys/{id}", async (
            [Description(
                "The key's id, as returned by `POST /api/keys` and listed by `GET /api/keys`. Not "
                + "the prefix and not the credential.")]
            string id,
            HttpContext context, IPrincipalStore principals, IUserStore users,
            TenantLogging audit, CancellationToken ct) =>
        {
            if (PrincipalClaims.From(context.User) is not { } caller) return Results.Unauthorized();

            // Null for an unknown id AND for anything that is not an API key - a container's or a
            // console session's credential is not revocable here, whoever is asking. See
            // IPrincipalStore.OwnerOfApiKeyAsync for why that filter lives in the store.
            var owner = await principals.OwnerOfApiKeyAsync(id, ct);

            // ONE refusal for three states: no such key, not an API key, and not yours. They are
            // byte-identical on purpose - "forbidden" would confirm that the id names a real
            // credential, which is the only thing a caller could learn here that they should not.
            if (owner is null || !string.Equals(owner, caller.Id, StringComparison.Ordinal))
            {
                return Results.NotFound(new { error = "No such key." });
            }

            // Read BEFORE the revoke. The row is gone afterwards, and a log entry that cannot say
            // WHICH key was destroyed is not an audit trail.
            var summary = (await principals.ListApiKeysForAsync(owner, ct))
                .FirstOrDefault(k => string.Equals(k.Id, id, StringComparison.Ordinal));

            await principals.RevokeAsync(id, ct);

            await audit.WriteAsync(
                context, TenantActions.KeyRevoked, id, summary?.Label,
                new
                {
                    prefix = summary?.Prefix,
                    // WHOSE key, which is the question an offboarding review asks. The email
                    // rather than the id, for the same reason the actor's is denormalised: a join
                    // to a deleted row answers nothing.
                    owner = (await users.FindByIdAsync(owner, ct))?.Email,
                },
                ct);

            return Results.NoContent();
        })
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Revoke an API key")
            .WithDescription(
                "Destroys the credential. It stops authenticating on the very next request - the "
                + "API-key handler resolves the row on every call rather than trusting a ticket.\n\n"
                + "**Owner only.** Another person's key is theirs to revoke; deleting that person "
                + "removes their keys with them.\n\n"
                + "404 for an id that is unknown, that belongs to somebody else, or that names a "
                + "container or Concierge credential - all three answer identically, so "
                + "the route cannot be used to discover which keys exist.");

        app.MapGet("/api/admin/keys", async (
            IPrincipalStore principals, IUserStore users, TeamRegistry teams,
            CancellationToken ct) =>
        {
            // One pass over the accounts rather than a lookup per row: this is an administration
            // screen rather than a hot path.
            var accounts = (await users.ListAsync(ct)).ToDictionary(u => u.Id, StringComparer.Ordinal);

            var rows = new List<object>();

            foreach (var key in await principals.ListAllApiKeysAsync(ct))
            {
                accounts.TryGetValue(key.OwnerUserId, out var owner);

                rows.Add(new
                {
                    key.Id,
                    key.Label,
                    key.Prefix,
                    key.CreatedAt,
                    key.LastUsedAt,

                    // Denormalised deliberately, exactly as the tenant log denormalises an actor:
                    // an owner who has been deleted cannot be joined to, and their keys are gone
                    // with them anyway (owner_user_id cascades).
                    Owner = new
                    {
                        Id = key.OwnerUserId,
                        Email = owner?.Email,
                    },
                });
            }

            return Results.Ok(rows);
        })
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("List every API key in the tenant")
            .WithDescription(
                "Every key, with its owner. A key reaches every team its owner does, which is "
                + "every team that exists. Read-only: only a key's owner may revoke it.\n\n"
                + "**API keys only.** Container and Concierge credentials are machinery "
                + "with lifetimes tied to a container and a session, and a row somebody can revoke "
                + "is a row somebody will revoke.");
    }

    /// <summary>
    /// The owner is deliberately absent. See the class remarks: adding it would make this an
    /// impersonation primitive, and nobody has a need of one.
    /// </summary>
    internal sealed record MintKey(
        [property: Description(
            "What to call this key, for the roster that renders it. Required, and at most 60 "
            + "characters - an unnamed key is one nobody can confidently revoke.")]
        string? Label);
}
