namespace Yawble.Core.Teams;

/// <summary>
/// The git found on this computer. <paramref name="State"/>: "ok", "missing", "old" (older than
/// <see cref="GitProgram.Minimum"/>) or "broken" (it is there but <c>git --version</c> fails).
/// <paramref name="CanInstall"/>: this computer can install or update it from the app (Windows with winget).
/// </summary>
public sealed record GitStatus(string State, string? Version, string? Path, string Minimum, bool CanInstall, string? Detail)
{
    public bool Ok => State == "ok";
}

/// <summary>What an install or update did, and the git found afterwards.</summary>
public sealed record GitInstallResult(bool Done, string Message, GitStatus Status);

/// <summary>Finding and running programs on this computer. Tests pass a fake: no test installs anything.</summary>
public interface IGitHost
{
    bool IsWindows { get; }

    /// <summary>The full path of git (putting its folder on this process's PATH when needed), or null.</summary>
    string? FindGit();

    /// <summary>The full path of winget, or null.</summary>
    string? FindWinget();

    Task<GitResult> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan limit, CancellationToken ct);
}

public sealed class SystemGitHost : IGitHost
{
    public bool IsWindows => OperatingSystem.IsWindows();

    public string? FindGit() => GitProgram.EnsureOnPath();

    public string? FindWinget()
    {
        if (!IsWindows) return null;
        if (GitProgram.FindOnPath(Environment.GetEnvironmentVariable("PATH"), "winget.exe") is { } onPath) return onPath;
        // winget is an app execution alias in the person's WindowsApps folder, which a service-started PATH can miss.
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        var alias = local is null ? null : Path.Combine(local, "Microsoft", "WindowsApps", "winget.exe");
        return alias is not null && File.Exists(alias) ? alias : null;
    }

    public Task<GitResult> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan limit, CancellationToken ct) =>
        GitProgram.RunProgramAsync(exe, args, limit, ct);
}

/// <summary>
/// git.check and git.install: is git here and new enough, and (Windows) installing or updating Git for Windows for the
/// person with winget (Git for Windows installs for the whole computer, so Windows asks for permission). git itself is the only requirement; where a repository
/// is hosted (GitHub, another service, or nowhere) is each team's own business.
/// </summary>
public sealed class GitService(IGitHost host)
{
    public const string WingetId = "Git.Git";
    public static readonly TimeSpan CheckLimit = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan InstallLimit = TimeSpan.FromMinutes(15);

    private readonly SemaphoreSlim _installing = new(1, 1);

    public async Task<GitStatus> CheckAsync(CancellationToken ct)
    {
        var minimum = GitProgram.Minimum.ToString();
        var canInstall = host.IsWindows && host.FindWinget() is not null;
        var path = host.FindGit();
        if (path is null) return new GitStatus("missing", null, null, minimum, canInstall, null);
        var r = await host.RunAsync(path, ["--version"], CheckLimit, ct).ConfigureAwait(false);
        var version = r.ExitCode == 0 ? GitProgram.ParseVersion(r.Stdout) : null;
        if (version is null)
        {
            var why = r.ExitCode == 0 ? $"it printed \"{First(r.Stdout)}\"" : $"git --version failed: {First(r.Stderr.Length > 0 ? r.Stderr : r.Stdout)}";
            return new GitStatus("broken", null, path, minimum, canInstall, why);
        }
        var text = version.ToString(3);
        return version < GitProgram.Minimum
            ? new GitStatus("old", text, path, minimum, canInstall, null)
            : new GitStatus("ok", text, path, minimum, canInstall, null);
    }

    /// <summary>
    /// The winget command line an install (git missing) or update (git old or broken) runs. Git for Windows' installer
    /// installs for the whole computer whatever scope it is asked for (it ignores /CURRENTUSER), so Windows asks the
    /// person for permission; the person can decline, and Download Git remains.
    /// </summary>
    public static IReadOnlyList<string> WingetArgs(bool update) =>
    [
        update ? "upgrade" : "install", "--id", WingetId, "--exact", "--source", "winget",
        "--silent", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity",
    ];

    /// <summary>Installs Git for Windows when it is missing, or updates it when it is too old or broken. Nothing to do when it is fine.</summary>
    public async Task<GitInstallResult> InstallAsync(CancellationToken ct)
    {
        var before = await CheckAsync(ct).ConfigureAwait(false);
        if (before.Ok) return new GitInstallResult(true, $"git {before.Version} is already installed; nothing to do.", before);
        if (!host.IsWindows)
            return new GitInstallResult(false, $"Install git {before.Minimum} or later with this computer's package manager, then check again. {GitProgram.DownloadUrl}", before);
        var winget = host.FindWinget();
        if (winget is null)
            return new GitInstallResult(false, $"winget is not available on this computer: install Git for Windows from {GitProgram.DownloadUrl}, then check again.", before);
        if (!await _installing.WaitAsync(0, ct).ConfigureAwait(false))
            return new GitInstallResult(false, "Git is being installed already; wait for it to finish.", before);
        try
        {
            var update = before.State != "missing";
            var r = await host.RunAsync(winget, WingetArgs(update), InstallLimit, ct).ConfigureAwait(false);
            var after = await CheckAsync(ct).ConfigureAwait(false);
            if (after.Ok) return new GitInstallResult(true, $"git {after.Version} is installed ({after.Path}).", after);
            var output = Last(string.Join('\n', r.Stdout, r.Stderr));
            var what = update ? "Updating" : "Installing";
            return new GitInstallResult(false,
                $"{what} Git for Windows did not finish (winget exit code {r.ExitCode}{(output.Length > 0 ? $": {output}" : "")}). " +
                $"Install it from {GitProgram.DownloadUrl}, then check again.", after);
        }
        finally
        {
            _installing.Release();
        }
    }

    private static string First(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";

    /// <summary>winget's last meaningful line (it draws progress bars with lone characters).</summary>
    private static string Last(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).LastOrDefault(l => l.Length > 3 && l.Any(char.IsLetter)) ?? "";
}
