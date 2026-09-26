using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// FAILED sign-ins are counted per account, and after ten in fifteen minutes the
/// next attempt answers 429 with a sentence the sign-in page shows. Successful sign-ins are never
/// counted - one person signs in from several devices. Each test signs in as its own account, so
/// the counts of one never reach another through the shared host.
/// </summary>
public sealed class LoginThrottleTests(HostFixture host) : IClassFixture<HostFixture>
{
    private const string Password = "the right password";

    [Fact]
    public async Task Eleven_wrong_passwords_for_one_account_in_a_minute_get_a_429()
    {
        var ct = TestContext.Current.CancellationToken;
        var email = await NewAccountAsync(ct);
        using var client = host.Anonymous();

        for (var attempt = 1; attempt <= LoginThrottle.PerAccountLimit; attempt++)
        {
            var wrong = await LoginAsync(client, email, "not it", ct);
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        var eleventh = await LoginAsync(client, email, "not it", ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, eleventh.StatusCode);
        Assert.True(eleventh.Headers.RetryAfter?.Delta > TimeSpan.Zero);

        using var body = JsonDocument.Parse(await eleventh.Content.ReadAsStringAsync(ct));
        Assert.StartsWith("Too many wrong passwords", body.RootElement.GetProperty("error").GetString());

        // The cool-down refuses the RIGHT password too, or a 429 would only ever mark wrong guesses.
        var right = await LoginAsync(client, email, Password, ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, right.StatusCode);
    }

    [Fact]
    public async Task The_right_password_from_three_devices_is_never_limited()
    {
        var ct = TestContext.Current.CancellationToken;
        var email = await NewAccountAsync(ct);
        HttpClient[] devices = [host.Anonymous(), host.Anonymous(), host.Anonymous()];

        try
        {
            for (var round = 0; round < LoginThrottle.PerAccountLimit + 5; round++)
            {
                foreach (var device in devices)
                {
                    var response = await LoginAsync(device, email, Password, ct);
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                }
            }
        }
        finally
        {
            foreach (var device in devices) device.Dispose();
        }
    }

    [Fact]
    public async Task One_locked_account_does_not_lock_another_behind_the_same_address()
    {
        var ct = TestContext.Current.CancellationToken;
        var targeted = await NewAccountAsync(ct);
        var bystander = await NewAccountAsync(ct);
        using var client = host.Anonymous();

        for (var attempt = 0; attempt <= LoginThrottle.PerAccountLimit; attempt++)
        {
            await LoginAsync(client, targeted, "not it", ct);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginAsync(client, targeted, Password, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, bystander, Password, ct)).StatusCode);
    }

    [Fact]
    public async Task A_success_clears_the_accounts_count()
    {
        var ct = TestContext.Current.CancellationToken;
        var email = await NewAccountAsync(ct);
        using var client = host.Anonymous();

        for (var attempt = 1; attempt < LoginThrottle.PerAccountLimit; attempt++)
        {
            await LoginAsync(client, email, "not it", ct);
        }

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, email, Password, ct)).StatusCode);

        for (var attempt = 1; attempt <= LoginThrottle.PerAccountLimit; attempt++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, email, "not it", ct)).StatusCode);
        }
    }

    [Fact]
    public void The_per_address_bound_is_looser_than_the_per_account_one()
    {
        // Behind the tunnel every visitor shares one address, so the address bound must never be
        // the one an ordinary person trips first.
        Assert.True(LoginThrottle.PerAddressLimit >= 5 * LoginThrottle.PerAccountLimit);
    }

    private async Task<string> NewAccountAsync(CancellationToken ct)
    {
        var email = $"throttle-{Guid.NewGuid():N}@example.test";
        await host.Services.GetRequiredService<IUserStore>().CreateAsync(email, Password, ct);
        return email;
    }

    private static Task<HttpResponseMessage> LoginAsync(
        HttpClient client, string email, string password, CancellationToken ct) =>
        client.PostAsJsonAsync("/api/auth/login", new { email, password }, ct);
}
