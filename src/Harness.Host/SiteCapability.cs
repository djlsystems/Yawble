using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Harness.Host;

/// <summary>What a valid capability grants: one site of one team, to one person, until a time.</summary>
public sealed record SiteGrant(string Team, string Site, string UserId, string Email, DateTimeOffset Expires)
{
    /// <summary>What <c>site.whoami()</c> answers: the part of the email before the <c>@</c>. Never
    /// the email itself, and never a credential.</summary>
    public string DisplayName => Email.Split('@', 2)[0];
}

/// <summary>
/// THE PER-SITE CAPABILITY. A site's page runs in an opaque origin and must never carry the person's
/// session, so the Host issues this instead when a signed-in person opens the site, and the page's
/// every request carries it as a PATH SEGMENT (<c>/sites/&lt;team&gt;/&lt;site&gt;/_c/&lt;capability&gt;/...</c>):
/// relative links, images and the helper script's calls all inherit it, and no header or cookie is needed.
///
/// <para>
/// It is a Data Protection payload - authenticated and encrypted with the instance's key ring,
/// under its own purpose, so no other protected value (the session ticket included) is accepted as
/// one and it is accepted as nothing else. It names the team, the site and the person, and expires
/// after <see cref="Lifetime"/>. Only the site routes read it; to every other route it is an unknown
/// string. It allows reading the site's files, reading and writing its data and posting its actions,
/// and nothing more.
/// </para>
/// </summary>
public sealed class SiteCapability(IDataProtectionProvider provider)
{
    /// <summary>How long a capability lasts. Short, because it travels in a URL; reopening the site
    /// from the app issues a new one.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    private const string Purpose = "Harness.Sites.Capability.v1";

    private readonly ITimeLimitedDataProtector _protector =
        provider.CreateProtector(Purpose).ToTimeLimitedDataProtector();

    public string Issue(string team, string site, string userId, string email, DateTimeOffset? expires = null)
    {
        var body = JsonSerializer.Serialize(new Body(team, site, userId, email));
        return _protector.Protect(body, expires ?? DateTimeOffset.UtcNow.Add(Lifetime));
    }

    /// <summary>The grant <paramref name="capability"/> carries, or null when it is not one, has been
    /// altered, or has expired.</summary>
    public SiteGrant? Read(string? capability)
    {
        if (string.IsNullOrEmpty(capability) || capability.Length > 2048) return null;

        try
        {
            var text = _protector.Unprotect(capability, out var expires);
            var body = JsonSerializer.Deserialize<Body>(text);

            return body is { T: { } team, S: { } site, U: { } user, E: { } email }
                ? new SiteGrant(team, site, user, email, expires)
                : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record Body(string T, string S, string U, string E);
}
