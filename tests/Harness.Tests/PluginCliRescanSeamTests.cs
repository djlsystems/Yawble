using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// THE SEAM BETWEEN THE OPERATOR CLI'S INSTALL AND PLUGIN SKILLS (B0012, card 730 verification):
/// a request through <c>plugins/.rescan</c> - which reaches <see cref="PluginCatalog.Rescan"/> by way
/// of <see cref="PluginRescanRequests"/>, never the rescan route - must also run the followers
/// <c>Program</c> attached: the plugin's skills are registered, and every Manager's roster is
/// re-prompted with what the Host now holds.
/// </summary>
public sealed class PluginCliRescanSeamTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-plugin-seam-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    private string Plugins => PluginInstall.PluginsRoot(_dataRoot);

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(new FakeAgent())));

        _ = _factory.Services;
        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        Assert.Empty(Services.GetRequiredService<PluginCatalog>().Plugins);
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task A_cli_install_registers_the_plugins_skills_and_reprompts_the_roster()
    {
        var store = Services.GetRequiredService<ISkillStore>();

        // INSTALL 0.1.0 THE CLI'S WAY: files, then a nonce. Nothing calls the catalog directly.
        Install("0.1.0", "First description.", ["seam-echo"]);
        using (await RequestAsync("install")) { }

        Assert.Equal("seam-echo", (await store.GetAsync("plugin-seam-echo", Ct))!.Source);

        var team = (await Services.GetRequiredService<TeamRegistry>()
            .CreateAsync("Seam", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;
        using var person = await PersonAsync();
        (await person.PostAsJsonAsync($"/api/teams/{team}/containers", new { name = "Echo", agent = "plugin:seam-echo" }, Ct))
            .EnsureSuccessStatusCode();

        Assert.Contains("Echo (plugin seam-echo: First description - skill plugin-seam-echo)", ManagerPrompt(team));

        // A NEW VERSION, again only through the nonce: a new skill, and a new description the
        // roster must now carry without anyone touching the team.
        Install("0.2.0", "Second description.", ["seam-echo", "extra"]);
        using (await RequestAsync("upgrade")) { }

        Assert.Equal("seam-echo", (await store.GetAsync("plugin-seam-echo-extra", Ct))!.Source);
        var prompt = ManagerPrompt(team);
        Assert.Contains("Echo (plugin seam-echo: Second description - skill plugin-seam-echo)", prompt);
        Assert.DoesNotContain("First description", prompt);

        // REMOVED, through the nonce: its skills go, and the roster says so.
        Directory.Delete(Path.Combine(Plugins, "seam-echo"), recursive: true);
        using (await RequestAsync("remove")) { }

        Assert.Null(await store.GetAsync("plugin-seam-echo", Ct));
        Assert.Null(await store.GetAsync("plugin-seam-echo-extra", Ct));
        Assert.Contains("Echo (plugin seam-echo: not installed)", ManagerPrompt(team));
    }

    private void Install(string version, string description, string[] skills)
    {
        var directory = PluginInstall.Write(_dataRoot, "seam-echo", version, manifest: PluginInstall.Manifest("seam-echo", version, m =>
        {
            m["description"] = description;
            m["skills"] = new JsonArray([.. skills.Select(s => (JsonNode)$"skills/{s}.md")]);
        }));

        Directory.CreateDirectory(Path.Combine(directory, "skills"));
        foreach (var skill in skills)
        {
            File.WriteAllText(Path.Combine(directory, "skills", skill + ".md"), $"---\nname: {skill}\ndescription: How to use {skill}.\n---\nBody.");
        }
    }

    private string ManagerPrompt(string team) =>
        Services.GetRequiredService<ContainerHost>().Find(new ContainerId(team, TeamRegistry.DefaultManagerName))!.SystemPrompt;

    /// <summary>What the CLI does: write the nonce, then read the report until it carries it.</summary>
    private async Task<JsonDocument> RequestAsync(string nonce)
    {
        Directory.CreateDirectory(Plugins);
        await File.WriteAllTextAsync(Path.Combine(Plugins, PluginRescanRequests.RequestFile), nonce + "\n", Ct);

        var report = Path.Combine(Plugins, PluginRescanRequests.ReportFile);
        var deadline = DateTime.UtcNow.AddSeconds(15);

        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(report))
            {
                var document = JsonDocument.Parse(await File.ReadAllTextAsync(report, Ct));
                if (document.RootElement.GetProperty("request").GetString() == nonce) return document;
                document.Dispose();
            }

            await Task.Delay(100, Ct);
        }

        Assert.Fail($"The Host did not answer rescan request '{nonce}' within 15 seconds.");
        return null!;
    }

    private async Task<HttpClient> PersonAsync()
    {
        var person = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).StatusCode);
        return person;
    }
}
