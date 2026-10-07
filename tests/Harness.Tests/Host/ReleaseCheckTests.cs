using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harness.Tests.Host;

/// <summary>
/// THE RELEASE CHECK says whether a newer release is out, what changed, and nothing it does not know.
/// The feed is faked: no test reads the network.
/// </summary>
public sealed class ReleaseCheckTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 10, 6, 20, 0, 0, TimeSpan.Zero);

    private sealed class FakeFeed(string? repository = "owner/name") : IReleaseFeed
    {
        public List<PublishedRelease> Releases { get; } = [];
        public Exception? Failure { get; set; }
        public int Reads { get; private set; }
        public string? Repository { get; } = repository;

        public Task<IReadOnlyList<PublishedRelease>> ListAsync(CancellationToken ct)
        {
            Reads++;
            if (Failure is not null) throw Failure;
            return Task.FromResult<IReadOnlyList<PublishedRelease>>(Releases.ToList());
        }
    }

    private static PublishedRelease Release(string version, bool prerelease = true, string notes = "") =>
        new(version, prerelease, Now, $"https://example.test/releases/v{version}", notes);

    private static ReleaseCheck Check(FakeFeed feed, bool enabled = true) =>
        new(feed, () => enabled, () => Now, NullLogger<ReleaseCheck>.Instance);

    [Fact]
    public async Task A_pre_release_build_is_offered_every_newer_pre_release_newest_first_with_its_notes()
    {
        var feed = new FakeFeed();
        feed.Releases.AddRange([
            Release("2026.10.05.1"), Release("2026.10.06.1"),
            Release("2026.10.08.1", notes: "Tabs everywhere"), Release("2026.10.07.2", notes: "Update notice"),
        ]);
        var check = Check(feed);

        await check.CheckAsync(Ct);
        var status = check.StatusFor("2026.10.06.1");

        Assert.True(status.Checked);
        Assert.True(status.UpdateAvailable);
        Assert.Equal("2026.10.06.1", status.Current);
        Assert.Equal("2026.10.08.1", status.Latest);
        Assert.True(status.LatestIsPrerelease);
        Assert.Equal(["2026.10.08.1", "2026.10.07.2"], status.Newer.Select(r => r.Version));
        Assert.Equal("Tabs everywhere", status.Newer[0].Notes);
        Assert.Equal(Now, status.CheckedAt);
    }

    [Fact]
    public async Task A_regular_release_build_is_offered_regular_releases_only()
    {
        var feed = new FakeFeed();
        feed.Releases.AddRange([Release("2026.11.01.1", prerelease: false), Release("2026.11.05.1"), Release("2026.12.01.1", prerelease: false)]);
        var check = Check(feed);

        await check.CheckAsync(Ct);

        var status = check.StatusFor("2026.11.01.1");
        Assert.Equal("2026.12.01.1", status.Latest);
        Assert.False(status.LatestIsPrerelease);
        Assert.Equal(["2026.12.01.1"], status.Newer.Select(r => r.Version));
    }

    [Fact]
    public async Task A_development_build_is_ahead_of_its_base_release_and_only_a_later_release_is_newer()
    {
        var feed = new FakeFeed();
        feed.Releases.Add(Release("2026.10.06.1"));
        var check = Check(feed);
        await check.CheckAsync(Ct);

        var same = check.StatusFor("2026.10.06.1+19.21a7de8");
        Assert.True(same.Checked);
        Assert.False(same.UpdateAvailable);
        Assert.Equal("2026.10.06.1", same.Current);

        feed.Releases.Add(Release("2026.10.06.2"));
        await check.CheckAsync(Ct);
        Assert.Equal("2026.10.06.2", check.StatusFor("2026.10.06.1+19.21a7de8").Latest);
    }

    [Fact]
    public async Task Nothing_is_guessed_before_a_read_after_a_failed_read_or_without_a_repository()
    {
        var feed = new FakeFeed();
        var check = Check(feed);

        var before = check.StatusFor("2026.10.06.1");
        Assert.False(before.Checked);
        Assert.False(before.UpdateAvailable);
        Assert.Equal("Not checked yet.", before.Detail);

        feed.Failure = new HttpRequestException("Name or service not known");
        await check.CheckAsync(Ct);
        var failed = check.StatusFor("2026.10.06.1");
        Assert.False(failed.Checked);
        Assert.False(failed.UpdateAvailable);
        Assert.Equal("Not checked: Name or service not known", failed.Detail);
        Assert.Equal(Now, failed.CheckedAt);

        var unconfigured = new FakeFeed(repository: null);
        var dev = Check(unconfigured);
        await dev.CheckAsync(Ct);
        Assert.Equal(0, unconfigured.Reads);
        Assert.False(dev.StatusFor("2026.10.06.1").Checked);
        Assert.Contains("not told where its releases are published", dev.StatusFor("2026.10.06.1").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Turned_off_it_reads_nothing_and_says_so()
    {
        var feed = new FakeFeed();
        feed.Releases.Add(Release("2027.01.01.1"));
        var check = Check(feed, enabled: false);

        await check.CheckAsync(Ct);

        Assert.Equal(0, feed.Reads);
        var status = check.StatusFor("2026.10.06.1");
        Assert.False(status.Enabled);
        Assert.False(status.UpdateAvailable);
        Assert.Equal("Checking for updates is turned off in Settings.", status.Detail);
    }

    [Fact]
    public async Task Long_release_notes_are_cut_at_the_bound()
    {
        var feed = new FakeFeed();
        feed.Releases.Add(Release("2027.01.01.1", notes: new string('x', ReleaseCheck.MaxNotes + 50)));
        var check = Check(feed);

        await check.CheckAsync(Ct);

        Assert.Equal(ReleaseCheck.MaxNotes + 1, check.StatusFor("2026.10.06.1").Newer[0].Notes.Length);
    }

    [Theory]
    [InlineData("2026.10.06.1", "2026.10.06.2", -1)]
    [InlineData("2026.10.10.1", "2026.10.9.1", 1)]
    [InlineData("2026.10.06.1+3.abc", "2026.10.06.1", 0)]
    [InlineData("0.0.0+unknown", "2026.10.06.1", -1)]
    public void Versions_order_part_by_part_numerically(string a, string b, int expected) =>
        Assert.Equal(expected, Math.Sign(ReleaseCheck.Compare(a, b)));

    [Fact]
    public async Task A_person_reads_and_checks_through_the_route_and_a_machine_principal_is_refused()
    {
        var dataRoot = Directory.CreateTempSubdirectory("harness-release-check-").FullName;
        var feed = new FakeFeed();
        feed.Releases.Add(Release("9999.01.01.1", notes: "Everything new"));
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.Replace(ServiceDescriptor.Singleton<IReleaseFeed>(feed))));
        try
        {
            await factory.Services.GetRequiredService<IUserStore>().CreateAsync("person@example.test", HostFixture.Password, Ct);
            using var person = factory.CreateClient();
            (await person.PostAsJsonAsync("/api/auth/login", new { email = "person@example.test", password = HostFixture.Password }, Ct))
                .EnsureSuccessStatusCode();

            using var before = JsonDocument.Parse(await person.GetStringAsync(ReleaseCheckEndpoints.Route, Ct));
            Assert.False(before.RootElement.GetProperty("checked").GetBoolean());
            Assert.True(before.RootElement.GetProperty("enabled").GetBoolean());

            var checkedNow = await person.PostAsync(ReleaseCheckEndpoints.Route + "/check", null, Ct);
            checkedNow.EnsureSuccessStatusCode();
            using var after = JsonDocument.Parse(await person.GetStringAsync(ReleaseCheckEndpoints.Route, Ct));
            Assert.True(after.RootElement.GetProperty("updateAvailable").GetBoolean());
            Assert.Equal("9999.01.01.1", after.RootElement.GetProperty("latest").GetString());
            Assert.Equal("Everything new", after.RootElement.GetProperty("newer")[0].GetProperty("notes").GetString());

            var agent = factory.Services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
            var team = await factory.Services.GetRequiredService<TeamRegistry>().CreateAsync("Alpha", agent, memberAgent: agent, ct: Ct);
            var key = await factory.Services.GetRequiredService<IPrincipalStore>().MintAsync(
                new ContainerId(team.Id, "Worker").ToString(), PrincipalKind.Container, team.Id, Permits.All, ct: Ct);
            using var machine = factory.CreateClient();
            machine.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, key);
            foreach (var response in new[]
            {
                await machine.GetAsync(ReleaseCheckEndpoints.Route, Ct),
                await machine.PostAsync(ReleaseCheckEndpoints.Route + "/check", null, Ct),
            })
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
                Assert.Equal(PermitGate.HumansOnlyMessage, body.RootElement.GetProperty("error").GetString());
            }
        }
        finally
        {
            await factory.DisposeAsync();
            try { Directory.Delete(dataRoot, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task The_setting_is_on_by_default_and_listed_with_what_it_does()
    {
        var dataRoot = Directory.CreateTempSubdirectory("harness-release-setting-").FullName;
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning"));
        try
        {
            var settings = factory.Services.GetRequiredService<TenantSettings>();
            Assert.True(settings.UpdatesCheck);
            Assert.Equal("on", settings.Current(TenantSettings.UpdatesCheckName));
        }
        finally
        {
            await factory.DisposeAsync();
            try { Directory.Delete(dataRoot, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
