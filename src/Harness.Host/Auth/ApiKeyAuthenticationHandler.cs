using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Harness.Contracts;

namespace Harness.Host.Auth;

/// <summary>
/// How a machine authenticates: one header, one lookup, one principal.
///
/// A missing header is NoResult rather than Fail, so a browser carrying a cookie and no key is not
/// reported as a failed key - the two schemes have to be able to coexist on the same policy.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IPrincipalStore principals)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    // `new`: AuthenticationHandler<TOptions> already declares an INSTANCE property named Scheme.
    // This is a different thing entirely - the scheme's well-known NAME, referenced only through the
    // type ("ApiKeyAuthenticationHandler.Scheme") - so the hiding is intentional and the keyword just
    // says so instead of leaving a CS0108 warning for TreatWarningsAsErrors to turn into a build break.
    public new const string Scheme = "ApiKey";
    public const string Header = "X-Api-Key";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(Header, out var values)) return AuthenticateResult.NoResult();

        var credential = values.ToString();
        if (string.IsNullOrWhiteSpace(credential)) return AuthenticateResult.NoResult();

        var principal = await principals.ResolveAsync(credential, Context.RequestAborted);

        if (principal is null)
        {
            // Logged with the prefix rather than the credential: enough to recognise a misconfigured
            // agent, not enough to reuse.
            Logger.LogWarning(
                "Rejected an unknown credential with prefix {Prefix}.",
                credential.Length >= 8 ? credential[..8] : "(short)");

            return AuthenticateResult.Fail("Unknown credential.");
        }

        return AuthenticateResult.Success(
            new AuthenticationTicket(PrincipalClaims.ToClaimsPrincipal(principal, Scheme), Scheme));
    }
}
