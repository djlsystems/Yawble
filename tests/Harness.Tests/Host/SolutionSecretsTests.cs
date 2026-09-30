using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Solutions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// A PACKAGE BINDS ITS PLUGINS' SECRETS AT INSTALL, by key name: the Job Tracker's Scout is bound to
/// its five keys in the same step it is hired, the preview and the result name each key and whether
/// the Host has it set (never its value), a key for a source the person left off is not needed, and an
/// update keeps a binding the person changed. No value appears in any response, row or file.
/// </summary>
public sealed class SolutionSecretsTests(HostFixture host) : IClassFixture<HostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private T Get<T>() where T : notnull => host.Services.GetRequiredService<T>();

    private string Package(string version = "1.0.0") =>
        SolutionSamples.JobTracker(
            version,
            Path.Combine(Get<TeamDocuments>().EnsureFor(host.Alpha), "packages", Guid.NewGuid().ToString("N")));

    private static string Unique(string prefix) => $"{prefix} {Guid.NewGuid().ToString("N")[..6]}";

    private static readonly string[] FiveKeys = ["ADZUNA_APP_ID", "ADZUNA_APP_KEY", "USAJOBS_API_KEY", "USAJOBS_USER_AGENT", "THEMUSE_API_KEY"];

    private static object Sources(params string[] sources) =>
        new Dictionary<string, object> { ["Scout"] = new Dictionary<string, object> { ["sources"] = sources } };

    private sealed class Recorded(HttpClient client)
    {
        public List<string> Bodies { get; } = [];

        public async Task<JsonElement> PostAsync(string path, object body)
        {
            using var response = await client.PostAsJsonAsync(path, body, Ct);
            var text = await response.Content.ReadAsStringAsync(Ct);
            Bodies.Add(text);
            Assert.True(response.StatusCode == HttpStatusCode.OK, text);
            return JsonDocument.Parse(text).RootElement.Clone();
        }

        public async Task<JsonElement> GetAsync(string path)
        {
            using var response = await client.GetAsync(path, Ct);
            var text = await response.Content.ReadAsStringAsync(Ct);
            Bodies.Add(text);
            Assert.True(response.StatusCode == HttpStatusCode.OK, text);
            return JsonDocument.Parse(text).RootElement.Clone();
        }

        public async Task<JsonElement> PutAsync(string path, object body)
        {
            using var response = await client.PutAsJsonAsync(path, body, Ct);
            var text = await response.Content.ReadAsStringAsync(Ct);
            Bodies.Add(text);
            Assert.True(response.StatusCode == HttpStatusCode.OK, text);
            return JsonDocument.Parse(text).RootElement.Clone();
        }
    }

    private static Dictionary<string, JsonElement> ByKey(JsonElement list) =>
        list.EnumerateArray().ToDictionary(s => s.GetProperty("key").GetString()!, s => s, StringComparer.Ordinal);

    [Fact]
    public async Task Install_binds_the_job_tracker_plugin_member_to_its_five_keys_and_names_each_keys_set_state_never_its_value()
    {
        // Two keys set on the Host, with values distinctive enough to find anywhere they leak.
        var appId = $"adzuna-id-{Guid.NewGuid():N}";
        var appKey = $"adzuna-key-{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable("ADZUNA_APP_ID", appId);
        Environment.SetEnvironmentVariable("ADZUNA_APP_KEY", appKey);

        try
        {
            using var client = await host.PersonAsync();
            var person = new Recorded(client);
            var folder = Package();

            // THE PREVIEW: each key by name, set or not, what it is for and how to set it. Whether it
            // is needed waits on the person's answer to `sources`, which the wizard asks for.
            var preview = await person.PostAsync("/api/solutions/preview", new { folder });
            var planned = ByKey(preview.GetProperty("secrets"));
            Assert.Equal(FiveKeys.Order(StringComparer.Ordinal), planned.Keys.Order(StringComparer.Ordinal));
            Assert.True(planned["ADZUNA_APP_ID"].GetProperty("set").GetBoolean());
            Assert.True(planned["ADZUNA_APP_KEY"].GetProperty("set").GetBoolean());
            Assert.False(planned["USAJOBS_API_KEY"].GetProperty("set").GetBoolean());
            Assert.Equal(JsonValueKind.Null, planned["USAJOBS_API_KEY"].GetProperty("needed").ValueKind);
            Assert.Equal("usajobsApiKey", planned["USAJOBS_API_KEY"].GetProperty("field").GetString());
            Assert.Equal("Scout", planned["USAJOBS_API_KEY"].GetProperty("member").GetString());
            Assert.Contains("developer.usajobs.gov", planned["USAJOBS_API_KEY"].GetProperty("description").GetString());
            Assert.Equal(("sources", "usajobs"),
                (planned["USAJOBS_API_KEY"].GetProperty("when").GetProperty("setting").GetString(),
                 planned["USAJOBS_API_KEY"].GetProperty("when").GetProperty("value").GetString()));
            Assert.Equal("the operator CLI's `secret set USAJOBS_API_KEY` (it prompts for the value), then its `up` to restart the Host",
                planned["USAJOBS_API_KEY"].GetProperty("setWith").GetString());

            // THE INSTALL: Adzuna and USAJOBS ticked, The Muse left off.
            var done = await person.PostAsync("/api/solutions/install", new
            {
                folder,
                teamName = Unique("Secrets"),
                settings = Sources("sample", "adzuna", "usajobs"),
            });
            Assert.True(done.GetProperty("ok").GetBoolean(), done.ToString());
            var team = done.GetProperty("team").GetString()!;

            // BOUND, in the member's own settings, exactly as a person's binding is stored: key names.
            var scout = await Get<IPluginMemberSettingsStore>().ForAsync(new ContainerId(team, "Scout"), Ct);
            Assert.Equal(
                new Dictionary<string, string>
                {
                    ["adzunaAppId"] = "ADZUNA_APP_ID",
                    ["adzunaAppKey"] = "ADZUNA_APP_KEY",
                    ["usajobsApiKey"] = "USAJOBS_API_KEY",
                    ["usajobsUserAgent"] = "USAJOBS_USER_AGENT",
                    ["themuseApiKey"] = "THEMUSE_API_KEY",
                },
                scout.Secrets.ToDictionary(p => p.Key, p => p.Value));
            Assert.Contains("usajobsApiKey", (await Get<ITenantLog>().FindLatestAsync(TenantActions.MemberAdded, $"{team}/Scout", Ct))!.Detail);

            // THE RESULT: set, unset and not needed, and the keys still to set.
            var result = ByKey(done.GetProperty("secrets"));
            Assert.Equal(5, result.Count);
            Assert.True(result["ADZUNA_APP_ID"].GetProperty("set").GetBoolean());
            Assert.True(result["ADZUNA_APP_ID"].GetProperty("needed").GetBoolean());
            Assert.False(result["USAJOBS_USER_AGENT"].GetProperty("set").GetBoolean());
            Assert.True(result["USAJOBS_USER_AGENT"].GetProperty("needed").GetBoolean());
            Assert.False(result["THEMUSE_API_KEY"].GetProperty("needed").GetBoolean());
            Assert.Equal(["USAJOBS_API_KEY", "USAJOBS_USER_AGENT"],
                done.GetProperty("unset").EnumerateArray().Select(k => k.GetString()!));

            // Every route a person reads about this team and member.
            await person.GetAsync($"/api/teams/{team}/members/Scout/plugin-settings");
            await person.GetAsync($"/api/teams/{team}/solution");
            await person.GetAsync("/api/solutions/installed");
            await person.GetAsync("/api/tenant-log");
            await person.GetAsync("/api/diagnostics");

            // NO VALUE in any response...
            foreach (var value in new[] { appId, appKey })
            {
                Assert.DoesNotContain(person.Bodies, body => body.Contains(value, StringComparison.Ordinal));
            }

            // ...nor in any file the Host wrote under its data root: the database and its WAL (the
            // member's settings row, tenant rows, the message log, diagnostics), logs and reports.
            SqliteConnection.ClearAllPools();
            foreach (var file in Directory.EnumerateFiles(host.DataRoot, "*", SearchOption.AllDirectories))
            {
                byte[] bytes;
                try { bytes = await File.ReadAllBytesAsync(file, Ct); }
                catch (IOException) { continue; }

                var text = Encoding.Latin1.GetString(bytes);
                Assert.False(text.Contains(appId, StringComparison.Ordinal) || text.Contains(appKey, StringComparison.Ordinal),
                    $"{Path.GetRelativePath(host.DataRoot, file)} holds a secret's value.");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("ADZUNA_APP_ID", null);
            Environment.SetEnvironmentVariable("ADZUNA_APP_KEY", null);
        }
    }

    [Fact]
    public async Task An_update_keeps_a_binding_the_person_changed_and_takes_the_packages_new_key_where_they_did_not()
    {
        using var client = await host.PersonAsync();
        var person = new Recorded(client);

        var installed = await person.PostAsync("/api/solutions/install", new
        {
            folder = Package(),
            teamName = Unique("Rebind"),
            settings = Sources("sample"),
        });
        var team = installed.GetProperty("team").GetString()!;

        // THE PERSON rebinds USAJOBS_API_KEY in the member's settings.
        var route = $"/api/teams/{team}/members/Scout/plugin-settings";
        var current = await person.GetAsync(route);
        var secrets = current.GetProperty("secrets").Deserialize<Dictionary<string, string>>()!;
        secrets["usajobsApiKey"] = "MY_USAJOBS_KEY";
        await person.PutAsync(route, new { config = current.GetProperty("config"), secrets });

        // 1.1.0 renames two keys: the one the person changed, and one they did not.
        var newer = Package("1.1.0");
        SolutionSamples.Edit(newer, m =>
        {
            m["members"]![1]!["secrets"]!["usajobsApiKey"] = "USAJOBS_KEY_V2";
            m["members"]![1]!["secrets"]!["themuseApiKey"] = "THEMUSE_KEY_V2";
        });

        var preview = ByKey((await person.PostAsync("/api/solutions/preview", new { folder = newer, team })).GetProperty("secrets"));
        Assert.Contains("MY_USAJOBS_KEY", preview.Keys);
        Assert.Contains("THEMUSE_KEY_V2", preview.Keys);
        Assert.DoesNotContain("USAJOBS_KEY_V2", preview.Keys);
        Assert.False(preview["THEMUSE_KEY_V2"].GetProperty("needed").GetBoolean());

        var done = await person.PostAsync("/api/solutions/update", new { folder = newer, team });
        Assert.True(done.GetProperty("ok").GetBoolean(), done.ToString());

        var scout = await Get<IPluginMemberSettingsStore>().ForAsync(new ContainerId(team, "Scout"), Ct);
        Assert.Equal("MY_USAJOBS_KEY", scout.Secrets["usajobsApiKey"]);
        Assert.Equal("THEMUSE_KEY_V2", scout.Secrets["themuseApiKey"]);
        Assert.Equal("ADZUNA_APP_ID", scout.Secrets["adzunaAppId"]);
        Assert.Equal("[\"sample\"]", scout.Config["sources"].GetRawText());

        // Recorded as a person's settings change is: the field's name, never a value.
        var row = (await Get<ITenantLog>().FindLatestAsync(TenantActions.MemberPluginSettingsChanged, $"{team}/Scout", Ct))!;
        var detail = JsonDocument.Parse(row.Detail!).RootElement;
        Assert.Equal(["themuseApiKey"], detail.GetProperty("secrets").EnumerateArray().Select(s => s.GetString()!));
    }

    [Fact]
    public void Merged_secrets_keep_what_the_person_changed_or_unbound()
    {
        SolutionMember Member(Dictionary<string, string> secrets) =>
            new("Scout", MemberRef.PluginKind, SolutionManifest.RoleMember, null, "", "job-board", new Dictionary<string, JsonElement>()) { Secrets = secrets };

        var installed = Member(new() { ["a"] = "A_KEY", ["b"] = "B_KEY", ["c"] = "C_KEY", ["d"] = "D_KEY" });
        var now = Member(new() { ["a"] = "A_V2", ["b"] = "B_V2", ["c"] = "C_V2", ["e"] = "E_KEY" });
        var current = new Dictionary<string, string> { ["a"] = "A_KEY", ["b"] = "PERSONS_KEY", ["d"] = "D_KEY" };

        var merged = SolutionInstaller.MergedSecrets(current, installed, now);

        Assert.Equal(
            new Dictionary<string, string> { ["a"] = "A_V2", ["b"] = "PERSONS_KEY", ["e"] = "E_KEY" },
            merged.ToDictionary(p => p.Key, p => p.Value));
    }
}
