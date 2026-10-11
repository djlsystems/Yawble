using System.Diagnostics;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// The Host's reachability check (`repo` refresh, `GET repo-status?refresh=true`) runs
/// `git ls-remote --exit-code origin`. "origin" is a remote NAME, so whether to hand git
/// <c>GH_TOKEN</c> cannot be decided by looking for `github.com` in the arguments: without the
/// token the credential helper sends an empty password and GitHub answers "Invalid username or
/// token", while Fetch and push, which look the team's remotes up, work with the same token - and a
/// Manager reading that answer would tell its members not to fetch or rebase.
///
/// What git was handed is read by the origin itself: its `uploadpack` is a script that records
/// <c>GH_TOKEN</c> before it serves the request.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class LsRemoteTokenTests : IDisposable
{
    private const string Token = "token-from-the-host";

    private readonly string _root = Directory.CreateTempSubdirectory("harness-lsremote-token-").FullName;
    private readonly string _clone;
    private readonly string _seen;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public LsRemoteTokenTests()
    {
        var origin = Path.Combine(_root, "origin.git");
        var seed = Path.Combine(_root, "seed");
        Git(_root, "init", "--bare", "-b", "main", origin);
        Git(_root, "clone", origin, seed);
        File.WriteAllText(Path.Combine(seed, "README.md"), "widget\n");
        Git(seed, "add", ".");
        Git(seed, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "commit", "-m", "base");
        Git(seed, "push", "origin", "main");

        _clone = Path.Combine(_root, "clone");
        Git(_root, "clone", origin, _clone);

        _seen = Path.Combine(_root, "seen");
        var uploadPack = Path.Combine(_root, "upload-pack.sh");
        File.WriteAllText(uploadPack, $"#!/bin/sh\nprintf '%s' \"${{GH_TOKEN:-none}}\" > '{_seen}'\nexec git upload-pack \"$@\"\n");
        File.SetUnixFileMode(uploadPack, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Git(_clone, "config", "remote.origin.uploadpack", uploadPack);
    }

    [Fact]
    public async Task A_refresh_of_a_GitHub_teams_origin_is_handed_the_token()
    {
        using var _ = new EnvironmentScope([new("GH_TOKEN", Token)]);
        var git = new GitRunner(remotesFor: _ => ["https://github.com/example/Widget.git"]);

        var result = await git.LsRemoteAsync(_clone, "origin", Ct);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Token, File.ReadAllText(_seen));
    }

    [Fact]
    public async Task A_refresh_of_a_team_with_no_GitHub_remote_is_handed_no_token()
    {
        using var _ = new EnvironmentScope([new("GH_TOKEN", Token)]);
        var git = new GitRunner(remotesFor: _ => ["https://gitlab.example/example/Widget.git"]);

        var result = await git.LsRemoteAsync(_clone, "origin", Ct);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("none", File.ReadAllText(_seen));
    }

    [Fact]
    public async Task Fetch_and_refresh_are_handed_the_same_token()
    {
        using var _ = new EnvironmentScope([new("GH_TOKEN", Token)]);
        var git = new GitRunner(remotesFor: _ => ["https://github.com/example/Widget.git"]);

        Assert.Equal(0, (await git.FetchAsync(_clone, Ct)).ExitCode);
        var byFetch = File.ReadAllText(_seen);
        File.Delete(_seen);
        Assert.Equal(0, (await git.LsRemoteAsync(_clone, "origin", Ct)).ExitCode);

        Assert.Equal(byFetch, File.ReadAllText(_seen));
    }

    /// <summary>
    /// The read of the default branch, `git remote set-head origin --auto`, asks origin too, and
    /// needs the token as well: without it, on a private GitHub repository every successful Fetch
    /// would read "not known" and write it over the stored branch, and the Git dialog would refuse
    /// every action. This origin refuses a request without the token, as GitHub does for a private
    /// repository.
    /// </summary>
    [Fact]
    public async Task The_default_branch_read_of_a_GitHub_teams_origin_is_handed_the_token()
    {
        RequireTheToken();
        using var _ = new EnvironmentScope([new("GH_TOKEN", Token)]);
        var git = new GitRunner(remotesFor: _ => ["https://github.com/example/Widget.git"]);

        Assert.Equal("main", await git.ReadOriginHeadBranchAsync(_clone, Ct));
        Assert.Equal(Token, File.ReadAllText(_seen));
    }

    [Fact]
    public async Task The_default_branch_read_of_a_team_with_no_GitHub_remote_is_handed_no_token()
    {
        using var _ = new EnvironmentScope([new("GH_TOKEN", Token)]);
        var git = new GitRunner(remotesFor: _ => ["https://gitlab.example/example/Widget.git"]);

        Assert.Equal("main", await git.ReadOriginHeadBranchAsync(_clone, Ct));
        Assert.Equal("none", File.ReadAllText(_seen));
    }

    /// <summary>Replaces the recording upload-pack with one that also refuses a request carrying no token.</summary>
    private void RequireTheToken()
    {
        var uploadPack = Path.Combine(_root, "upload-pack.sh");
        File.WriteAllText(uploadPack,
            $"#!/bin/sh\nprintf '%s' \"${{GH_TOKEN:-none}}\" > '{_seen}'\n"
            + "[ -n \"$GH_TOKEN\" ] || { echo 'Invalid username or token' >&2; exit 128; }\n"
            + "exec git upload-pack \"$@\"\n");
    }

    private static void Git(string workingDirectory, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
