using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// A member asks its own team's plugin member for something - a signed-in session, a page checked -
/// and the plugin answers the member that asked. So a member may `tell` a plugin member of its own
/// team without the Tell permit, and without being sent to the Manager. Telling an agent member still
/// needs Tell, and another team's plugin is as far away as before.
/// </summary>
public sealed class MemberTellsPluginTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";

    /// <summary>What a member hired today holds: Progress, Skills, Read and Sites - never Tell.</summary>
    private static readonly HashSet<string> MemberPermits =
        new(StringComparer.Ordinal) { Permits.Progress, Permits.Skills, Permits.Read, Permits.Sites };

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-member-tells-plugin-{Guid.NewGuid():N}");
    private readonly FakeAgent _agents = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;
    private string _team = "";
    private string _other = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        PluginMemberEndToEndTests.InstallSampleEcho(_dataRoot);
        _agents.Behaviour = invocation => Task.FromResult(new AgentResult(0, $"noted by {invocation.Container.Name}"));

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(_agents)));

        var registry = Services.GetRequiredService<TeamRegistry>();
        _team = (await registry.CreateAsync("Probe", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;
        _other = (await registry.CreateAsync("Other", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        _person = _factory.CreateClient();
        (await _person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();

        foreach (var team in new[] { _team, _other })
        {
            Assert.Equal(HttpStatusCode.OK, (await _person.PostAsJsonAsync($"/api/teams/{team}/containers", new
            {
                name = "Echo", agent = "plugin:sample-echo", config = new { mode = "upper" },
            }, Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await _person.PostAsJsonAsync(
                $"/api/teams/{team}/containers", new { name = "Dev", agent = "claude-headless" }, Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await _person.PostAsJsonAsync(
                $"/api/teams/{team}/containers", new { name = "Tester", agent = "claude-headless" }, Ct)).StatusCode);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _person.Dispose();
        await _factory.DisposeAsync();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    private async Task<HttpClient> MemberAsync(string team, string member)
    {
        var key = await Services.GetRequiredService<IPrincipalStore>().MintAsync(
            new ContainerId(team, member).ToString(), PrincipalKind.Container, team, MemberPermits, ct: Ct);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }

    private static async Task<(HttpStatusCode Status, string Body)> TellAsync(HttpClient client, string team, string member, string instruction)
    {
        var response = await client.PostAsJsonAsync($"/api/teams/{team}/containers/{member}/tell", new { instruction }, Ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<Message> AwaitRowAsync(string type, Func<Message, bool> match, string what)
    {
        var log = Services.GetRequiredService<IMessageLog>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if ((await log.ReadAfterAsync(0, [type], int.MaxValue, Ct)).FirstOrDefault(match) is { } row) return row;
            await Task.Delay(50, Ct);
        }

        throw new TimeoutException($"No row: {what}.");
    }

    [Fact]
    public async Task A_member_without_tell_tells_its_own_teams_plugin_and_the_instruction_names_it_and_runs()
    {
        using var dev = await MemberAsync(_team, "Dev");

        var (status, body) = await TellAsync(dev, _team, "Echo", "hello from dev");

        Assert.True(status == HttpStatusCode.OK, body);
        var echo = new ContainerId(_team, "Echo");
        var instruction = await AwaitRowAsync(MessageTypes.InstructionFor(echo), _ => true, "the instruction to Echo");
        Assert.Equal(new ContainerId(_team, "Dev").ToString(), instruction.Source);
        await AwaitRowAsync(MessageTypes.Completed, m => m.Source == echo.ToString(), "Echo's run");
    }

    [Fact]
    public async Task A_member_without_tell_is_refused_telling_an_agent_member_or_the_manager_naming_the_permit()
    {
        using var dev = await MemberAsync(_team, "Dev");

        foreach (var target in new[] { "Tester", TeamRegistry.DefaultManagerName })
        {
            var (status, body) = await TellAsync(dev, _team, target, "do it");

            Assert.Equal(HttpStatusCode.Forbidden, status);
            Assert.Equal(PermitGate.Missing(Permits.Tell), JsonDocument.Parse(body).RootElement.GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task A_member_cannot_tell_another_teams_plugin()
    {
        using var dev = await MemberAsync(_other, "Dev");

        var (status, body) = await TellAsync(dev, _team, "Echo", "hello from elsewhere");

        Assert.NotEqual(HttpStatusCode.OK, status);
        Assert.Empty(await Services.GetRequiredService<IMessageLog>().ReadAfterAsync(
            0, [MessageTypes.InstructionFor(new ContainerId(_team, "Echo"))], int.MaxValue, Ct));
        Assert.DoesNotContain("Echo", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_member_holding_tell_is_still_sent_to_the_manager_for_an_agent_member_but_not_for_a_plugin()
    {
        var key = await Services.GetRequiredService<IPrincipalStore>().MintAsync(
            new ContainerId(_team, "Tester").ToString(), PrincipalKind.Container, _team,
            new HashSet<string>(MemberPermits, StringComparer.Ordinal) { Permits.Tell }, ct: Ct);
        using var tester = _factory.CreateClient();
        tester.DefaultRequestHeaders.Add("X-Api-Key", key);

        var (toDev, devBody) = await TellAsync(tester, _team, "Dev", "do it");
        Assert.Equal(HttpStatusCode.Forbidden, toDev);
        Assert.Contains(TeamRegistry.DefaultManagerName, devBody, StringComparison.Ordinal);

        var (toEcho, echoBody) = await TellAsync(tester, _team, "Echo", "hello");
        Assert.True(toEcho == HttpStatusCode.OK, echoBody);
    }
}
