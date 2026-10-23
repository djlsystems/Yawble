using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Solutions;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// AN INSTALLED SOLUTION TEAM IS NEVER LEFT ON A PLUGIN OLDER THAN IT NEEDS. A fixture package ships
/// its plugin at 2.3.1 and needs 2.3.0 or newer (<c>pluginMinimums</c>); with its team installed,
/// making 2.2.0 active - a new install, a Replace, the kept 2.2.0 installed again, through the route
/// and the operator CLI's request file - is refused naming the team, its package and the minimum, and
/// nothing changes. 2.4.0 is accepted, and once the package is uninstalled 2.2.0 is too. Fixture
/// packages only: no published package is named.
/// </summary>
public sealed class PluginMinimumTests(HostFixture host) : IClassFixture<HostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SolutionActor Person = new("person-id", "person@example.test");

    private T Get<T>() where T : notnull => host.Services.GetRequiredService<T>();

    private string Plugins => PluginInstall.PluginsRoot(host.DataRoot);

    private static string NewPluginId() => $"fixture-mail-{Guid.NewGuid().ToString("N")[..6]}";

    /// <summary>A built version of <paramref name="id"/> inside the data root, as a team's build leaves it.</summary>
    private string Built(string id, string version, string under)
    {
        var folder = Path.Combine(under, id, version);
        Directory.CreateDirectory(Path.Combine(folder, "bin"));
        File.WriteAllText(Path.Combine(folder, PluginManifest.FileName), PluginInstall.Manifest(id, version).ToJsonString());
        var run = Path.Combine(folder, "bin", "run");
        File.WriteAllText(run, "#!/bin/sh\ncat >/dev/null; echo '{\"t\":\"result\",\"ok\":true,\"output\":\"ok\"}'\n");
        File.SetUnixFileMode(run, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return folder;
    }

    private string Builds() => Path.Combine(host.DataRoot, "teams", "builds", Guid.NewGuid().ToString("N"));

    /// <summary>The fixture package: one plugin member on <paramref name="id"/>, shipped at
    /// <paramref name="shipped"/>, needing <paramref name="minimum"/> (none: the shipped version).</summary>
    private string Package(string id, string shipped, string? minimum, string version = "1.0.0")
    {
        var folder = Path.Combine(Builds(), "fixture-mail-pack");
        var plugin = Path.Combine(folder, "plugins", id);
        Directory.CreateDirectory(Path.GetDirectoryName(plugin)!);
        Directory.Move(Built(id, shipped, Builds()), plugin);

        var manifest = new JsonObject
        {
            ["format"] = 1,
            ["id"] = id + "-pack",
            ["name"] = "Fixture Mail",
            ["version"] = version,
            ["description"] = "A fixture package that needs a plugin version.",
            ["team"] = new JsonObject { ["name"] = "Inbox", ["instructions"] = "Keep the inbox." },
            ["members"] = new JsonArray(new JsonObject { ["name"] = "Mailer", ["pluginId"] = id }),
        };

        if (minimum is not null) manifest["pluginMinimums"] = new JsonObject { [id] = minimum };
        File.WriteAllText(Path.Combine(folder, SolutionManifest.FileName), manifest.ToJsonString());
        return folder;
    }

    private async Task<SolutionDone> InstallSolutionAsync(string folder)
    {
        var outcome = await Get<SolutionInstaller>().InstallAsync(
            new SolutionInstallRequest(folder, $"Inbox {Guid.NewGuid().ToString("N")[..6]}"), Person, Ct);
        return outcome.Body as SolutionDone ?? throw new Xunit.Sdk.XunitException(JsonSerializer.Serialize(outcome.Body));
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> InstallAsync(HttpClient person, string path, bool replace = false)
    {
        var response = await person.PostAsJsonAsync("/api/plugins/install", new { path, replace }, Ct);
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>(Ct));
    }

    /// <summary>What the operator CLI's <c>plugin install --from-instance</c> does: the request file,
    /// answered by the Host's own <see cref="PluginRescanRequests"/>.</summary>
    private async Task<JsonElement> AskAsync(string path, bool replace)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var request = Path.Combine(Plugins, PluginRescanRequests.InstallRequestFile);
        var report = Path.Combine(Plugins, PluginRescanRequests.InstallReportFile);
        Directory.CreateDirectory(Plugins);
        await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new { request = nonce, path, replace }), Ct);

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (!File.Exists(request) && File.Exists(report))
            {
                var answer = JsonDocument.Parse(await File.ReadAllTextAsync(report, Ct)).RootElement.Clone();
                if (answer.GetProperty("request").GetString() == nonce) return answer;
            }

            await Task.Delay(50, Ct);
        }

        Assert.Fail($"The Host did not answer install request '{nonce}' within 15 seconds.");
        return default;
    }

    private string? Active(string id) => Get<PluginCatalog>().For(id)?.Manifest.Version;

    private string[] VersionFolders(string id) =>
        [.. Directory.EnumerateDirectories(Path.Combine(Plugins, id)).Select(Path.GetFileName).Where(v => !v!.StartsWith('.')).Order()!];

    [Fact]
    public async Task A_plugin_version_older_than_an_installed_solution_team_needs_is_refused_naming_the_team_and_a_newer_one_is_not()
    {
        var id = NewPluginId();
        var builds = Builds();
        var old = Built(id, "2.2.0", builds);
        using var person = await host.PersonAsync();

        // 2.2.0 IS ACTIVE BEFORE THE PACKAGE: nothing needs more yet.
        Assert.Equal(HttpStatusCode.OK, (await InstallAsync(person, old)).Status);
        Assert.Equal("2.2.0", Active(id));

        // THE PLAN SAYS WHAT THE INSTALL DOES: the active 2.2.0 is older than the team needs.
        var package = Package(id, "2.3.1", "2.3.0");
        var planned = Assert.Single(Get<SolutionService>().Check(package).Check!.Plan!.Plugins);
        Assert.Equal("2.3.0", planned.Minimum);
        Assert.Equal($"{id} 2.2.0 is active here, older than the 2.3.0 this team needs: 2.3.1 is installed and made active.", planned.OnInstall);

        var done = await InstallSolutionAsync(package);
        Assert.Equal("2.3.1", Active(id));
        Assert.Equal("2.3.0", (await Get<ITeamSolutionStore>().FindAsync(done.Team, Ct))!.PluginMinimums[id]);

        var sentence = $"{id} 2.2.0 is older than what Fixture Mail 1.0.0 on team {done.TeamName} needs (2.3.0 or newer).";
        File.WriteAllText(Path.Combine(old, "SECOND-BUILD"), "changed");
        var before = VersionFolders(id);

        // THE KEPT 2.2.0, installed again without and with Replace; a new 2.2.5; and the operator CLI's
        // request file: each refused with the one sentence, and nothing changes.
        foreach (var (path, replace) in new[] { (old, false), (old, true), (Built(id, "2.2.5", builds), false) })
        {
            var (status, body) = await InstallAsync(person, path, replace);
            Assert.Equal(HttpStatusCode.Conflict, status);
            Assert.Equal(sentence.Replace("2.2.0", Path.GetFileName(path)), body.GetProperty("error").GetString());
            Assert.False(body.GetProperty("installed").GetBoolean());

            var asked = await AskAsync(path, replace);
            Assert.Equal(409, asked.GetProperty("status").GetInt32());
            Assert.Equal(sentence.Replace("2.2.0", Path.GetFileName(path)), asked.GetProperty("reason").GetString());

            Assert.Equal("2.3.1", Active(id));
            Assert.Equal("2.3.1\n", await File.ReadAllTextAsync(Path.Combine(Plugins, id, PluginCatalog.ActiveFile), Ct));
            Assert.Equal(before, VersionFolders(id));
            Assert.False(File.Exists(Path.Combine(Plugins, id, "2.2.0", "SECOND-BUILD")));
        }

        // THE INSTALLED TAB'S PLUGIN LIST names the team and the minimum it needs.
        var listed = (await person.GetFromJsonAsync<JsonElement>("/api/plugins", Ct)).GetProperty("plugins")
            .EnumerateArray().Single(p => p.GetProperty("id").GetString() == id);
        var need = Assert.Single(listed.GetProperty("solutions").EnumerateArray());
        Assert.Equal(
            (done.Team, done.TeamName, id + "-pack", "Fixture Mail", "1.0.0", "2.3.0"),
            (need.GetProperty("team").GetString(), need.GetProperty("teamName").GetString(), need.GetProperty("packageId").GetString(),
             need.GetProperty("package").GetString(), need.GetProperty("version").GetString(), need.GetProperty("minimum").GetString()));

        // A NEWER VERSION IS NEVER REFUSED by this rule.
        var (newer, newerBody) = await InstallAsync(person, Built(id, "2.4.0", builds));
        Assert.Equal(HttpStatusCode.OK, newer);
        Assert.True(newerBody.GetProperty("installed").GetBoolean());
        Assert.Equal("2.4.0", Active(id));

        // UNINSTALLED, the kept record needs nothing: 2.2.0 is accepted.
        var uninstall = await person.PostAsJsonAsync($"/api/teams/{done.Team}/solution/uninstall", new { }, Ct);
        Assert.True(uninstall.IsSuccessStatusCode, await uninstall.Content.ReadAsStringAsync(Ct));
        Assert.NotNull(await Get<ITeamSolutionStore>().FindUninstalledAsync(done.Team, Ct));

        var (accepted, acceptedBody) = await InstallAsync(person, old, replace: true);
        Assert.Equal(HttpStatusCode.OK, accepted);
        Assert.True(acceptedBody.GetProperty("installed").GetBoolean());
        Assert.Equal("2.2.0", Active(id));
    }

    [Fact]
    public async Task Without_plugin_minimums_the_team_needs_the_shipped_version_and_an_update_rewrites_what_it_needs()
    {
        var id = NewPluginId();
        using var person = await host.PersonAsync();

        var done = await InstallSolutionAsync(Package(id, "2.3.1", minimum: null));
        Assert.Equal("2.3.1", (await Get<ITeamSolutionStore>().FindAsync(done.Team, Ct))!.MinimumFor(id));

        var (refused, body) = await InstallAsync(person, Built(id, "2.3.0", Builds()));
        Assert.Equal(HttpStatusCode.Conflict, refused);
        Assert.Equal($"{id} 2.3.0 is older than what Fixture Mail 1.0.0 on team {done.TeamName} needs (2.3.1 or newer).", body.GetProperty("error").GetString());

        // 1.1.0 ships 2.3.1 still and needs only 2.2.0: the update keeps the plugin and lowers the minimum.
        var update = await Get<SolutionInstaller>().UpdateAsync(
            new SolutionUpdateRequest(Package(id, "2.3.1", "2.2.0", version: "1.1.0"), done.Team), Person, Ct);
        Assert.True(update.Ok, JsonSerializer.Serialize(update.Body));
        Assert.Equal("2.2.0", (await Get<ITeamSolutionStore>().FindAsync(done.Team, Ct))!.MinimumFor(id));
        Assert.Equal("2.3.1", Active(id));

        var (accepted, _) = await InstallAsync(person, Built(id, "2.2.0", Builds()));
        Assert.Equal(HttpStatusCode.OK, accepted);
        Assert.Equal("2.2.0", Active(id));
    }
}
