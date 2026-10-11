using System.Net;
using System.Text;
using System.Web;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// WHAT THE REAL ENDPOINTS PUT ON THE WIRE, over a stubbed transport - no provider is called. A
/// public client (no secret stored) sends no <c>client_secret</c> on the code exchange, the refresh
/// or a device-code poll; the device flow asks the tenant's device endpoint and polls with the
/// device-code grant.
/// </summary>
public sealed class HttpOAuthEndpointsTests
{
    private const string ClientId = "8f3c2a71-0b6e-4d2a-9c11-5e7f4a2b9d03";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static OAuthProvider Microsoft(string tenant = "organizations") =>
        ConnectionProviders.Resolve(ConnectionProviders.Microsoft, new StoredProvider(
            ConnectionProviders.Microsoft, ConnectionProviders.Microsoft, null, ClientId, false, tenant, null, null, null, []));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task A_public_client_sends_no_client_secret_on_exchange_refresh_or_device_poll(string? secret)
    {
        var wire = new Wire("""{"access_token":"at-stub","refresh_token":"rt-stub","expires_in":3600}""");
        var endpoints = new HttpOAuthEndpoints(new HttpClient(wire));
        var provider = Microsoft();

        Assert.True((await endpoints.ExchangeCodeAsync(provider, secret, "code-stub", "http://127.0.0.1:1/cb", "verifier-stub", Ct)).Ok);
        Assert.True((await endpoints.RefreshAsync(provider, secret, "rt-old", Ct)).Ok);
        Assert.True((await endpoints.DeviceTokenAsync(provider, secret, "dc-stub", Ct)).Ok);

        Assert.Equal(3, wire.Sent.Count);
        Assert.All(wire.Sent, s =>
        {
            Assert.Equal("https://login.microsoftonline.com/organizations/oauth2/v2.0/token", s.Url);
            Assert.Null(s.Form["client_secret"]);
            Assert.Equal(ClientId, s.Form["client_id"]);
        });
        Assert.Equal(["authorization_code", "refresh_token", "urn:ietf:params:oauth:grant-type:device_code"], wire.Sent.Select(s => s.Form["grant_type"]));
        Assert.Equal("dc-stub", wire.Sent[2].Form["device_code"]);
    }

    [Fact]
    public async Task A_confidential_client_still_sends_its_secret()
    {
        var wire = new Wire("""{"access_token":"at-stub"}""");
        await new HttpOAuthEndpoints(new HttpClient(wire)).RefreshAsync(Microsoft(), "stub-secret", "rt-old", Ct);

        Assert.Equal("stub-secret", Assert.Single(wire.Sent).Form["client_secret"]);
    }

    [Fact]
    public async Task The_device_authorization_asks_the_tenants_device_endpoint_with_the_client_id_and_scopes()
    {
        var wire = new Wire("""{"device_code":"dc-stub","user_code":"UC-STUB","verification_uri":"https://microsoft.com/devicelogin","expires_in":900,"interval":5}""");
        var answer = await new HttpOAuthEndpoints(new HttpClient(wire)).DeviceAuthorizationAsync(
            Microsoft("3b9e1c55-7a20-4f61-8d4e-2c6a9f0b1e77"), ["openid", "offline_access", "https://graph.microsoft.com/Mail.Read"], Ct);

        Assert.True(answer.Ok);
        Assert.Equal(("UC-STUB", "https://microsoft.com/devicelogin", 900, 5), (answer.UserCode, answer.VerificationUri, answer.ExpiresIn, answer.Interval));

        var sent = Assert.Single(wire.Sent);
        Assert.Equal("https://login.microsoftonline.com/3b9e1c55-7a20-4f61-8d4e-2c6a9f0b1e77/oauth2/v2.0/devicecode", sent.Url);
        Assert.Equal(ClientId, sent.Form["client_id"]);
        Assert.Equal("openid offline_access https://graph.microsoft.com/Mail.Read", sent.Form["scope"]);
        Assert.Null(sent.Form["client_secret"]);
    }

    [Theory]
    [InlineData("authorization_pending")]
    [InlineData("slow_down")]
    [InlineData("access_denied")]
    [InlineData("expired_token")]
    public async Task A_device_poll_answers_the_providers_error_as_it_is(string error)
    {
        var wire = new Wire($$"""{"error":"{{error}}","error_description":"stub"}""", HttpStatusCode.BadRequest);
        var answer = await new HttpOAuthEndpoints(new HttpClient(wire)).DeviceTokenAsync(Microsoft(), null, "dc-stub", Ct);

        Assert.False(answer.Ok);
        Assert.Equal(error, answer.Error);
    }

    private sealed class Wire(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public readonly List<(string Url, System.Collections.Specialized.NameValueCollection Form)> Sent = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Sent.Add((request.RequestUri!.ToString(), HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(ct))));
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
