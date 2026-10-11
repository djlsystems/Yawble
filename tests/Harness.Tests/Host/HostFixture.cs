using System.Net.Http.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// The real Host in memory over its own temp data root, with two teams and the two kinds of caller
/// the gates tell apart: a person signed in with the cookie, and a container holding the credential
/// the platform mints for it. Minted through <see cref="IPrincipalStore"/> exactly as
/// <c>AgentEnvironment</c> does, so the key is resolved on every request by the real auth path.
/// </summary>
public sealed class HostFixture : IAsyncLifetime
{
    public const string Password = "correct horse battery";

    private readonly string _dataRoot =
        Path.Combine(Path.GetTempPath(), $"harness-host-test-{Guid.NewGuid():N}");

    private WebApplicationFactory<Program> _factory = null!;

    public string Alpha { get; private set; } = "";

    public string Beta { get; private set; } = "";

    /// <summary>A container on <see cref="Alpha"/> holding every permit, so only identity bounds it.</summary>
    public string AlphaContainerKey { get; private set; } = "";

    public IServiceProvider Services => _factory.Services;

    /// <summary>The temp data root this Host runs over.</summary>
    public string DataRoot => _dataRoot;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(host => host
                .UseSetting("DataRoot", _dataRoot)
                .UseSetting("Logging:LogLevel:Default", "Warning"));

        var registry = Services.GetRequiredService<TeamRegistry>();
        var agent = Services.GetRequiredService<AgentCatalog>().Definitions
            .First(d => d.Mode == AgentMode.Headless).Name;

        Alpha = (await registry.CreateAsync("Alpha", agent, memberAgent: agent)).Id;
        Beta = (await registry.CreateAsync("Beta", agent, memberAgent: agent)).Id;

        AlphaContainerKey = await Services.GetRequiredService<IPrincipalStore>().MintAsync(
            new ContainerId(Alpha, "Worker").ToString(), PrincipalKind.Container, Alpha, Permits.All);

        await Services.GetRequiredService<IUserStore>()
            .CreateAsync("person@example.test", Password);
    }

    /// <summary>A caller holding nothing: no cookie, no key.</summary>
    public HttpClient Anonymous(bool allowAutoRedirect = true) =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = allowAutoRedirect });

    /// <summary>A handler into this Host's in-memory server, for code that makes its own clients.</summary>
    public HttpMessageHandler ServerHandler() => _factory.Server.CreateHandler();

    /// <summary>The in-memory server itself, for a request whose path must reach the app exactly as
    /// written - an <see cref="HttpClient"/> builds a <see cref="Uri"/>, which removes dot-segments.</summary>
    public Microsoft.AspNetCore.TestHost.TestServer Server => _factory.Server;

    public HttpClient Container(string key)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, key);
        return client;
    }

    public async Task<HttpClient> PersonAsync(bool allowAutoRedirect = true)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = allowAutoRedirect });
        var login = await client.PostAsJsonAsync(
            "/api/auth/login", new { email = "person@example.test", password = Password });

        login.EnsureSuccessStatusCode();
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();

        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
