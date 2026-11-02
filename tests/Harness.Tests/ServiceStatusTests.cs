using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Link;
using Harness.Tests.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// THE DROPDOWN'S MANAGEMENT LINE, as <c>GET /api/service-status</c> carries it: null until the
/// instance is attached, then the link client's own view - its state, since when, the service's and
/// the instance's names, the last report and a reason - and never the service's address or the
/// credential, sealed or not.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class ServiceStatusTests
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";
    private const string Credential = "link-credential-5c9e";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Service_status_has_no_management_until_attached()
    {
        var root = Directory.CreateTempSubdirectory("harness-service-status-").FullName;
        try
        {
            await using var factory = Factory(root);
            using var person = await PersonAsync(factory);

            var status = await person.GetFromJsonAsync<JsonElement>(ServiceStatusEndpoints.Route, Ct);

            Assert.True(status.TryGetProperty("management", out var management), "the answer has no management property");
            Assert.Equal(JsonValueKind.Null, management.ValueKind);
        }
        finally
        {
            Remove(root);
        }
    }

    [Fact]
    public async Task Service_status_carries_the_links_state_and_never_the_credential()
    {
        var root = Directory.CreateTempSubdirectory("harness-service-status-").FullName;
        try
        {
            var closed = ClosedLoopbackUrl();
            string sealedCredential;

            // One host stores a link aimed at a port nothing listens on...
            await using (var seed = Factory(root))
            {
                var protection = seed.Services.GetRequiredService<IDataProtectionProvider>();
                sealedCredential = LinkCredential.Protect(protection, Credential);
                var now = DateTimeOffset.UtcNow;
                await seed.Services.GetRequiredService<ILinkStore>().SaveAsync(new StoredLink(
                    closed, "Manage", "office", sealedCredential, now, LinkState.Connecting, now, null, null), Ct);
            }

            // ...and the next one reads it, fails to reach the service and says so in the dropdown.
            await using var factory = Factory(root);
            var client = factory.Services.GetRequiredService<LinkClient>();
            using var person = await PersonAsync(factory);

            string raw = "";
            JsonElement management = default;
            await Until(async () =>
            {
                raw = await person.GetStringAsync(ServiceStatusEndpoints.Route, Ct);
                management = JsonDocument.Parse(raw).RootElement.GetProperty("management");
                return management.ValueKind == JsonValueKind.Object && management.GetProperty("state").GetString() == "retrying";
            });

            var view = client.Current!;
            Assert.Equal("Manage", management.GetProperty("serviceName").GetString());
            Assert.Equal("office", management.GetProperty("instanceName").GetString());
            Assert.Equal(JsonValueKind.Null, management.GetProperty("lastReportAt").ValueKind);
            Assert.True(management.TryGetProperty("since", out var since));
            Assert.True(management.TryGetProperty("reason", out _));
            Assert.Equal(
                ["instanceName", "lastReportAt", "reason", "serviceName", "since", "state"],
                management.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            Assert.True(since.GetDateTimeOffset() <= DateTimeOffset.UtcNow);
            Assert.Equal(LinkState.Retrying, view.State);

            // Neither the credential, nor its sealed form, nor the service's address is anywhere in the answer.
            Assert.DoesNotContain(Credential, raw, StringComparison.Ordinal);
            Assert.DoesNotContain(sealedCredential, raw, StringComparison.Ordinal);
            Assert.DoesNotContain(closed, raw, StringComparison.Ordinal);
            Assert.DoesNotContain(new Uri(closed).Authority, raw, StringComparison.Ordinal);
        }
        finally
        {
            Remove(root);
        }
    }

    private static WebApplicationFactory<Program> Factory(string root) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("DataRoot", root)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .UseSetting("ScheduleRunnerEnabled", "false"));

    private static async Task<HttpClient> PersonAsync(WebApplicationFactory<Program> factory)
    {
        var users = factory.Services.GetRequiredService<IUserStore>();
        if (await users.FindAsync(Email, Ct) is null) await users.CreateAsync(Email, Password);

        var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();
        return client;
    }

    private static string ClosedLoopbackUrl()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return $"http://127.0.0.1:{port}";
    }

    private static async Task Until(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition did not come true in time.");
            await Task.Delay(50, Ct);
        }
    }

    private static void Remove(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
