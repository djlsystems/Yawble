using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// On the real Host, a person installs a built plugin version from a folder already
/// inside the instance (the route, and the operator CLI's <c>.install</c> request file, one
/// <see cref="PluginInstaller"/>), and edits a plugin member's settings after hire. Both are a
/// person's acts: a Manager's credential is refused.
/// </summary>
public sealed class PluginInstallRouteTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";
    private const string TokenKey = "PLUGIN_SETTINGS_ROUTE_TOKEN";
    private const string TokenValue = "a-real-secret-value";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-plugin-install-{Guid.NewGuid():N}");
    private readonly string _outside = Path.Combine(Path.GetTempPath(), $"harness-plugin-outside-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;
    private string _team = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    private string Plugins => PluginInstall.PluginsRoot(_dataRoot);

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(_outside);
        Environment.SetEnvironmentVariable(TokenKey, TokenValue);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(new FakeAgent())));

        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        _team = (await Services.GetRequiredService<TeamRegistry>().CreateAsync("Plugins", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;
    }

    public async ValueTask DisposeAsync()
    {
        Environment.SetEnvironmentVariable(TokenKey, null);
        await _factory.DisposeAsync();
        foreach (var directory in new[] { _dataRoot, _outside })
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>A built version folder as a team leaves one in its worktree: modes as a build left
    /// them (the executable NOT executable, as from Windows), a skill, a nested folder.</summary>
    private string Built(string parent, string id = "built-echo", string version = "0.2.0", Action<JsonObject>? edit = null)
    {
        var folder = Path.Combine(parent, id, "out", version);
        Directory.CreateDirectory(Path.Combine(folder, "bin"));
        Directory.CreateDirectory(Path.Combine(folder, "skills"));

        var manifest = PluginInstall.Manifest(id, version, m =>
        {
            m["skills"] = new JsonArray("skills/built.md");
            m["config"] = JsonNode.Parse("""
                {"mode":{"type":"string","enum":["upper","reverse"],"default":"upper"},
                 "allow":{"type":"list","enum":["a","b"]},
                 "live":{"type":"bool","default":false,"setBy":"person"}}
                """);
            m["secrets"] = JsonNode.Parse("""{"token":{"description":"A demo credential.","required":false}}""");
            edit?.Invoke(m);
        });

        File.WriteAllText(Path.Combine(folder, PluginManifest.FileName), manifest.ToJsonString());
        File.WriteAllText(Path.Combine(folder, "bin", "run"), "#!/bin/sh\ncat >/dev/null; echo '{\"t\":\"result\",\"ok\":true,\"output\":\"ok\"}'\n");
        File.SetUnixFileMode(Path.Combine(folder, "bin", "run"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllText(Path.Combine(folder, "skills", "built.md"), "---\nname: built\ndescription: How to use the built echo.\n---\n\nSend it text.\n");
        return folder;
    }

    private string InWorktree(string id = "built-echo", string version = "0.2.0", Action<JsonObject>? edit = null) =>
        Built(Path.Combine(_dataRoot, "teams", "t", "repos", "R", "wt_x"), id, version, edit);

    private async Task<HttpClient> PersonAsync()
    {
        var person = _factory.CreateClient();
        (await person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();
        return person;
    }

    private async Task<HttpClient> ManagerAsync()
    {
        var key = await Services.GetRequiredService<IPrincipalStore>().MintAsync(
            new ContainerId(_team, TeamRegistry.DefaultManagerName).ToString(), PrincipalKind.Container, _team,
            TeamRegistry.ManagerPermits, ct: Ct);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, key);
        return client;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> InstallAsync(HttpClient client, string path, bool replace = false)
    {
        var response = await client.PostAsJsonAsync("/api/plugins/install", new { path, replace }, Ct);
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>(Ct));
    }

    private async Task<List<TenantEvent>> TenantRowsAsync(string action) =>
        [.. (await Services.GetRequiredService<ITenantLog>().ReadAsync(take: ITenantLog.MaxTake, ct: Ct)).Events.Where(e => e.Action == action)];

    private static UnixFileMode Mode(string path) => File.GetUnixFileMode(path);

    private const UnixFileMode Rwxrx = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
    private const UnixFileMode Rwr = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;

    [DllImport("libc")]
    private static extern uint getegid();

    private static string Stat(string format, string path)
    {
        var start = new System.Diagnostics.ProcessStartInfo("stat", ["-c", format, path]) { RedirectStandardOutput = true };
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return output;
    }

    // ---- B: install ----------------------------------------------------------------------------

    [Fact]
    public async Task A_person_installs_a_built_folder_from_inside_the_data_root_with_the_hosts_modes_and_the_active_switch()
    {
        using var person = await PersonAsync();
        var (status, body) = await InstallAsync(person, InWorktree());

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("installed").GetBoolean(), body.GetRawText());
        Assert.Equal("built-echo", body.GetProperty("id").GetString());
        Assert.Equal("0.2.0", body.GetProperty("version").GetString());
        Assert.False(body.GetProperty("replaced").GetBoolean());

        var target = Path.Combine(Plugins, "built-echo", "0.2.0");
        Assert.Equal("0.2.0\n", await File.ReadAllTextAsync(Path.Combine(Plugins, "built-echo", PluginCatalog.ActiveFile), Ct));
        Assert.Equal(Rwxrx, Mode(target));
        Assert.Equal(Rwxrx, Mode(Path.Combine(target, "bin")));
        Assert.Equal(Rwxrx, Mode(Path.Combine(target, "bin", "run")));
        Assert.Equal(Rwr, Mode(Path.Combine(target, PluginManifest.FileName)));
        Assert.Equal(Rwr, Mode(Path.Combine(target, "skills", "built.md")));
        Assert.Equal(Rwr, Mode(Path.Combine(Plugins, "built-echo", PluginCatalog.ActiveFile)));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(Plugins, "built-echo"), ".*"));

        // Registered at once: hireable, its skill indexed.
        Assert.Equal("0.2.0", Services.GetRequiredService<PluginCatalog>().For("built-echo")!.Manifest.Version);

        var row = Assert.Single(await TenantRowsAsync(TenantActions.PluginInstalled));
        Assert.Equal(Email, row.ActorEmail);
        Assert.Equal("built-echo", row.Subject);
        Assert.Contains("0.2.0", row.Detail);
    }

    [Fact]
    public async Task The_installer_gives_every_file_the_group_it_is_told_and_keeps_the_host_as_owner()
    {
        var group = (int)getegid();
        var catalog = new PluginCatalog(Path.Combine(_outside, "data", "plugins"));
        var installer = new PluginInstaller(catalog, Path.Combine(_outside, "data"), group);

        var result = await installer.InstallAsync(Built(Path.Combine(_outside, "data", "work")), replace: false, Ct);

        Assert.True(result.Installed, result.Reason);
        var target = Path.Combine(catalog.Root, "built-echo", "0.2.0");
        foreach (var path in new[] { target, Path.Combine(target, "bin", "run"), Path.Combine(target, PluginManifest.FileName), Path.Combine(catalog.Root, "built-echo", "active") })
        {
            Assert.Equal($"{Environment.UserName}:{group}", Stat("%U:%g", path));
        }
    }

    [Fact]
    public async Task A_plugin_whose_reads_are_malformed_is_refused_at_install_naming_the_field()
    {
        using var person = await PersonAsync();
        var (status, body) = await InstallAsync(person, InWorktree(edit: m =>
            m["reads"] = JsonNode.Parse("""[{"site":"board","collection":"Items"}]""")));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("`reads[0].collection`: \"Items\" is not a valid collection name.", body.GetProperty("error").GetString());
        Assert.False(body.GetProperty("installed").GetBoolean());
        Assert.False(Directory.Exists(Path.Combine(Plugins, "built-echo")));
    }

    [Fact]
    public async Task A_folder_outside_the_data_root_is_refused_and_nothing_is_written()
    {
        using var person = await PersonAsync();
        var (status, body) = await InstallAsync(person, Built(_outside));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("outside the data root", body.GetProperty("error").GetString());
        Assert.False(body.GetProperty("installed").GetBoolean());
        Assert.False(Directory.Exists(Path.Combine(Plugins, "built-echo")));
        Assert.Empty(await TenantRowsAsync(TenantActions.PluginInstalled));
    }

    [Fact]
    public async Task A_path_through_a_symlink_leaving_the_data_root_is_refused()
    {
        var real = Built(_outside);
        var link = Path.Combine(_dataRoot, "teams", "link-out");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        Directory.CreateSymbolicLink(link, Path.GetDirectoryName(Path.GetDirectoryName(real))!);

        using var person = await PersonAsync();
        var (status, body) = await InstallAsync(person, Path.Combine(link, "out", "0.2.0"));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("symlink", body.GetProperty("error").GetString());
        Assert.False(Directory.Exists(Path.Combine(Plugins, "built-echo")));
    }

    [Fact]
    public async Task A_symlink_inside_the_folder_leading_out_of_it_is_refused()
    {
        var folder = InWorktree();
        File.CreateSymbolicLink(Path.Combine(folder, "bin", "helper"), Path.Combine("..", "..", "..", "..", "..", "secret.txt"));

        using var person = await PersonAsync();
        var (status, body) = await InstallAsync(person, folder);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("bin/helper", body.GetProperty("error").GetString());
        Assert.False(Directory.Exists(Path.Combine(Plugins, "built-echo")));
    }

    [Fact]
    public async Task An_existing_version_is_refused_without_replace_and_replaced_with_it()
    {
        using var person = await PersonAsync();
        var folder = InWorktree();
        Assert.Equal(HttpStatusCode.OK, (await InstallAsync(person, folder)).Status);

        await File.WriteAllTextAsync(Path.Combine(folder, "NEW"), "second build", Ct);

        var (refused, body) = await InstallAsync(person, folder);
        Assert.Equal(HttpStatusCode.Conflict, refused);
        Assert.Contains("already installed", body.GetProperty("error").GetString());
        Assert.False(File.Exists(Path.Combine(Plugins, "built-echo", "0.2.0", "NEW")));

        var (replaced, again) = await InstallAsync(person, folder, replace: true);
        Assert.Equal(HttpStatusCode.OK, replaced);
        Assert.True(again.GetProperty("replaced").GetBoolean());
        Assert.True(File.Exists(Path.Combine(Plugins, "built-echo", "0.2.0", "NEW")));
        Assert.Equal(2, (await TenantRowsAsync(TenantActions.PluginInstalled)).Count);
    }

    [Fact]
    public async Task A_manifest_the_catalog_refuses_is_refused_with_its_reason_before_anything_is_written()
    {
        using var person = await PersonAsync();

        var (badProtocol, body) = await InstallAsync(person, InWorktree(edit: m => m["protocol"] = "other/1"));
        Assert.Equal(HttpStatusCode.BadRequest, badProtocol);
        Assert.Contains("`protocol`", body.GetProperty("error").GetString());

        var (unknownRuntime, second) = await InstallAsync(person, InWorktree("other-echo", edit: m => m["requires"] = new JsonArray("ruby")));
        Assert.Equal(HttpStatusCode.BadRequest, unknownRuntime);
        Assert.Contains("'ruby'", second.GetProperty("error").GetString());

        var missingSkill = InWorktree("third-echo");
        File.Delete(Path.Combine(missingSkill, "skills", "built.md"));
        var (noSkill, third) = await InstallAsync(person, missingSkill);
        Assert.Equal(HttpStatusCode.BadRequest, noSkill);
        Assert.Contains("skills/built.md", third.GetProperty("error").GetString());

        Assert.False(Directory.Exists(Plugins) && Directory.EnumerateFileSystemEntries(Plugins).Any(e => !Path.GetFileName(e).StartsWith('.')));
        Assert.Empty(await TenantRowsAsync(TenantActions.PluginInstalled));
    }

    [Fact]
    public async Task A_manager_is_refused_the_install_and_the_manifest()
    {
        PluginInstall.Write(_dataRoot, "listed-echo");
        await Services.GetRequiredService<PluginCatalog>().RescanAsync(Ct);

        using var manager = await ManagerAsync();

        var install = await manager.PostAsJsonAsync("/api/plugins/install", new { path = InWorktree() }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, install.StatusCode);
        Assert.Equal(PermitGate.HumansOnlyMessage, (await install.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
        Assert.False(Directory.Exists(Path.Combine(Plugins, "built-echo")));

        var manifest = await manager.GetAsync("/api/plugins/listed-echo/0.1.0/manifest", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, manifest.StatusCode);
    }

    [Fact]
    public async Task The_operator_cli_installs_through_the_request_file_with_the_same_checks_and_verdict()
    {
        Directory.CreateDirectory(Plugins);

        // What the CLI does: write the request, then wait for the Host's own service to answer it.
        // Never a second PluginRescanRequests: the Host's polls the same file once a second, so two
        // would both answer one request, and the slower one's report overwrites a later answer.
        async Task<JsonElement> AskAsync(string nonce, string path, bool replace)
        {
            var request = Path.Combine(Plugins, PluginRescanRequests.InstallRequestFile);
            var report = Path.Combine(Plugins, PluginRescanRequests.InstallReportFile);
            await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new { request = nonce, path, replace }), Ct);

            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                if (!File.Exists(request) && File.Exists(report))
                {
                    var answer = JsonDocument.Parse(await File.ReadAllTextAsync(report, Ct)).RootElement.Clone();
                    if (answer.GetProperty("request").GetString() == nonce)
                    {
                        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, Mode(report));
                        return answer;
                    }
                }

                await Task.Delay(50, Ct);
            }

            Assert.Fail($"The Host did not answer install request '{nonce}' within 15 seconds.");
            return default;
        }

        var folder = InWorktree();

        var installed = await AskAsync("one", folder, replace: false);
        Assert.Equal(200, installed.GetProperty("status").GetInt32());
        Assert.True(installed.GetProperty("installed").GetBoolean());
        Assert.NotNull(Services.GetRequiredService<PluginCatalog>().For("built-echo"));

        var exists = await AskAsync("two", folder, replace: false);
        Assert.Equal(409, exists.GetProperty("status").GetInt32());

        var forced = await AskAsync("three", folder, replace: true);
        Assert.Equal(200, forced.GetProperty("status").GetInt32());
        Assert.True(forced.GetProperty("replaced").GetBoolean());

        var outside = await AskAsync("four", Built(_outside), replace: false);
        Assert.Equal(400, outside.GetProperty("status").GetInt32());
        Assert.Contains("outside the data root", outside.GetProperty("reason").GetString());

        var refused = await AskAsync("five", InWorktree("bad-echo", edit: m => m["schemaVersion"] = 9), replace: false);
        Assert.Equal(400, refused.GetProperty("status").GetInt32());
        Assert.Contains("`schemaVersion`", refused.GetProperty("reason").GetString());

        var rows = await TenantRowsAsync(TenantActions.PluginInstalled);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Contains("\"operator\"", row.Detail));
    }

    // ---- A: the listing ------------------------------------------------------------------------

    [Fact]
    public async Task The_listing_carries_every_version_its_verdict_the_settings_declarations_and_the_members()
    {
        using var person = await PersonAsync();
        Assert.Equal(HttpStatusCode.OK, (await InstallAsync(person, InWorktree(version: "0.1.0"))).Status);
        Assert.Equal(HttpStatusCode.OK, (await InstallAsync(person, InWorktree(version: "0.2.0"))).Status);
        PluginInstall.Write(_dataRoot, "broken-echo", manifest: PluginInstall.Manifest("broken-echo", edit: m => m["protocol"] = "nope/1"));
        await Services.GetRequiredService<PluginCatalog>().RescanAsync(Ct);
        await HireAsync(person, "Echo");

        using var listed = JsonDocument.Parse(await person.GetStringAsync("/api/plugins", Ct));
        var root = listed.RootElement;

        var plugin = root.GetProperty("plugins").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "built-echo");
        Assert.True(plugin.GetProperty("active").GetBoolean());
        Assert.Equal("installed", plugin.GetProperty("verdict").GetString());
        Assert.Equal("person", plugin.GetProperty("config").GetProperty("live").GetProperty("setBy").GetString());
        Assert.Equal("list", plugin.GetProperty("config").GetProperty("allow").GetProperty("type").GetString());
        Assert.Equal("[]", plugin.GetProperty("config").GetProperty("allow").GetProperty("default").GetRawText());
        Assert.Equal("plugin-built-echo-built", plugin.GetProperty("skill").GetString());
        var member = Assert.Single(plugin.GetProperty("members").EnumerateArray());
        Assert.Equal(_team, member.GetProperty("team").GetString());
        Assert.Equal("Echo", member.GetProperty("member").GetString());

        var versions = root.GetProperty("versions").EnumerateArray().ToList();
        Assert.Contains(versions, v => v.GetProperty("id").GetString() == "built-echo" && v.GetProperty("version").GetString() == "0.1.0" && v.GetProperty("verdict").GetString() == "inactive");
        Assert.Contains(versions, v => v.GetProperty("id").GetString() == "built-echo" && v.GetProperty("version").GetString() == "0.2.0" && v.GetProperty("verdict").GetString() == "installed" && v.GetProperty("active").GetBoolean());
        var broken = versions.Single(v => v.GetProperty("id").GetString() == "broken-echo");
        Assert.Equal("refused", broken.GetProperty("verdict").GetString());
        Assert.Contains("`protocol`", broken.GetProperty("reason").GetString());

        Assert.DoesNotContain(_dataRoot, root.GetRawText(), StringComparison.Ordinal);

        // A machine caller with Read sees no member of any team.
        using var manager = await ManagerAsync();
        using var asManager = JsonDocument.Parse(await manager.GetStringAsync("/api/plugins", Ct));
        Assert.Empty(asManager.RootElement.GetProperty("plugins").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "built-echo").GetProperty("members").EnumerateArray());
    }

    [Fact]
    public async Task The_manifest_route_answers_the_raw_text_of_any_version()
    {
        using var person = await PersonAsync();
        var folder = InWorktree();
        Assert.Equal(HttpStatusCode.OK, (await InstallAsync(person, folder)).Status);

        var response = await person.GetAsync("/api/plugins/built-echo/0.2.0/manifest", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(await File.ReadAllTextAsync(Path.Combine(folder, PluginManifest.FileName), Ct), await response.Content.ReadAsStringAsync(Ct));

        Assert.Equal(HttpStatusCode.NotFound, (await person.GetAsync("/api/plugins/built-echo/9.9.9/manifest", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await person.GetAsync("/api/plugins/built-echo/..%2F..%2Fx/manifest", Ct)).StatusCode);
    }

    // ---- C: settings after hire ----------------------------------------------------------------

    private async Task HireAsync(HttpClient person, string name)
    {
        var hired = await person.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name,
            agent = "plugin:built-echo",
            config = new { mode = "reverse" },
            secrets = new { token = TokenKey },
        }, Ct);
        Assert.True(hired.IsSuccessStatusCode, await hired.Content.ReadAsStringAsync(Ct));
    }

    private string SettingsRoute(string member) => $"/api/teams/{_team}/members/{member}/plugin-settings";

    [Fact]
    public async Task A_person_reads_a_plugin_members_settings_with_key_names_and_never_a_value()
    {
        using var person = await PersonAsync();
        Assert.Equal(HttpStatusCode.OK, (await InstallAsync(person, InWorktree())).Status);
        await HireAsync(person, "Echo");

        var text = await person.GetStringAsync(SettingsRoute("Echo"), Ct);
        using var read = JsonDocument.Parse(text);

        Assert.Equal("built-echo", read.RootElement.GetProperty("plugin").GetString());
        Assert.Equal("reverse", read.RootElement.GetProperty("config").GetProperty("mode").GetString());
        Assert.Equal(TokenKey, read.RootElement.GetProperty("secrets").GetProperty("token").GetString());
        Assert.Equal("list", read.RootElement.GetProperty("fields").GetProperty("allow").GetProperty("type").GetString());
        Assert.True(read.RootElement.GetProperty("secretFields").TryGetProperty("token", out _));
        Assert.DoesNotContain(TokenValue, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_change_is_validated_like_a_hire_and_nothing_is_written_when_it_is_refused()
    {
        using var person = await PersonAsync();
        Assert.Equal(HttpStatusCode.OK, (await InstallAsync(person, InWorktree())).Status);
        await HireAsync(person, "Echo");

        async Task<string> RefusedAsync(object body)
        {
            var response = await person.PutAsJsonAsync(SettingsRoute("Echo"), body, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString()!;
        }

        Assert.Contains("`mode` must be one of", await RefusedAsync(new { config = new { mode = "sideways" } }));
        Assert.Contains("`mode` must be a string", await RefusedAsync(new { config = new { mode = 3 } }));
        Assert.Contains("'c'", await RefusedAsync(new { config = new { allow = new[] { "a", "c" } } }));
        Assert.Contains("`colour` is not a configuration field", await RefusedAsync(new { config = new { colour = "red" } }));
        Assert.Contains("`password` is not a secret", await RefusedAsync(new { secrets = new { password = TokenKey } }));
        Assert.Contains("not a secret key", await RefusedAsync(new { secrets = new { token = "lower-case" } }));
        Assert.Contains("platform's own", await RefusedAsync(new { secrets = new { token = "HARNESS_KEY" } }));

        var stored = await Services.GetRequiredService<IPluginMemberSettingsStore>().ForAsync(new ContainerId(_team, "Echo"), Ct);
        Assert.Equal("reverse", stored.Config["mode"].GetString());
        Assert.Empty(await TenantRowsAsync(TenantActions.MemberPluginSettingsChanged));
    }

    [Fact]
    public async Task A_valid_change_is_saved_with_its_tenant_row_and_read_by_the_next_run()
    {
        using var person = await PersonAsync();
        Assert.Equal(HttpStatusCode.OK, (await InstallAsync(person, InWorktree())).Status);
        await HireAsync(person, "Echo");

        var response = await person.PutAsJsonAsync(SettingsRoute("Echo"), new
        {
            config = new { allow = new[] { "a", "b" }, live = true },
            secrets = new { },
        }, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(TokenValue, text, StringComparison.Ordinal);

        var stored = await Services.GetRequiredService<IPluginMemberSettingsStore>().ForAsync(new ContainerId(_team, "Echo"), Ct);
        Assert.False(stored.Config.ContainsKey("mode"));
        Assert.Equal("""["a","b"]""", stored.Config["allow"].GetRawText());
        Assert.True(stored.Config["live"].GetBoolean());
        Assert.Empty(stored.Secrets);

        var row = Assert.Single(await TenantRowsAsync(TenantActions.MemberPluginSettingsChanged));
        Assert.Equal(Email, row.ActorEmail);
        Assert.Equal($"{_team}/Echo", row.Subject);
        using var detail = JsonDocument.Parse(row.Detail!);
        Assert.Equal(["allow", "live", "mode"], detail.RootElement.GetProperty("config").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["token"], detail.RootElement.GetProperty("secrets").EnumerateArray().Select(e => e.GetString()));
        Assert.DoesNotContain(TokenKey, row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_manager_is_refused_reading_and_changing_a_plugin_members_settings()
    {
        using var person = await PersonAsync();
        Assert.Equal(HttpStatusCode.OK, (await InstallAsync(person, InWorktree())).Status);
        await HireAsync(person, "Echo");

        using var manager = await ManagerAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync(SettingsRoute("Echo"), Ct)).StatusCode);

        var put = await manager.PutAsJsonAsync(SettingsRoute("Echo"), new { config = new { live = true } }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        Assert.Equal(PermitGate.HumansOnlyMessage, (await put.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());

        var stored = await Services.GetRequiredService<IPluginMemberSettingsStore>().ForAsync(new ContainerId(_team, "Echo"), Ct);
        Assert.False(stored.Config.ContainsKey("live"));
    }

    [Fact]
    public async Task An_agent_member_and_an_unknown_member_are_answered_by_name()
    {
        using var person = await PersonAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await person.GetAsync(SettingsRoute("Nobody"), Ct)).StatusCode);
        var manager = await person.GetAsync(SettingsRoute(TeamRegistry.DefaultManagerName), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, manager.StatusCode);
        Assert.Contains("not a plugin", await manager.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_person_reads_whether_a_key_is_set_by_its_name_and_never_its_value()
    {
        using var person = await PersonAsync();

        var set = await person.GetAsync($"/api/secrets/{TokenKey}", Ct);
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        var text = await set.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(TokenValue, text);
        var body = JsonDocument.Parse(text).RootElement;
        Assert.Equal(TokenKey, body.GetProperty("key").GetString());
        Assert.True(body.GetProperty("set").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("refusal").ValueKind);
        Assert.Contains($"secret set {TokenKey}", body.GetProperty("setWith").GetString());

        var unset = await person.GetFromJsonAsync<JsonElement>("/api/secrets/PLUGIN_SETTINGS_ROUTE_NEVER_SET", Ct);
        Assert.False(unset.GetProperty("set").GetBoolean());
        Assert.Contains("secret set PLUGIN_SETTINGS_ROUTE_NEVER_SET", unset.GetProperty("setWith").GetString());
    }

    [Fact]
    public async Task A_key_no_plugin_may_be_pointed_at_answers_its_refusal_and_not_set()
    {
        using var person = await PersonAsync();

        var platform = await person.GetFromJsonAsync<JsonElement>("/api/secrets/HARNESS_URL", Ct);
        Assert.False(platform.GetProperty("set").GetBoolean());
        Assert.Contains("platform's own", platform.GetProperty("refusal").GetString());

        var lower = await person.GetFromJsonAsync<JsonElement>("/api/secrets/token", Ct);
        Assert.False(lower.GetProperty("set").GetBoolean());
        Assert.Contains("not a secret key", lower.GetProperty("refusal").GetString());
    }

    [Fact]
    public async Task A_manager_is_refused_reading_whether_a_key_is_set()
    {
        using var manager = await ManagerAsync();

        var response = await manager.GetAsync($"/api/secrets/{TokenKey}", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain(TokenValue, await response.Content.ReadAsStringAsync(Ct));
    }
}
