using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Contracts.Link;
using Microsoft.AspNetCore.DataProtection;

namespace Harness.Host.Link;

/// <summary>What an attach came to: <see cref="Sentence"/> is what the person reads, and <see cref="Link"/>
/// the link stored, only when <see cref="Ok"/>.</summary>
public sealed record AttachOutcome(bool Ok, string Sentence, StoredLink? Link);

/// <summary>
/// ATTACH AND DETACH. Attach spends a one-time token at <c>POST {serviceUrl}/api/link/attach</c> for the
/// link's credential and stores the link, the credential sealed by <see cref="LinkCredential"/>. A failure
/// - the service's own sentence, or a service that could not be reached - stores nothing and leaves an
/// earlier link in place; only a new attach that succeeds replaces it. Detach forgets the link and its
/// credential. Each attach, detach and refusal is one tenant row naming the service, never the token or
/// the credential.
/// </summary>
public sealed class LinkAttach(ILinkStore store, IDataProtectionProvider protection, ITenantLog tenantLog, HttpClient http, TimeProvider clock)
{
    public const string NotAttached = "Not attached.";

    /// <summary>How the attach was asked, as the tenant row says it: by the operator CLI.</summary>
    public const string ViaCli = "cli";

    /// <summary>... or by <c>HARNESS_LINK_URL</c> and <c>HARNESS_LINK_TOKEN</c> at start.</summary>
    public const string ViaEnvironment = "environment";

    private static readonly JsonSerializerOptions Detail = new(JsonSerializerDefaults.Web);

    // One attach or detach at a time: the environment's at start and the CLI's never interleave.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<AttachOutcome> AttachAsync(string serviceUrl, string token, CancellationToken ct) =>
        AttachAsync(serviceUrl, token, ViaCli, ct);

    public async Task<AttachOutcome> AttachAsync(string serviceUrl, string token, string via, CancellationToken ct)
    {
        var url = (serviceUrl ?? "").Trim().TrimEnd('/');

        if (UrlRefusal(url) is { } refusal) return new AttachOutcome(false, refusal, null);
        if (string.IsNullOrWhiteSpace(token)) return new AttachOutcome(false, "Give the token the management service issued.", null);

        await _gate.WaitAsync(ct);
        try
        {
            var build = BuildVersion.Current;
            HttpResponseMessage response;

            try
            {
                response = await http.PostAsJsonAsync(url + LinkProtocol.AttachPath, new AttachRequest(token.Trim(), build.Version, build.Commit), LinkJson.Options, ct);
            }
            catch (Exception exception) when (exception is HttpRequestException || (exception is OperationCanceledException && !ct.IsCancellationRequested))
            {
                return new AttachOutcome(false, $"Could not reach {url}.", null);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    var sentence = await ErrorOf(response, ct) ?? $"{url} refused the attach ({(int)response.StatusCode}).";
                    await RowAsync(TenantActions.LinkRefused, url, null, new { url, reason = sentence, via }, ct);
                    return new AttachOutcome(false, sentence, null);
                }

                var answer = await AnswerOf(response, ct);
                if (answer is null) return new AttachOutcome(false, $"{url} answered, but not with a link.", null);

                var now = clock.GetUtcNow();
                var link = new StoredLink(
                    url, answer.ServiceName.Trim(), answer.InstanceName.Trim(), LinkCredential.Protect(protection, answer.Credential),
                    now, LinkState.Connecting, now, null, null);

                await store.SaveAsync(link, ct);
                await RowAsync(TenantActions.LinkAttached, url, link.ServiceName,
                    new { url, service = link.ServiceName, instance = link.InstanceName, via }, ct);

                return new AttachOutcome(true, $"Attached to {link.ServiceName} as {link.InstanceName}.", link);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forgets the link and its credential. Answers the sentence the person reads.</summary>
    public async Task<string> DetachAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (await store.GetAsync(ct) is not { } link) return NotAttached;

            await store.ClearAsync(ct);
            await RowAsync(TenantActions.LinkDetached, link.ServiceUrl, link.ServiceName,
                new { url = link.ServiceUrl, service = link.ServiceName, instance = link.InstanceName }, ct);

            return $"Detached from {link.ServiceName}.";
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Why <paramref name="url"/> cannot be a service URL, or null. https anywhere; http only to a loopback
    /// or private address - the rule a worker's control address follows - since the token and the
    /// credential cross it.
    /// </summary>
    public static string? UrlRefusal(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.Query))
        {
            return $"{url} is not a service URL: give its https:// address.";
        }

        if (WorkerProcess.Plaintext(uri) is not null)
        {
            return $"{url} is neither loopback nor private, so it must be https://: the token would otherwise cross the network in the clear.";
        }

        return null;
    }

    private static async Task<string?> ErrorOf(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<LinkError>(LinkJson.Options, ct);
            return string.IsNullOrWhiteSpace(error?.Error) ? null : error.Error.Trim();
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or HttpRequestException)
        {
            return null;
        }
    }

    private static async Task<AttachAnswer?> AnswerOf(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var answer = await response.Content.ReadFromJsonAsync<AttachAnswer>(LinkJson.Options, ct);
            return answer is { Credential.Length: > 0 } && !string.IsNullOrWhiteSpace(answer.ServiceName) && !string.IsNullOrWhiteSpace(answer.InstanceName)
                ? answer
                : null;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or HttpRequestException)
        {
            return null;
        }
    }

    // The operator CLI and the environment have no person: the row has no actor, and says how it was asked.
    private Task RowAsync(string action, string url, string? serviceName, object detail, CancellationToken ct) =>
        tenantLog.WriteAsync(null, null, action, url, serviceName, JsonSerializer.Serialize(detail, Detail), ct);
}
