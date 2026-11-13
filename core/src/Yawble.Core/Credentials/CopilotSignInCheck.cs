using System.Net;
using System.Net.Http.Headers;

namespace Yawble.Core.Credentials;

/// <summary>
/// Whether GitHub still accepts Copilot CLI's stored token (one GET of https://api.github.com/user, from the host, the
/// call Copilot itself makes first). A token GitHub rejects is reported before a run is spent on it: the person signs
/// in again in their own terminal. A network problem is not a rejection (the run then decides).
/// </summary>
public static class CopilotSignInCheck
{
    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public const string Expired =
        "GitHub Copilot's sign-in on this computer has expired or was revoked (GitHub answered 401). Run 'copilot' in your own terminal and sign in again with /login.";

    /// <summary>True only when GitHub answered 401 for this token.</summary>
    public static async Task<bool> IsRejectedAsync(string token, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
            request.Headers.Authorization = new AuthenticationHeaderValue("token", token);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("yawble", CoreInfo.Version));
            using var response = await s_http.SendAsync(request, ct);
            return response.StatusCode == HttpStatusCode.Unauthorized;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}
