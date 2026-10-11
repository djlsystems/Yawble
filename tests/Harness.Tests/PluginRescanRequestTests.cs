using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// THE OPERATOR CLI'S INSTALL, AS THE HOST SEES IT (section F, <see cref="PluginRescanRequests"/>):
/// the CLI lays a version out under <c>plugins/</c> and writes a nonce to <c>plugins/.rescan</c>;
/// the running Host rescans and answers in <c>plugins/.rescan-report.json</c>. No restart, no
/// person's key.
/// </summary>
public sealed class PluginRescanRequestTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-plugin-rescan-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Plugins => PluginInstall.PluginsRoot(_dataRoot);

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(new FakeAgent())));

        // Started, with nothing installed.
        _ = _factory.Services;
        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        Assert.Empty(Services.GetRequiredService<PluginCatalog>().Plugins);
    }

    private IServiceProvider Services => _factory.Services;

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task A_plugin_installed_after_start_is_listed_once_the_operator_asks_with_no_restart()
    {
        PluginInstall.Write(_dataRoot, id: "operator-echo");

        using var report = await RequestAsync("first");

        var plugin = Assert.Single(report.RootElement.GetProperty("plugins").EnumerateArray());
        Assert.Equal("operator-echo", plugin.GetProperty("id").GetString());
        Assert.Equal("0.1.0", plugin.GetProperty("version").GetString());
        Assert.False(File.Exists(Path.Combine(Plugins, PluginRescanRequests.RequestFile)), "the answered request is left");
        if (!OperatingSystem.IsWindows())
        {
            // It names members of every team, and agent can read the plugins directory.
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(Plugins, PluginRescanRequests.ReportFile)));
        }

        // The catalog the whole Host reads is the new one: listed, and hireable.
        using var person = await PersonAsync();
        using var listed = JsonDocument.Parse(await person.GetStringAsync("/api/plugins", Ct));
        Assert.Contains(listed.RootElement.GetProperty("plugins").EnumerateArray(), p => p.GetProperty("id").GetString() == "operator-echo");
        Assert.Null(Services.GetRequiredService<PluginCatalog>().RefusalFor("operator-echo"));
    }

    [Fact]
    public async Task A_refused_plugin_is_answered_with_the_Hosts_reason()
    {
        var version = PluginInstall.Write(_dataRoot, id: "no-exec");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path.Combine(version, "bin", "run"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        using var report = await RequestAsync("refused");

        Assert.Empty(report.RootElement.GetProperty("plugins").EnumerateArray());
        var refused = Assert.Single(report.RootElement.GetProperty("refused").EnumerateArray());
        Assert.Equal("no-exec", refused.GetProperty("id").GetString());
        if (!OperatingSystem.IsWindows()) Assert.Contains("is not executable", refused.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task The_report_names_every_member_hired_on_each_plugin()
    {
        PluginInstall.Write(_dataRoot, id: "hired-echo");
        using (await RequestAsync("install")) { }

        var team = (await Services.GetRequiredService<TeamRegistry>()
            .CreateAsync("Plugged", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;
        using var person = await PersonAsync();
        var hired = await person.PostAsJsonAsync($"/api/teams/{team}/containers", new { name = "Echo", agent = "plugin:hired-echo" }, Ct);
        Assert.Equal(HttpStatusCode.OK, hired.StatusCode);

        using var report = await RequestAsync("members");

        var member = Assert.Single(report.RootElement.GetProperty("members").EnumerateArray());
        Assert.Equal("hired-echo", member.GetProperty("plugin").GetString());
        Assert.Equal("Plugged", member.GetProperty("team").GetString());
        Assert.Equal("Echo", member.GetProperty("member").GetString());
    }

    [Fact]
    public async Task A_request_already_answered_is_not_answered_again()
    {
        using (await RequestAsync("once")) { }
        var answered = File.GetLastWriteTimeUtc(Path.Combine(Plugins, PluginRescanRequests.ReportFile));

        var service = Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<PluginRescanRequests>().Single();
        await File.WriteAllTextAsync(Path.Combine(Plugins, PluginRescanRequests.RequestFile), "once\n", Ct);

        Assert.False(await service.AnswerAsync(Ct));
        Assert.Equal(answered, File.GetLastWriteTimeUtc(Path.Combine(Plugins, PluginRescanRequests.ReportFile)));
    }

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
        (await person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();
        return person;
    }
}
