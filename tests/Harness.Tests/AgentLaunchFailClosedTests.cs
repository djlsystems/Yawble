using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// FAIL CLOSED. When the <c>agent</c> user exists and is somebody else but the Host
/// cannot switch to it, nothing that would run agent-written code as the Host starts: no member,
/// no Concierge, no host git. Each fake leaves a marker if it runs. With no <c>agent</c> user, or
/// with the Host already being it, everything runs as the Host.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class AgentLaunchFailClosedTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("harness-failclosed-").FullName;
    private string Marker => Path.Combine(_root, "ran");

    /// <summary>The agent user exists (uid 1001), the Host is 1000 and holds no capability.</summary>
    private static AgentLaunchUser Stranded() =>
        AgentLaunchUser.Decide("agent", (1001, 1001), 1000, 0, "/usr/bin/setpriv", _ => true);

    private static AgentLaunchUser NoAgentUser() =>
        AgentLaunchUser.Decide("agent", null, 1000, 0, "/usr/bin/setpriv", _ => true);

    private static AgentLaunchUser HostIsAgent() =>
        AgentLaunchUser.Decide("agent", (1000, 1000), 1000, 0, "/usr/bin/setpriv", _ => true);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Only_an_existing_agent_user_the_host_cannot_reach_refuses()
    {
        const ulong caps = (1UL << 6) | (1UL << 7);
        Assert.True(AgentLaunchUser.Decide("agent", (1001, 1001), 1000, 0, "/usr/bin/setpriv", _ => true).Refuses);
        Assert.True(AgentLaunchUser.Decide("agent", (1001, 1001), 1000, caps, null, _ => true).Refuses);
        Assert.True(AgentLaunchUser.Decide("agent", (1001, 1001), 1000, caps, "/usr/bin/setpriv", _ => false).Refuses);

        Assert.False(NoAgentUser().Refuses);
        Assert.False(HostIsAgent().Refuses);
        Assert.False(AgentLaunchUser.Decide("agent", (1001, 1001), 1000, caps, "/usr/bin/setpriv", _ => true).Refuses);

        Assert.Equal("REFUSED", Stranded().Mode);
        Assert.Equal("same user as the Host", NoAgentUser().Mode);
        Assert.Contains("lacks CAP_SETUID", Stranded().Refusal("This member"));
    }

    private AgentCatalog MarkerCatalog() => new(
    [
        new AgentDefinition("probe", AgentMode.Headless,
            new AgentLaunch("/bin/sh", ["-c", $"echo ran > '{Marker}'; echo started"], LanguageModel: false)),
    ]);

    private Task<AgentResult> RunMemberAsync(AgentLaunchUser runAs)
    {
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);

        return new ProcessAgentRunner(MarkerCatalog(), new RunHeartbeat(), runAs: runAs).RunAsync(new AgentInvocation(
            new ContainerId("alpha", "worker"), "You are a probe.", "hello", workspace,
            new Dictionary<string, string>(), Agent: "probe"), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_member_is_refused_with_the_reason_and_nothing_is_started()
    {
        var runAs = Stranded();

        var result = await RunMemberAsync(runAs);

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains(runAs.Reason, result.LaunchError);
        Assert.Contains("was not started", result.LaunchError);
        Assert.False(File.Exists(Marker), "the member's program ran as the Host");
    }

    [Fact]
    public async Task A_member_still_runs_when_there_is_no_agent_user_or_the_host_is_it()
    {
        foreach (var runAs in new[] { NoAgentUser(), HostIsAgent() })
        {
            File.Delete(Marker);

            var result = await RunMemberAsync(runAs);

            Assert.Contains("started", result.Output);
            Assert.True(File.Exists(Marker), $"the member did not run ({runAs.Reason})");
        }
    }

    /// <summary>A git that is used as given (an absolute GitExecutable) and leaves the marker.</summary>
    private string FakeGit()
    {
        var git = Path.Combine(_root, "git");
        File.WriteAllText(git, $"#!/bin/sh\necho ran >> '{Marker}'\nexit 0\n");
        File.SetUnixFileMode(git, (UnixFileMode)0b111_101_101);
        return git;
    }

    [Fact]
    public async Task Host_git_is_refused_with_the_reason_and_git_is_not_started()
    {
        var runAs = Stranded();
        var git = new GitRunner(FakeGit(), runAs: runAs);
        var ct = TestContext.Current.CancellationToken;

        var status = await git.RunGitAsync(_root, ["status"], ct);
        var fetch = await git.FetchAsync(_root, ct);

        Assert.Equal(126, status.ExitCode);
        Assert.Contains(runAs.Reason, status.Stderr);
        Assert.Contains("Host git was not started", status.Stderr);
        Assert.Equal(126, fetch.ExitCode);
        Assert.False(File.Exists(Marker), "git ran as the Host");
    }

    [Fact]
    public async Task Host_git_still_runs_when_there_is_no_agent_user_or_the_host_is_it()
    {
        foreach (var runAs in new[] { NoAgentUser(), HostIsAgent() })
        {
            File.Delete(Marker);

            var result = await new GitRunner(FakeGit(), runAs: runAs)
                .RunGitAsync(_root, ["status"], TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(Marker), $"git did not run ({runAs.Reason})");
        }
    }

    [Fact]
    public async Task The_concierge_is_refused_with_the_reason()
    {
        var runAs = Stranded();
        var catalog = new AgentCatalog(AgentCatalogFile.BuiltIns());
        var agent = catalog.Definitions
            .First(d => d.Mode == AgentMode.Interactive && !d.Hidden && d.Launch.LanguageModel).Name;
        using var restore = new EnvironmentScope([new("HOME", _root)]);

        var factory = new ConciergeLaunchFactory(
            new TeamPaths(_root), "http://localhost:5000", new MintingPrincipals(), catalog, runAs: runAs);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => factory.ForAsync(
            team: "", teamLabel: "this instance", user: "user-1", login: "person@example.com", agent: agent,
            teamEnv: new Dictionary<string, string>(), ct: TestContext.Current.CancellationToken));

        Assert.Contains(runAs.Reason, refused.Message);
    }

    [Fact]
    public async Task The_doctor_reports_the_hosts_recorded_decision_and_its_reason()
    {
        var runAs = Stranded();

        AgentLaunchRecord.Write(_root, runAs);
        var report = await HostDoctor.ReportAsync(_root, TestContext.Current.CancellationToken);

        Assert.NotNull(report.AgentLaunch);
        Assert.Equal("REFUSED", report.AgentLaunch.Mode);
        Assert.True(report.AgentLaunch.Refuses);
        Assert.Equal(runAs.Reason, report.AgentLaunch.Reason);
        Assert.Contains("\"agentLaunch\":{", HostDoctor.ToJson(report));
    }

    [Fact]
    public async Task The_doctor_says_nothing_was_recorded_when_the_host_never_started()
    {
        var report = await HostDoctor.ReportAsync(_root, TestContext.Current.CancellationToken);

        Assert.Null(report.AgentLaunch);
    }
}
