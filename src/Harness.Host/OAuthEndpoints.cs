using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Harness.Host;

/// <summary>
/// THE ONLY WAY THE HOST TALKS TO AN OAUTH PROVIDER. Tests fake it and never call a real provider,
/// as <see cref="IGitHubContributor"/> is faked for GitHub. The client secret is handed in by the
/// caller for this one request and goes nowhere else.
/// </summary>
public interface IOAuthEndpoints
{
    /// <summary>The authorization code grant, with the PKCE verifier and the redirect URI the flow
    /// started with.</summary>
    Task<OAuthTokenAnswer> ExchangeCodeAsync(
        OAuthProvider provider, string? clientSecret, string code, string redirectUri, string codeVerifier, CancellationToken ct);

    /// <summary>The refresh token grant.</summary>
    Task<OAuthTokenAnswer> RefreshAsync(
        OAuthProvider provider, string? clientSecret, string refreshToken, CancellationToken ct);

    /// <summary>The provider's userinfo for <paramref name="accessToken"/>, or null when it has none or
    /// refuses.</summary>
    Task<JsonElement?> UserInfoAsync(OAuthProvider provider, string accessToken, CancellationToken ct);

    /// <summary>Revokes <paramref name="token"/> where the provider supports it. Returns null on
    /// success, else why not.</summary>
    Task<string?> RevokeAsync(OAuthProvider provider, string token, CancellationToken ct);

    /// <summary>The provider's device authorization endpoint: a device code the Host keeps, and the
    /// user code and link the person is shown.</summary>
    Task<DeviceAuthorizationAnswer> DeviceAuthorizationAsync(OAuthProvider provider, IReadOnlyList<string> scopes, CancellationToken ct);

    /// <summary>One poll of the token endpoint with the device code grant. Still waiting is an
    /// answer with <c>authorization_pending</c> or <c>slow_down</c> as its error.</summary>
    Task<OAuthTokenAnswer> DeviceTokenAsync(OAuthProvider provider, string? clientSecret, string deviceCode, CancellationToken ct);
}

/// <summary>A device authorization endpoint's answer. <see cref="DeviceCode"/> stays on the Host;
/// only the user code, the link and the expiry are shown.</summary>
public sealed record DeviceAuthorizationAnswer(
    string? DeviceCode,
    string? UserCode,
    string? VerificationUri,
    int? ExpiresIn,
    int? Interval,
    string? Error,
    string? ErrorDescription)
{
    public bool Ok => Error is null && !string.IsNullOrEmpty(DeviceCode) && !string.IsNullOrEmpty(UserCode) && !string.IsNullOrEmpty(VerificationUri);

    public string Reason => ErrorDescription is { Length: > 0 } description ? $"{Error}: {description}" : Error ?? "no device code";
}

/// <summary>
/// A token endpoint's answer. <see cref="Error"/> set means refused: <see cref="Refused"/> is a
/// provider's OAuth error (a 4xx with <c>error</c>), which only a person can fix by reconnecting;
/// otherwise it is transient (network, 5xx) and the connection stays as it was.
/// </summary>
public sealed record OAuthTokenAnswer(
    string? AccessToken,
    string? RefreshToken,
    int? ExpiresIn,
    string? Scope,
    string? IdToken,
    string? Error,
    string? ErrorDescription,
    bool Refused)
{
    public bool Ok => Error is null && !string.IsNullOrEmpty(AccessToken);

    /// <summary>The provider's reason, in one line: <c>invalid_grant: Token has been expired or revoked.</c></summary>
    public string Reason => ErrorDescription is { Length: > 0 } description ? $"{Error}: {description}" : Error ?? "no access token";

    public static OAuthTokenAnswer Transient(string why) => new(null, null, null, null, null, why, null, Refused: false);
}

/// <summary>The real endpoints, over HTTP. Nothing it sends or receives is logged.</summary>
public sealed class HttpOAuthEndpoints(HttpClient http) : IOAuthEndpoints
{
    public Task<OAuthTokenAnswer> ExchangeCodeAsync(
        OAuthProvider provider, string? clientSecret, string code, string redirectUri, string codeVerifier, CancellationToken ct) =>
        TokenAsync(provider, clientSecret, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier,
        }, ct);

    public Task<OAuthTokenAnswer> RefreshAsync(OAuthProvider provider, string? clientSecret, string refreshToken, CancellationToken ct) =>
        TokenAsync(provider, clientSecret, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }, ct);

    public Task<OAuthTokenAnswer> DeviceTokenAsync(OAuthProvider provider, string? clientSecret, string deviceCode, CancellationToken ct) =>
        TokenAsync(provider, clientSecret, new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["device_code"] = deviceCode,
        }, ct);

    public async Task<DeviceAuthorizationAnswer> DeviceAuthorizationAsync(OAuthProvider provider, IReadOnlyList<string> scopes, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(provider.DeviceAuthorizationUrl))
        {
            return new DeviceAuthorizationAnswer(null, null, null, null, null, "the provider has no device authorization endpoint", null);
        }

        var (status, root, unreachable) = await PostAsync(provider.DeviceAuthorizationUrl, new Dictionary<string, string>
        {
            ["client_id"] = provider.ClientId ?? "",
            ["scope"] = string.Join(' ', scopes),
        }, ct);

        if (unreachable is not null) return new DeviceAuthorizationAnswer(null, null, null, null, null, unreachable, null);

        var error = Text(root, "error");
        if (error is not null || status is < 200 or >= 300)
        {
            return new DeviceAuthorizationAnswer(null, null, null, null, null, error ?? $"HTTP {status}", Text(root, "error_description"));
        }

        return new DeviceAuthorizationAnswer(
            Text(root, "device_code"),
            Text(root, "user_code"),
            // RFC 8628 names it verification_uri; some providers still say verification_url.
            Text(root, "verification_uri") ?? Text(root, "verification_url"),
            Seconds(root, "expires_in"),
            Seconds(root, "interval"),
            null, null);
    }

    private async Task<OAuthTokenAnswer> TokenAsync(
        OAuthProvider provider, string? clientSecret, Dictionary<string, string> form, CancellationToken ct)
    {
        form["client_id"] = provider.ClientId ?? "";
        if (!string.IsNullOrEmpty(clientSecret)) form["client_secret"] = clientSecret;

        var (status, root, unreachable) = await PostAsync(provider.TokenUrl, form, ct);
        if (unreachable is not null) return OAuthTokenAnswer.Transient(unreachable);

        var error = Text(root, "error");

        if (error is not null || status is < 200 or >= 300)
        {
            return new OAuthTokenAnswer(
                null, null, null, null, null, error ?? $"HTTP {status}", Text(root, "error_description"),
                Refused: error is not null && status is >= 400 and < 500);
        }

        return new OAuthTokenAnswer(
            Text(root, "access_token"),
            Text(root, "refresh_token"),
            Seconds(root, "expires_in"),
            Text(root, "scope"),
            Text(root, "id_token"),
            null, null, false);
    }

    /// <summary>A form POST answered with a JSON object: its status and body, or why there is none.</summary>
    private async Task<(int Status, JsonElement Root, string? Unreachable)> PostAsync(
        string url, Dictionary<string, string> form, CancellationToken ct)
    {
        HttpResponseMessage response;
        string body;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            response = await http.SendAsync(request, ct);
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (0, default, $"the provider could not be reached ({exception.GetType().Name})");
        }

        using (response)
        {
            var status = (int)response.StatusCode;

            try
            {
                var root = JsonDocument.Parse(body).RootElement.Clone();
                return root.ValueKind == JsonValueKind.Object
                    ? (status, root, null)
                    : (status, default, $"the provider answered {status} with no token");
            }
            catch (JsonException)
            {
                return (status, default, $"the provider answered {status} with no JSON");
            }
        }
    }

    private static int? Seconds(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var seconds) ? seconds
            : value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed) ? parsed
            : null
            : null;

    public async Task<JsonElement?> UserInfoAsync(OAuthProvider provider, string accessToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(provider.UserinfoUrl)) return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, provider.UserinfoUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement.Clone();
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task<string?> RevokeAsync(OAuthProvider provider, string token, CancellationToken ct)
    {
        if (provider.RevokeUrl is null) return "the provider has no revocation endpoint";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, provider.RevokeUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }),
            };
            using var response = await http.SendAsync(request, ct);
            return response.IsSuccessStatusCode ? null : $"the provider answered {(int)response.StatusCode}";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return $"the provider could not be reached ({exception.GetType().Name})";
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    /// <summary>The claims of an ID token, read WITHOUT verifying its signature: it came straight
    /// from the provider's token endpoint over TLS, in answer to this Host's own request, and only
    /// names the account. Null when it is not a JWT.</summary>
    public static JsonElement? IdTokenClaims(string? idToken)
    {
        var parts = idToken?.Split('.');
        if (parts is not { Length: 3 }) return null;

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            return JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload))).RootElement.Clone();
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }
}
