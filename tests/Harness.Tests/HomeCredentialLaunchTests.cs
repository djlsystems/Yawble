using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// A HOME RUN LOSES WHAT OTHER COMMANDS DECLARE. Each built-in headless preset, on the shared home,
/// through the real launch path (<see cref="HomeLaunchBed"/>) while the Host holds every variable
/// any preset's issued-credential declaration names: its child receives none of another command's,
/// keeps its own, and keeps whatever the team or the catalog entry handed in by name.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class HomeCredentialLaunchTests
{
    public static TheoryData<string> HeadlessPresets() => HomeLaunchBed.HeadlessPresets();

    [Theory]
    [MemberData(nameof(HeadlessPresets))]
    public async Task A_home_run_of_each_built_in_headless_preset_receives_no_other_commands_declared_variable_and_keeps_its_own(
        string preset)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The fake CLIs are shell scripts.");

        using var bed = new HomeLaunchBed();
        var definition = bed.Definition(preset);
        var others = bed.OtherCommandsDeclare(preset);
        Assert.NotEmpty(others);

        // The team hands its members nothing of these: what a team's runs inherit is the Host's.
        var container = await bed.ContainerEnvironmentAsync(preset);
        Assert.Empty(container.Keys.Intersect(others));

        var seen = await bed.RunAsync(preset);

        Assert.Empty(seen.Keys.Intersect(others));

        var own = HomeLaunchBed.Declared(definition).ToList();
        if (AgentEnvironment.ProviderVariableFor(RunCredentials.CommandOf(definition)) is { } variable) own.Add(variable);
        Assert.All(own, name => Assert.Equal(HomeLaunchBed.HostValue(name), seen.GetValueOrDefault(name)));

        // The team's git token, as for any member of a team with a GitHub remote, and the team's own env.
        Assert.Equal(HomeLaunchBed.HostValue(AgentEnvironment.GitHubVariable), seen[AgentEnvironment.GitHubVariable]);
        Assert.Equal("team", seen["TEAM_VAR"]);

        // The older provider list still applies: a provider key no built-in command uses never arrives.
        Assert.Empty(seen.Keys
            .Intersect(AgentEnvironment.ProviderVariables)
            .Except(own)
            .Except([AgentEnvironment.GitHubVariable, "GITHUB_TOKEN"]));

        // Home never opens the store.
        Assert.False(File.Exists(bed.Database));
    }

    [Fact]
    public async Task A_team_env_or_catalog_entry_naming_another_commands_variable_is_kept_on_home()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The fake CLIs are shell scripts.");

        var preset = HomeLaunchBed.HeadlessPresets().First().Data;
        string[] names;
        using (var probe = new HomeLaunchBed()) names = [.. probe.OtherCommandsDeclare(preset).Take(3)];
        Assert.Equal(3, names.Length);
        var (byTeam, byEntry, inherited) = (names[0], names[1], names[2]);

        using var bed = new HomeLaunchBed(d => d.Name == preset
            ? d with { Env = new Dictionary<string, string>(d.Env ?? new Dictionary<string, string>()) { [byEntry] = "from-the-entry" } }
            : d);

        var seen = await bed.RunAsync(preset, new Dictionary<string, string> { [byTeam] = "from-the-team" });

        Assert.Equal("from-the-team", seen[byTeam]);
        Assert.Equal("from-the-entry", seen[byEntry]);
        Assert.False(seen.ContainsKey(inherited));
    }

    [Fact]
    public async Task A_home_launch_check_is_scoped_as_its_run_is()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The fake CLIs are shell scripts.");

        using var bed = new HomeLaunchBed();
        var preset = HomeLaunchBed.HeadlessPresets()
            .Select(p => p.Data)
            .First(p => bed.Definition(p).LaunchCheck is { Count: > 0 });

        var report = await bed.Runner.CheckLaunchAsync(preset, TestContext.Current.CancellationToken);
        Assert.True(report.Result == AgentLaunchReport.Ok, report.Detail);

        Assert.Empty(bed.Seen(preset).Keys.Intersect(bed.OtherCommandsDeclare(preset)));
    }
}
