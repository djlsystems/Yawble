using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// A CONNECTION'S TEST, on this worker: a mailbox's IMAP login and SMTP authentication (the same
/// <see cref="IMailLogin"/> a mailbox is saved with), a sign-in's refresh and one read of the account's
/// profile, or the one harmless GET a credential's preset declares. Answers ok, refused or failed in one
/// sentence. No sentence carries a value the probe holds: each is taken out before it is answered, and
/// nothing here logs. Tests point it at fake servers: <paramref name="mail"/> trusting a fake server's
/// certificate, <paramref name="http"/> answering for a fake service.
/// </summary>
public sealed class ConnectionTester(IMailLogin? mail = null, HttpMessageHandler? http = null, TimeSpan? timeout = null)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    /// <summary>The most of a body read: enough for a refusal's reason.</summary>
    private const int MaximumBody = 64 * 1024;

    private const int MaximumReason = 300;

    private readonly IMailLogin _mail = mail ?? new SocketMailLogin();

    // NO REDIRECT IS FOLLOWED: a sign-in page answering with one has not said yes to the values.
    private readonly HttpClient _http = new(http ?? new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>The test's answer. Never throws for what a service does: that is the sentence.</summary>
    public async Task<ConnectionTested> TestAsync(TestConnection request, CancellationToken ct = default)
    {
        if (request.Probe is null) return new ConnectionTested(request.Request, ConnectionTested.Failed, "The test reached the worker with nothing to test.");

        var probe = ConnectionProbe.Open(request.Probe);

        var (outcome, sentence, rotated) = probe switch
        {
            MailboxProbe mailbox => await MailboxAsync(mailbox, ct),
            OAuthProbe oauth => await OAuthAsync(oauth, ct),
            HttpProbe get => await HttpAsync(get, ct),
            _ => (ConnectionTested.Failed, "This worker does not know how to test that connection.", null),
        };

        return new ConnectionTested(request.Request, outcome, Redact(sentence, SecretsOf(probe)), rotated is null ? null : new ConnectionTestSecret(rotated));
    }

    private static IEnumerable<string> SecretsOf(ConnectionProbe probe) => probe switch
    {
        MailboxProbe mailbox => [mailbox.Password],
        OAuthProbe oauth => [oauth.RefreshToken, .. oauth.ClientSecret is { } secret ? [secret] : Array.Empty<string>()],
        HttpProbe get => get.Secrets,
        _ => [],
    };

    /// <summary>Every value in <paramref name="secrets"/> taken out of <paramref name="sentence"/>.</summary>
    public static string Redact(string sentence, IEnumerable<string> secrets) =>
        secrets.Where(s => s.Length > 0).OrderByDescending(s => s.Length)
            .Aggregate(sentence, (text, secret) => text.Replace(secret, "[redacted]", StringComparison.Ordinal));

    // ---- a mailbox --------------------------------------------------------------------------------

    private async Task<(string, string, string?)> MailboxAsync(MailboxProbe probe, CancellationToken ct)
    {
        var settings = new MailboxSettings(
            probe.Username, new MailServer(probe.Imap.Host, probe.Imap.Port, probe.Imap.Security),
            new MailServer(probe.Smtp.Host, probe.Smtp.Port, probe.Smtp.Security), probe.Preset, true);

        var login = await _mail.LoginAsync(probe.Account, settings, probe.Password, ct);

        return (login.Outcome switch
        {
            MailLoginOutcome.Connected => ConnectionTested.Ok,
            MailLoginOutcome.Refused => ConnectionTested.Refused,
            _ => ConnectionTested.Failed,
        }, login.Sentence, null);
    }

    // ---- a sign-in --------------------------------------------------------------------------------

    private async Task<(string, string, string?)> OAuthAsync(OAuthProbe probe, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = probe.RefreshToken,
            ["client_id"] = probe.ClientId,
        };

        if (!string.IsNullOrEmpty(probe.ClientSecret)) form["client_secret"] = probe.ClientSecret;
        if (probe.Scopes is { Count: > 0 } scopes) form["scope"] = string.Join(' ', scopes);

        using var token = new HttpRequestMessage(HttpMethod.Post, probe.TokenUrl) { Content = new FormUrlEncodedContent(form) };
        token.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var (status, body, unreachable) = await SendAsync(token, HostOf(probe.TokenUrl, probe.Provider), ct);
        if (unreachable is not null) return (ConnectionTested.Failed, $"{unreachable} The sign-in was not renewed.", null);

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(body).RootElement.Clone();
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
        }
        catch (JsonException)
        {
            return (ConnectionTested.Failed, $"{probe.Provider} answered HTTP {status} without a token when asked to renew the sign-in. Try again later.", null);
        }

        var error = Text(root, "error");
        if (error is not null || status is < 200 or >= 300)
        {
            var reason = error is null ? $"HTTP {status}" : Text(root, "error_description") is { Length: > 0 } description ? $"{error}: {Cut(description)}" : error;

            return error is not null && status is >= 400 and < 500
                ? (ConnectionTested.Refused, $"{probe.Provider} refused to renew the sign-in ({reason}). Reconnect it.", null)
                : (ConnectionTested.Failed, $"{probe.Provider} could not renew the sign-in just now ({reason}). Try again later.", null);
        }

        if (Text(root, "access_token") is not { Length: > 0 } access)
        {
            return (ConnectionTested.Failed, $"{probe.Provider} renewed the sign-in but sent no access token. Try again later.", null);
        }

        var rotated = Text(root, "refresh_token") is { Length: > 0 } issued && issued != probe.RefreshToken ? issued : null;

        if (string.IsNullOrWhiteSpace(probe.ProfileUrl)) return (ConnectionTested.Ok, $"Connected: {probe.Provider} renewed the sign-in.", rotated);

        using var profile = new HttpRequestMessage(HttpMethod.Get, probe.ProfileUrl);
        profile.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        profile.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var (read, _, notRead) = await SendAsync(profile, HostOf(probe.ProfileUrl, probe.Provider), ct);
        var sentence = Redact(
            notRead is not null ? $"{probe.Provider} renewed the sign-in, but its profile could not be read: {notRead}"
            : read is 401 or 403 ? $"{probe.Provider} renewed the sign-in but refused to read the account's profile (HTTP {read}). Reconnect it."
            : read is < 200 or >= 300 ? $"{probe.Provider} renewed the sign-in, but reading the account's profile answered HTTP {read}. Try again later."
            : $"Connected: {probe.Provider} renewed the sign-in and read the account's profile.",
            [access]);

        var outcome = notRead is null && read is >= 200 and < 300 ? ConnectionTested.Ok
            : notRead is null && read is 401 or 403 ? ConnectionTested.Refused
            : ConnectionTested.Failed;

        // A ROTATED REFRESH TOKEN goes back whatever the profile said: the old one may no longer work.
        return (outcome, sentence, rotated);
    }

    // ---- a credential's declared request ------------------------------------------------------------

    private async Task<(string, string, string?)> HttpAsync(HttpProbe probe, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, probe.Url);
        foreach (var (name, value) in probe.Headers) request.Headers.TryAddWithoutValidation(name, value);

        var (status, body, unreachable) = await SendAsync(request, probe.Host, ct);
        if (unreachable is not null) return (ConnectionTested.Failed, unreachable, null);

        if (status is 401 or 403) return (ConnectionTested.Refused, $"{probe.Service} refused the {probe.What} (HTTP {status}).", null);

        if (status is >= 300 and < 400)
        {
            return (ConnectionTested.Failed,
                $"{probe.Host} answered with a redirect (HTTP {status}) rather than accepting the {probe.What}. Name the address that signs in.", null);
        }

        if (status is < 200 or >= 300) return (ConnectionTested.Failed, $"{probe.Service} answered HTTP {status}. Try again later.", null);

        if (probe.RefusedWhen is { } refusedWhen && Regex.IsMatch(body, refusedWhen, RegexOptions.None, TimeSpan.FromSeconds(1)))
        {
            var reason = probe.ReasonFrom is { } from && Regex.Match(body, from, RegexOptions.None, TimeSpan.FromSeconds(1)) is { Success: true } match
                ? Cut(System.Net.WebUtility.HtmlDecode(match.Groups[1].Value).Trim())
                : "";

            return (ConnectionTested.Refused, reason.Length > 0
                ? $"{probe.Service} refused the {probe.What}: {reason.TrimEnd('.')}."
                : $"{probe.Service} refused the {probe.What}.", null);
        }

        return (ConnectionTested.Ok, $"Connected: {probe.Service} accepted the {probe.What}.", null);
    }

    // ---- the wire ---------------------------------------------------------------------------------

    /// <summary>One request within the time allowed: its status and the start of its body, or the
    /// sentence saying why there is none. The sentence names the host, never the URL.</summary>
    private async Task<(int Status, string Body, string? Unreachable)> SendAsync(HttpRequestMessage request, string host, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout ?? DefaultTimeout);

        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(limit.Token);

            var buffer = new byte[MaximumBody];
            var read = 0;
            while (read < buffer.Length && await stream.ReadAsync(buffer.AsMemory(read), limit.Token) is var n and > 0) read += n;

            return ((int)response.StatusCode, Encoding.UTF8.GetString(buffer, 0, read), null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (0, "", $"{host} did not answer in time.");
        }
        catch (HttpRequestException)
        {
            return (0, "", $"Could not reach {host}.");
        }
    }

    private static string HostOf(string url, string fallback) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : fallback;

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Cut(string text)
    {
        var line = text.ReplaceLineEndings(" ");
        return line.Length <= MaximumReason ? line : line[..MaximumReason].TrimEnd() + "…";
    }
}
