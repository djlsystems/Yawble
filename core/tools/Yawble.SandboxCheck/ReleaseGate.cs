using System.Net.Http.Headers;
using System.Text.Json;
using System.Xml.Linq;
using Yawble.Core.Sandboxing;

namespace Yawble.SandboxCheck;

public enum Level { Info, Pass, Warn, Fail }

public sealed record Line(Level Level, string Text);

public sealed class Report
{
    public List<Line> Lines { get; } = [];
    public bool Failed => Lines.Any(l => l.Level == Level.Fail);
    public void Add(Level level, string text) => Lines.Add(new Line(level, text));
    public void Check(bool ok, string pass, string fail) => Add(ok ? Level.Pass : Level.Fail, ok ? pass : fail);
}

public static class ReleaseGate
{
    /// <summary>The repository root: the folder holding both Yawble.slnx and sandbox.lock.json.</summary>
    public static string? FindRepoRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "sandbox.lock.json")) && File.Exists(Path.Combine(dir.FullName, "Yawble.slnx")))
                return dir.FullName;
        return null;
    }

    /// <summary>A SemVer pre-release has a '-' suffix (1.0.1-rc1, 1.1.0-alpha).</summary>
    public static bool IsPrerelease(string version) => version.Contains('-');

    public static async Task<Report> RunAsync(string root, bool offline, HttpClient http, Func<string, NativeSandbox.Outcome>? runJson = null, Func<int?>? runTyped = null)
    {
        var report = new Report();
        using var lockDoc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "sandbox.lock.json")));
        var release = lockDoc.RootElement.GetProperty("release");
        var version = release.GetProperty("version").GetString()!;
        var tag = release.GetProperty("tag").GetString()!;
        var repo = release.GetProperty("githubRepo").GetString()!;
        var package = release.GetProperty("nugetPackage").GetString()!;
        var contract = lockDoc.RootElement.GetProperty("request").GetProperty("contract").GetString();
        var telemetry = lockDoc.RootElement.GetProperty("request").GetProperty("telemetryEnabled").GetBoolean();
        report.Add(Level.Info, $"chosen sandbox release {version} ({repo} {tag}, NuGet {package})");

        // 1. The pins agree with each other and with what was restored.
        report.Check(!IsPrerelease(version), $"{version} is not a pre-release version", $"{version} is a pre-release version");
        report.Check(contract == SandboxPin.RequestContract && telemetry == SandboxPin.TelemetryEnabled,
            $"requests pinned to contract {SandboxPin.RequestContract}, telemetry {(SandboxPin.TelemetryEnabled ? "on" : "off")}",
            $"sandbox.lock.json request pin ({contract}, telemetry {telemetry}) differs from the code (SandboxPin: {SandboxPin.RequestContract}, {SandboxPin.TelemetryEnabled})");
        var props = XDocument.Load(Path.Combine(root, "Directory.Packages.props"));
        var propsVersion = props.Descendants("PackageVersion")
            .FirstOrDefault(e => (string?)e.Attribute("Include") == package)?.Attribute("Version")?.Value;
        if (propsVersion == "$(SandboxVersion)") propsVersion = props.Descendants("SandboxVersion").FirstOrDefault()?.Value;
        report.Check(propsVersion == version, $"Directory.Packages.props references {package} {propsVersion}",
            $"Directory.Packages.props references {package} {propsVersion ?? "(none)"}, sandbox.lock.json says {version}");
        var restored = SandboxPlatformInfo.ManagedSdkVersion;
        report.Check(restored == version, $"restored SDK assembly is {restored}", $"restored SDK assembly is {restored}, expected {version}");

        // 2. The registries agree that this is a stable release.
        if (offline)
        {
            report.Add(Level.Warn, "--offline: NuGet and GitHub release metadata not checked");
        }
        else
        {
            await CheckRegistriesAsync(report, http, version, tag, repo, package).ConfigureAwait(false);
        }

        // 3. The native parser still accepts our pinned request.
        var verdict = SandboxContractProbe.Check(runJson ?? NativeSandbox.RunJson, runTyped);
        foreach (var attempt in verdict.Attempts)
            report.Add(Level.Info, $"probe [{attempt.Name}]: {(attempt.Accepted ? "accepted" : "rejected")} - {attempt.Outcome}");
        report.Check(verdict.Ok, verdict.Summary, verdict.Summary);
        return report;
    }

    private static async Task CheckRegistriesAsync(Report report, HttpClient http, string version, string tag, string repo, string package)
    {
        try
        {
            using var nuget = JsonDocument.Parse(await http.GetStringAsync(
                $"https://api.nuget.org/v3-flatcontainer/{package.ToLowerInvariant()}/index.json").ConfigureAwait(false));
            var versions = nuget.RootElement.GetProperty("versions").EnumerateArray().Select(v => v.GetString()!).ToList();
            report.Check(versions.Contains(version), $"NuGet lists {package} {version}", $"NuGet does not list {package} {version}");
            var newer = versions.Where(v => !IsPrerelease(v) && CompareVersions(v, version) > 0).ToList();
            if (newer.Count > 0)
                report.Add(Level.Warn, $"a newer stable {package} exists ({string.Join(", ", newer)}); the rule is the latest stable - bump sandbox.lock.json, Directory.Packages.props and re-run this gate");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            report.Add(Level.Fail, $"could not read NuGet metadata: {e.Message}");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/tags/{tag}");
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("yawble-release-gate", "1"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            if (!string.IsNullOrEmpty(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var gh = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            var prerelease = gh.RootElement.GetProperty("prerelease").GetBoolean();
            var draft = gh.RootElement.GetProperty("draft").GetBoolean();
            report.Check(!prerelease && !draft, $"GitHub release {tag} is a published stable release",
                $"GitHub release {tag} is {(draft ? "a draft" : "marked pre-release")}");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            report.Add(Level.Fail, $"could not read GitHub release {tag}: {e.Message}");
        }
    }

    internal static int CompareVersions(string a, string b) =>
        Version.TryParse(a.Split('-')[0], out var va) && Version.TryParse(b.Split('-')[0], out var vb) ? va.CompareTo(vb) : string.CompareOrdinal(a, b);
}
