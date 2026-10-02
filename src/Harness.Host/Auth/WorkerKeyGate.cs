using System.Security.Cryptography;
using System.Text;
using Harness.Contracts;

namespace Harness.Host.Auth;

/// <summary>
/// THE INSTANCE'S WORKER KEY, and the one place it is taken. A worker connects to control with it at
/// <see cref="ConnectRoute"/>; every other request that presents it - an API route, the MCP route, the
/// hub, a page of the console, a static file - is refused with <see cref="ElsewhereText"/> before
/// anything else reads it.
///
/// <para>
/// The key is a principal of kind <see cref="PrincipalKind.Worker"/> with no permit, stored hashed like
/// every credential (<see cref="SetAsync"/>), so the worker connection authenticates it the ordinary
/// way. This gate holds only the key's hash, to recognise it on requests the authentication middleware
/// never sees: static files and the console's pages are served before it runs.
/// </para>
/// </summary>
public sealed class WorkerKeyGate
{
    /// <summary>Where a worker connects, and the one route the worker key is taken on.</summary>
    public const string ConnectRoute = "/api/workers/connect";

    /// <summary>The principal row the worker key is stored as.</summary>
    public const string PrincipalId = "worker-key";

    /// <summary>What every other route answers the worker key with.</summary>
    public const string ElsewhereText = "A worker key is accepted only on the worker connection (/api/workers/connect).";

    private readonly byte[]? _hash;

    /// <param name="key">The configured worker key; null or blank when none is, and no request presents it.</param>
    public WorkerKeyGate(string? key) => _hash = string.IsNullOrWhiteSpace(key) ? null : Hash(key.Trim());

    /// <summary>Whether a worker key is configured, so a worker can connect at all.</summary>
    public bool Configured => _hash is not null;

    /// <summary>
    /// Stores <paramref name="key"/> as the worker key principal - hashed, no permit - or, with none,
    /// removes any worker key a previous start stored, so no worker can connect.
    /// </summary>
    public static async Task SetAsync(IPrincipalStore principals, string? key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            await principals.RevokeAsync(PrincipalId, ct);
            return;
        }

        await principals.MintAsync(
            PrincipalId, PrincipalKind.Worker, team: null, new HashSet<string>(StringComparer.Ordinal),
            credential: key.Trim(), label: "worker key", ct: ct);
    }

    /// <summary>Whether <paramref name="presented"/> is this instance's worker key.</summary>
    public bool Is(string? presented) =>
        _hash is not null && !string.IsNullOrWhiteSpace(presented)
        && CryptographicOperations.FixedTimeEquals(_hash, Hash(presented.Trim()));

    /// <summary>Refuses the worker key on every request but the worker connection. First in the pipeline after the headers.</summary>
    public void Use(WebApplication app) => app.Use(async (context, next) =>
    {
        if (_hash is not null
            && !context.Request.Path.Equals(ConnectRoute, StringComparison.Ordinal)
            && context.Request.Headers.TryGetValue(ApiKeyAuthenticationHandler.Header, out var presented)
            && Is(presented.ToString()))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new { error = ElsewhereText }, context.RequestAborted);
            return;
        }

        await next();
    });

    private static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));
}
