using System.Diagnostics;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// A clone with no default branch stored is read at its next Fetch or at the next start, whichever
/// comes first. Without the start read it leaves the Git dialog saying it is not known and every
/// backlog item's landed mark reading `unknown` until a person presses Fetch. The
/// start reads what `git clone` already recorded (`refs/remotes/origin/HEAD`), with no network.
/// </summary>
public sealed class DefaultBranchAtStartTests : IAsyncDisposable
{
    private const string Repo = "Widget";

    private readonly string _root = Directory.CreateTempSubdirectory("harness-branch-start-").FullName;
    private readonly string _origin;
    private readonly string _dataRoot;
    private readonly List<WebApplicationFactory<Program>> _factories = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public DefaultBranchAtStartTests()
    {
        // An origin whose default branch is NOT main, so a guess of `main` cannot pass.
        _origin = Path.Combine(_root, "origin.git");
        var seed = Path.Combine(_root, "seed");
        Git(_root, "init", "--bare", "-b", "develop", _origin);
        Git(_root, "clone", _origin, seed);
        File.WriteAllText(Path.Combine(seed, "README.md"), "widget\n");
        Git(seed, "add", ".");
        Git(seed, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "commit", "-m", "base");
        Git(seed, "push", "origin", "develop");

        _dataRoot = Path.Combine(_root, "data");
        Directory.CreateDirectory(_dataRoot);
    }

    [Fact]
    public async Task A_clone_with_no_stored_branch_is_read_at_the_next_start()
    {
        // Before the upgrade: the clone exists and nothing is stored for it.
        var first = Start();
        var registry = first.Services.GetRequiredService<TeamRegistry>();
        var agent = first.Services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
        var team = (await registry.CreateAsync("Alpha", agent, memberAgent: agent, ct: Ct)).Id;
        await registry.SetReposAsync(team, [$"https://github.com/example/{Repo}.git"], Ct);
        Assert.Null(registry.DefaultBranchFor(team, Repo).Branch);
        await first.DisposeAsync();

        // The next start, with no Fetch pressed.
        var second = Start();
        var restarted = second.Services.GetRequiredService<TeamRegistry>();

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (restarted.DefaultBranchFor(team, Repo).Branch is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, Ct);
        }

        Assert.Equal("develop", restarted.DefaultBranchFor(team, Repo).Branch);
    }

    private WebApplicationFactory<Program> Start()
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentRunner>(new FakeAgent());
                services.AddSingleton<IRepoClone>(new CloneWithoutReadingHead(_origin));
            }));
        _factories.Add(factory);
        _ = factory.Server; // start the host, hosted services included
        return factory;
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

    /// <summary>Clones the local origin and reports no default branch, so nothing is stored for
    /// it.</summary>
    private sealed class CloneWithoutReadingHead(string origin) : IRepoClone
    {
        public Task<IReadOnlyList<RepoCloneOutcome>> EnsureAllAsync(IReadOnlyList<(string Url, string Path)> repos, CancellationToken ct)
        {
            var outcomes = new List<RepoCloneOutcome>();
            foreach (var (url, path) in repos)
            {
                if (Directory.Exists(path))
                {
                    outcomes.Add(new RepoCloneOutcome(url, path, RepoCloneResult.AlreadyThere));
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                Git(Path.GetDirectoryName(path)!, "clone", origin, path);
                outcomes.Add(new RepoCloneOutcome(url, path, RepoCloneResult.Cloned));
            }

            return Task.FromResult<IReadOnlyList<RepoCloneOutcome>>(outcomes);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var factory in _factories) await factory.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
