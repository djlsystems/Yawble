using System.Reflection;
using System.Runtime.InteropServices;

namespace Yawble.Core;

/// <summary>What the core says about itself over IPC (the window shows it).</summary>
public static class CoreInfo
{
    public const string ProductName = "Yawble core";

    /// <summary>The name the About section shows.</summary>
    public const string DisplayName = "Yawble Desktop";

    /// <summary>What a build without its version (no node or no git at build time) says.</summary>
    public const string UnknownVersion = "0.0.0+dev.unknown";

    private static string? Metadata(string key) =>
        typeof(CoreInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;

    /// <summary>
    /// The build's version: yyyy.mm.dd.N for a release, &lt;last release&gt;+dev.&lt;sha&gt;[.dirty] between releases
    /// (scripts/build-version.mjs writes it at build time).
    /// </summary>
    public static string Version { get; } = Metadata("YawbleVersion") is { Length: > 0 } v ? v : UnknownVersion;

    /// <summary>The git commit this build was made from (full), or "unknown".</summary>
    public static string Commit { get; } = Metadata("YawbleCommit") is { Length: > 0 } c ? c : "unknown";

    /// <summary>Whether the build had uncommitted changes.</summary>
    public static bool Dirty { get; } = Metadata("YawbleDirty") == "true";

    /// <summary>Whether this is a release build: the release tag's own commit, clean.</summary>
    public static bool Released { get; } = Metadata("YawbleReleased") == "true";

    /// <summary>linux-x64, osx-arm64, win-x64, ...: the .NET runtime identifier of this build.</summary>
    public static string Platform { get; } = $"{OsKey()}-{RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}";

    /// <summary>The startup line in the core's log.</summary>
    public static string StartupLine() =>
        $"{DisplayName} core {Version} ({(Released ? "release" : "development build")}, commit {Commit}{(Dirty ? ", uncommitted changes" : "")})";

    private static string OsKey() =>
        OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsLinux() ? "linux" : "unknown";
}
