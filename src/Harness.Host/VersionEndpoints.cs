using System.Reflection;
using Harness.Host.Auth;

namespace Harness.Host;

/// <summary>
/// Which build this is. Read from the Host assembly's own attributes, which the build
/// stamps from <c>scripts/version.mjs</c> - so there is no second place a version is written.
/// </summary>
/// <param name="Version"><c>2026.09.23.1</c> at a release tag; <c>2026.09.23.1+3.eed3fd0</c> three
/// commits after it; <c>.dirty</c> on the end from a dirty tree.</param>
/// <param name="Commit">The full commit id, or <c>unknown</c> when the build could not ask git and
/// was not told.</param>
/// <param name="BuiltAt">When the Host was compiled, UTC. <c>null</c> if the stamp is missing or
/// unreadable.</param>
public sealed record BuildVersion(string Version, string Commit, DateTimeOffset? BuiltAt)
{
    public static BuildVersion Of(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(m => m.Value is not null)
            .GroupBy(m => m.Key)
            .ToDictionary(g => g.Key, g => g.Last().Value!);

        return new BuildVersion(
            string.IsNullOrEmpty(informational) ? "0.0.0+unknown" : informational,
            metadata.GetValueOrDefault("HarnessCommit") is { Length: > 0 } commit ? commit : "unknown",
            DateTimeOffset.TryParse(metadata.GetValueOrDefault("HarnessBuiltAt"), out var builtAt) ? builtAt : null);
    }

    /// <summary>This Host's build. Read once: the attributes cannot change while it runs.</summary>
    public static BuildVersion Current { get; } = Of(typeof(BuildVersion).Assembly);
}

public static class VersionEndpoints
{
    public const string Route = "/api/version";

    public static void Map(WebApplication app)
    {
        // ANONYMOUS, because the login card shows it before anyone has signed in, and because
        // update.ps1 asks it which version is now running. It says which build this is and nothing
        // about what the instance holds.
        app.MapGet(Route, () => Results.Ok(BuildVersion.Current))
            .AllowAnonymous()
            .NoPermitRequired()
            .WithTags("Diagnostics")
            .WithSummary("Which build of the host this is")
            .WithDescription(
                "Anonymous. `version` is derived from the git tag at build time: `2026.09.23.1` at "
                + "a release, `<last release>+<commits since>.<short sha>` after one, `0.0.0+...` "
                + "before the first, with `.dirty` appended for a build from a dirty tree. `commit` "
                + "is the full commit id and `builtAt` when the host was compiled (UTC).");
    }
}
