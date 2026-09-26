using System.Net.Http.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Tests.Host;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// CAUSATION JOINS A WORKFLOW, AND THE CONCIERGE CAN PASS IT. `PUT /api/me/steering` writes the
/// highlighted workflow; the next Concierge launch copies it to `HARNESS_CAUSATION` and writes it
/// into `STEERING.md`, which is what a Concierge already running can read.
/// </summary>
public sealed class SteeringRouteTests(HostFixture host) : IClassFixture<HostFixture>
{
    [Fact]
    public async Task The_steering_route_records_the_persons_highlighted_workflow()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = await host.PersonAsync();
        var person = await host.Services.GetRequiredService<IUserStore>().FindAsync("person@example.test", ct);
        var dataRoot = host.Services.GetRequiredService<TeamPaths>().DataRoot;

        (await client.PutAsJsonAsync("/api/me/steering", new { correlationId = 42 }, ct)).EnsureSuccessStatusCode();
        Assert.Equal("42", SteeringFile.Read(dataRoot, person!.Id));

        (await client.PutAsJsonAsync("/api/me/steering", new { correlationId = (long?)null }, ct)).EnsureSuccessStatusCode();
        Assert.Null(SteeringFile.Read(dataRoot, person.Id));
    }
}

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SteeringLaunchTests : IDisposable
{
    private readonly string _dataRoot =
        Path.Combine(Path.GetTempPath(), $"harness-steering-{Guid.NewGuid():N}");

    public SteeringLaunchTests() => Directory.CreateDirectory(_dataRoot);

    [Fact]
    public async Task The_next_concierge_launch_carries_the_steered_workflow()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalog = new AgentCatalog(AgentCatalogFile.BuiltIns());
        var agent = catalog.Definitions
            .First(d => d.Mode == AgentMode.Interactive && !d.Hidden && d.Launch.LanguageModel).Name;

        // The launch writes MCP files under HOME; keep them out of the real one.
        using var restore = new EnvironmentScope([new("HOME", _dataRoot)]);

        var factory = new ConciergeLaunchFactory(
            new TeamPaths(_dataRoot), "http://localhost:5000", new MintingPrincipals(), catalog);

        var spec = await factory.ForAsync(
            team: "", teamLabel: "this instance", user: "user-1", login: "person@example.com",
            agent, teamEnv: new Dictionary<string, string>(),
            steeringCausation: "42", ct: ct);

        Assert.Equal("42", spec.Env!["HARNESS_CAUSATION"]);
        Assert.Contains("42", await File.ReadAllTextAsync(Path.Combine(spec.StartingFolder, "STEERING.md"), ct));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }
}
