using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Harness.Host.Solutions;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// <c>POST /api/solutions/check</c> on the real Host: a person and the Concierge are answered with
/// the plan or the refusals, a member is refused whatever it holds, a folder outside the data root is
/// refused, and nothing is written. And the operator's <c>--solution-check</c>, which the CLI runs,
/// prints the route's own body.
/// </summary>
public sealed class SolutionCheckRouteTests(HostFixture host) : IClassFixture<HostFixture>
{
    private const string Route = "/api/solutions/check";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A copy of the sample inside this Host's data root, in a team's documents.</summary>
    private string Package(string version = "1.0.0") =>
        SolutionSamples.JobTracker(
            version,
            Path.Combine(host.Services.GetRequiredService<TeamDocuments>().EnsureFor(host.Alpha), "packages", Guid.NewGuid().ToString("N")));

    private async Task<HttpClient> ConciergeAsync()
    {
        var person = await host.Services.GetRequiredService<IUserStore>().FindAsync("person@example.test", Ct);
        var key = await host.Services.GetRequiredService<IPrincipalStore>().MintAsync(
            ConciergeLaunchFactory.PrincipalId(person!.Id), PrincipalKind.TenantConcierge, null,
            ConciergeLaunchFactory.ConciergePermits, ownerUserId: person.Id, ct: Ct);
        return host.Container(key);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    [Fact]
    public async Task A_person_gets_the_plan_of_a_valid_package()
    {
        using var person = await host.PersonAsync();

        var response = await person.PostAsJsonAsync(Route, new { folder = Package() }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.True(body.GetProperty("ok").GetBoolean(), body.ToString());
        var plan = body.GetProperty("plan");
        Assert.Equal("job-tracker", plan.GetProperty("package").GetProperty("id").GetString());
        Assert.Equal(3, plan.GetProperty("members").GetArrayLength());
        var trigger = plan.GetProperty("triggers")[2];
        Assert.Equal(400000, trigger.GetProperty("dailyTokenCap").GetInt64());
        Assert.Contains("{solution}/make-cover-letter.py", trigger.GetProperty("instruction").GetString());
        Assert.Equal("Resume", plan.GetProperty("inputs").GetProperty("documents")[0].GetProperty("folder").GetString());
    }

    [Fact]
    public async Task The_concierge_gets_the_refusals_of_an_invalid_package_each_naming_file_and_field()
    {
        var folder = Package();
        SolutionSamples.Edit(folder, m => m["triggers"]![1]!["member"] = "Recruiter");
        using var concierge = await ConciergeAsync();

        var response = await concierge.PostAsJsonAsync(Route, new { folder }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("plan").ValueKind);
        var refusal = Assert.Single(body.GetProperty("refusals").EnumerateArray());
        Assert.Equal("solution.json", refusal.GetProperty("file").GetString());
        Assert.Equal("triggers[1].member", refusal.GetProperty("field").GetString());
        Assert.Contains("Recruiter", refusal.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_member_is_refused_whatever_it_holds()
    {
        var folder = Package();

        using var everything = host.Container(host.AlphaContainerKey);
        var refused = await everything.PostAsJsonAsync(Route, new { folder }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(SolutionEndpoints.MemberRefusal, (await JsonAsync(refused)).GetProperty("error").GetString());

        var narrow = await host.Services.GetRequiredService<IPrincipalStore>().MintAsync(
            new ContainerId(host.Alpha, "Reader").ToString(), PrincipalKind.Container, host.Alpha,
            new HashSet<string> { Permits.Read, Permits.Progress, Permits.Sites }, ct: Ct);
        using var reader = host.Container(narrow);
        var withoutPermit = await reader.PostAsJsonAsync(Route, new { folder }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, withoutPermit.StatusCode);
        Assert.Equal(PermitGate.Missing(Permits.CreateTeam), (await JsonAsync(withoutPermit)).GetProperty("error").GetString());

        // A Manager's standing permits do not reach it either.
        Assert.DoesNotContain(Permits.CreateTeam, TeamRegistry.ManagerPermits);
    }

    [Theory]
    [InlineData("relative/job-tracker")]
    [InlineData("/etc")]
    [InlineData("")]
    public async Task A_folder_that_is_not_an_absolute_folder_inside_the_data_root_is_refused(string folder)
    {
        using var person = await host.PersonAsync();

        var response = await person.PostAsJsonAsync(Route, new { folder }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace((await JsonAsync(response)).GetProperty("error").GetString()));
    }

    [Fact]
    public async Task A_folder_reached_through_a_link_leaving_the_data_root_is_refused()
    {
        var outside = SolutionSamples.JobTracker();
        var link = Path.Combine(host.DataRoot, "documents", $"linked-{Guid.NewGuid():N}");
        Directory.CreateSymbolicLink(link, outside);
        using var person = await host.PersonAsync();

        var response = await person.PostAsJsonAsync(Route, new { folder = link }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("link that leaves the data root", (await JsonAsync(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_check_creates_no_team_member_trigger_skill_or_site_and_changes_no_file()
    {
        var folder = Package();
        var teams = host.Services.GetRequiredService<ITeamStore>();
        var membersBefore = (await teams.MembersAsync(Ct)).Count;
        var files = Directory.EnumerateFileSystemEntries(host.DataRoot, "*", SearchOption.AllDirectories)
            .Where(p => !p.Contains("messages.db", StringComparison.Ordinal) && !p.Contains($"{Path.DirectorySeparatorChar}logs", StringComparison.Ordinal))
            .ToDictionary(p => p, File.GetLastWriteTimeUtc);
        using var person = await host.PersonAsync();

        Assert.Equal(HttpStatusCode.OK, (await person.PostAsJsonAsync(Route, new { folder }, Ct)).StatusCode);

        Assert.Equal(membersBefore, (await teams.MembersAsync(Ct)).Count);
        Assert.False(host.Services.GetRequiredService<TeamRegistry>().Exists("JobTracker"));
        Assert.Empty(await host.Services.GetRequiredService<ISiteStore>().ListAsync(host.Alpha, Ct));
        foreach (var (path, written) in files)
        {
            Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        }
    }

    [Fact]
    public async Task The_operator_check_prints_the_routes_body_on_one_line()
    {
        var folder = Package("1.1.0");
        var output = new StringWriter();

        var outcome = await OperatorCommands.TryRunAsync(["--solution-check", folder], host.DataRoot, output, Ct);

        Assert.Equal(OperatorOutcome.Completed, outcome);
        var line = output.ToString().TrimEnd().Split('\n')[^1];
        var body = JsonDocument.Parse(line).RootElement;
        Assert.True(body.GetProperty("ok").GetBoolean(), line);
        Assert.Equal("1.1.0", body.GetProperty("plan").GetProperty("package").GetProperty("version").GetString());

        SolutionSamples.Edit(folder, m => m["members"]![1]!["pluginId"] = "elsewhere");
        output = new StringWriter();
        Assert.Equal(OperatorOutcome.Completed, await OperatorCommands.TryRunAsync(["--solution-check", folder], host.DataRoot, output, Ct));
        var refused = JsonDocument.Parse(output.ToString().TrimEnd().Split('\n')[^1]).RootElement;
        Assert.False(refused.GetProperty("ok").GetBoolean());
        Assert.Equal("members[1].pluginId", refused.GetProperty("refusals")[0].GetProperty("field").GetString());
    }

    [Fact]
    public async Task The_operator_check_without_a_folder_refuses()
    {
        var output = new StringWriter();

        Assert.Equal(OperatorOutcome.Refused, await OperatorCommands.TryRunAsync(["--solution-check"], host.DataRoot, output, Ct));
        Assert.Contains("needs the path of a solution package folder", output.ToString());
    }
}
