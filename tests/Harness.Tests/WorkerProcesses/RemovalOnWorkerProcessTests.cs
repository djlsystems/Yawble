using System.Net.Http.Json;
using System.Text.Json;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// CONTROL AND A WORKER AS REAL PROCESSES: what control cannot remove of a deleted member's workspace is
/// removed by the worker's agent pass; with no worker it is recorded unfinished with the reason, and a
/// worker joining finishes it without a person's retry.
/// </summary>
/// <remarks>
/// The first two need this test process to switch users: control runs as <c>daemon</c>, so it cannot
/// remove what <c>nobody</c> owns, and the worker runs as this (root) process with <c>nobody</c> as its
/// agent user. Run as root, control would remove the folder in its own pass and the worker would never
/// be asked. The third runs only as another user, where a folder the Host may not write stands in for
/// what only the agent could remove.
/// </remarks>
[Collection("worker processes")]
public sealed class RemovalOnWorkerProcessTests
{
    private const string ControlUser = "daemon";

    private const string AgentUser = "nobody";

    [Fact]
    public async Task An_agent_owned_owner_only_folder_is_removed_through_the_workers_agent_pass()
    {
        var (control, agent) = await UsersAsync();
        await using var bed = new ProcessBed();
        await StartControlAsAsync(bed, control);
        var (team, workspace, agentDir) = await WorkspaceAsync(bed, control, agent);

        var worker = bed.StartWorker("w1", more: new Dictionary<string, string> { ["HARNESS_AGENT_USER"] = AgentUser });
        await bed.UntilAsync("w1 is connected", async () => (await bed.WorkersAsync()).Any(w => w.GetProperty("id").GetString() == "w1"));

        var deleted = await DeleteAsync(bed, team);

        Assert.Empty(deleted.GetProperty("remaining").EnumerateArray());
        Assert.False(Directory.Exists(agentDir), $"{agentDir} is still there.\n{bed.Logs()}");
        Assert.False(Directory.Exists(workspace));
        Assert.DoesNotContain(await RemovalsAsync(bed), row => row.GetProperty("path").GetString() == workspace);

        // The worker was asked: control's own user could not have removed it (proven before the delete).
        Assert.Contains($"Worker w1 removes 1 path(s) inside {workspace} as the agent.", worker.Text());
    }

    [Fact]
    public async Task With_no_worker_a_removal_is_recorded_unfinished_and_finished_when_one_connects()
    {
        var (control, agent) = await UsersAsync();
        await using var bed = new ProcessBed();
        await StartControlAsAsync(bed, control);
        var (team, workspace, agentDir) = await WorkspaceAsync(bed, control, agent);

        var deleted = await DeleteAsync(bed, team);

        Assert.Contains(agentDir, deleted.GetProperty("remaining").EnumerateArray().Select(p => p.GetString()));
        Assert.Contains(deleted.GetProperty("failures").EnumerateArray(), f => f.GetString()!.Contains(WorkerAgentPass.NoWorkerText, StringComparison.Ordinal));
        Assert.Contains(await RemovalsAsync(bed), row => row.GetProperty("path").GetString() == workspace && row.GetProperty("member").GetString() == "Dev");
        Assert.True(Directory.Exists(agentDir));

        // A worker joins; nobody asks for a retry.
        var worker = bed.StartWorker("w1", more: new Dictionary<string, string> { ["HARNESS_AGENT_USER"] = AgentUser });
        await bed.UntilAsync("the unfinished removal is finished", async () =>
            !Directory.Exists(workspace) && !(await RemovalsAsync(bed)).Any(row => row.GetProperty("path").GetString() == workspace));
        Assert.Contains($"Worker w1 removes 1 path(s) inside {workspace} as the agent.", worker.Text());
    }

    [Fact]
    public async Task With_no_worker_what_the_host_cannot_remove_is_recorded_unfinished_with_the_reason_and_retried_when_a_worker_joins()
    {
        if (Environment.IsPrivilegedProcess)
        {
            Assert.Skip("As root the Host removes a folder it may not write all the same, so nothing would be left; the two tests above cover this as root.");
        }

        await using var bed = new ProcessBed();
        await bed.StartControlAsync();
        var team = await bed.TeamAsync("Removal", "Dev");
        var workspace = Directory.CreateDirectory(Path.Combine(bed.Root, "teams", team, "workspaces", "Dev")).FullName;
        var locked = Directory.CreateDirectory(Path.Combine(workspace, "locked")).FullName;
        await File.WriteAllTextAsync(Path.Combine(locked, "a.txt"), "x", TestContext.Current.CancellationToken);
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {
            var deleted = await DeleteAsync(bed, team);

            Assert.NotEmpty(deleted.GetProperty("remaining").EnumerateArray());
            Assert.Contains(deleted.GetProperty("failures").EnumerateArray(), f => f.GetString()!.Contains(WorkerAgentPass.NoWorkerText, StringComparison.Ordinal));
            Assert.Contains(await RemovalsAsync(bed), row => row.GetProperty("path").GetString() == workspace && row.GetProperty("member").GetString() == "Dev");

            // Removable again, standing in for the agent's pass; then a worker joins and nobody asks for a retry.
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Assert.Contains(await RemovalsAsync(bed), row => row.GetProperty("path").GetString() == workspace);
            bed.StartWorker("w1");
            await bed.UntilAsync("the unfinished removal is finished", async () =>
                !Directory.Exists(workspace) && !(await RemovalsAsync(bed)).Any(row => row.GetProperty("path").GetString() == workspace));
        }
        finally
        {
            if (Directory.Exists(locked)) File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>The control user and the agent user, or a skip saying why this process cannot use them.</summary>
    private static async Task<(AgentLaunchUser Control, AgentLaunchUser Agent)> UsersAsync()
    {
        var control = AgentLaunchUser.Resolve(ControlUser);
        var agent = AgentLaunchUser.Resolve(AgentUser);
        if (!control.Switches || !agent.Switches)
        {
            Assert.Skip($"Control as '{ControlUser}' and the agent as '{AgentUser}' need this test process to switch users, and it cannot: {(control.Switches ? agent : control).Reason}");
        }

        var dll = Path.Combine(AppContext.BaseDirectory, "Harness.Host.dll");
        var (runs, why) = await RunAsync([.. control.Prefix, "sh", "-c", $"dotnet --list-runtimes >/dev/null && test -r '{dll}'"]);
        if (runs != 0)
        {
            Assert.Skip($"'{ControlUser}' cannot run dotnet or read {dll} from this test's folder: {why}");
        }

        return (control, agent);
    }

    /// <summary>Control as <paramref name="user"/>, which owns the data root and a home of its own.</summary>
    private static async Task StartControlAsAsync(ProcessBed bed, AgentLaunchUser user)
    {
        var home = Directory.CreateDirectory(Path.Combine(bed.Work, "home-control")).FullName;
        Grant(bed.Work, UnixFileMode.OtherExecute);
        await OwnAsync(user, bed.Root, home);

        await bed.StartControlAsync(prefix: user.Prefix, more: new Dictionary<string, string>
        {
            ["HOME"] = home,
            ["DOTNET_CLI_HOME"] = home,
            ["HARNESS_AGENT_USER"] = AgentUser,
        });
    }

    /// <summary>
    /// A team with a member Dev whose workspace holds a file of control's user and an owner-only folder
    /// with files of the agent's, which control's user is shown unable to remove.
    /// </summary>
    private static async Task<(string Team, string Workspace, string AgentDir)> WorkspaceAsync(ProcessBed bed, AgentLaunchUser control, AgentLaunchUser agent)
    {
        var team = await bed.TeamAsync("Removal", "Dev");
        var workspace = Directory.CreateDirectory(Path.Combine(bed.Root, "teams", team, "workspaces", "Dev")).FullName;
        await File.WriteAllTextAsync(Path.Combine(workspace, "notes.md"), "control's", TestContext.Current.CancellationToken);
        await OwnAsync(control, workspace);
        foreach (var dir in new[] { bed.Root, Path.Combine(bed.Root, "teams"), Path.Combine(bed.Root, "teams", team), Path.GetDirectoryName(workspace)!, workspace })
        {
            File.SetUnixFileMode(dir, (UnixFileMode)Convert.ToInt32("777", 8));
        }

        // Made BY THE AGENT, owner-only, the way `dotnet test` and `mktemp` leave them.
        var agentDir = Path.Combine(workspace, "tmp");
        var (made, said) = await RunAsync([.. agent.Prefix, "sh", "-c",
            $"umask 077 && mkdir -p '{agentDir}/nuget/v3' && echo x > '{agentDir}/a.txt' && echo y > '{agentDir}/nuget/v3/b.bin' && chmod 700 '{agentDir}' '{agentDir}/nuget' '{agentDir}/nuget/v3'"]);
        Assert.True(made == 0, $"the agent user could not make {agentDir}: {said}");

        // Control's user cannot remove it: what removes it later is not control.
        var (removed, _) = await RunAsync([.. control.Prefix, "rm", "-rf", "--one-file-system", "--", agentDir]);
        Assert.NotEqual(0, removed);
        Assert.True(File.Exists(Path.Combine(agentDir, "nuget", "v3", "b.bin")));

        return (team, workspace, agentDir);
    }

    private static async Task<JsonElement> DeleteAsync(ProcessBed bed, string team)
    {
        var deleted = await bed.Person.DeleteAsync($"/api/teams/{team}/containers/Dev");
        Assert.True(deleted.IsSuccessStatusCode, $"{(int)deleted.StatusCode} {await deleted.Content.ReadAsStringAsync()}\n{bed.Logs()}");
        return await deleted.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<IReadOnlyList<JsonElement>> RemovalsAsync(ProcessBed bed) => [.. (await bed.GetAsync("/api/removals")).EnumerateArray()];

    private static async Task OwnAsync(AgentLaunchUser user, params string[] paths)
    {
        var (code, said) = await RunAsync(["chown", "-R", $"{user.Uid}:{user.Gid}", "--", .. paths]);
        Assert.True(code == 0, $"chown to {user.Name} failed: {said}");
    }

    private static void Grant(string path, UnixFileMode mode) => File.SetUnixFileMode(path, File.GetUnixFileMode(path) | mode);

    private static async Task<(int Code, string Said)> RunAsync(IReadOnlyList<string> command)
    {
        var start = new System.Diagnostics.ProcessStartInfo(command[0])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in command.Skip(1)) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return (process.ExitCode, await output + await error);
    }
}
