namespace Yawble.Core.Sandboxing;

/// <summary>One thing this host needs before an agent can start in a sandbox, and how to fix it when missing.</summary>
public sealed record HostCheck(string Id, bool Ok, string Message, string? Fix);

/// <summary>
/// The startup probe. On Linux, the sandbox's Bubblewrap backend needs bwrap,
/// slirp4netns for proxy-only networking, and unprivileged user namespaces; Ubuntu 24.04+ refuses those
/// (kernel.apparmor_restrict_unprivileged_userns=1) unless the caller runs under an AppArmor profile that allows
/// them. The Linux installer's install-apparmor.sh loads that profile with root once; this probe says clearly when
/// it is not in effect. Windows and macOS need nothing installed, so they report no checks.
/// </summary>
public sealed class HostChecks(Func<string, string?> readFile, Func<string, bool> onPath, string? apparmorScript)
{
    /// <summary>Every profile the installer loads is named yawble or yawble-*.</summary>
    public const string ProfilePrefix = "yawble";

    public static HostChecks ForThisHost() => new(ReadFileOrNull, OnPath, FindApparmorScript());

    public IReadOnlyList<HostCheck> Run() => OperatingSystem.IsLinux() ? RunLinux() : [];

    internal IReadOnlyList<HostCheck> RunLinux() => [UserNamespaces(), Tool("bwrap", "Bubblewrap (bwrap 0.5.0 or later), which the Linux sandbox is built on", "bubblewrap"), Tool("slirp4netns", "slirp4netns, which the sandbox needs to give a sandbox its proxy-only network", "slirp4netns")];

    private HostCheck UserNamespaces()
    {
        var restrict = readFile("/proc/sys/kernel/apparmor_restrict_unprivileged_userns")?.Trim();
        if (restrict != "1")
            return new("apparmor-userns", true, "Unprivileged user namespaces are not restricted by AppArmor on this host.", null);
        // The label the kernel gives this process, e.g. "yawble-core (unconfined)" or "unconfined".
        var label = (readFile("/proc/self/attr/apparmor/current") ?? readFile("/proc/self/attr/current"))?.Trim('\0', ' ', '\n') ?? "unknown";
        if (label.StartsWith(ProfilePrefix, StringComparison.Ordinal))
            return new("apparmor-userns", true, $"AppArmor restricts user namespaces, and the core runs under the Yawble profile ({label}), which allows them.", null);
        var fix = apparmorScript is not null
            ? $"Run this once as root, then restart Yawble: sudo '{apparmorScript}'"
            : "Run the installer's install-apparmor.sh once as root (sudo), then restart Yawble.";
        return new("apparmor-userns", false,
            $"The Yawble AppArmor profile is missing: this host restricts unprivileged user namespaces (kernel.apparmor_restrict_unprivileged_userns=1) and the core runs as '{label}', so no agent can start in a sandbox.",
            fix);
    }

    private HostCheck Tool(string name, string what, string package) => onPath(name)
        ? new(name, true, $"{name} is installed.", null)
        : new(name, false, $"{what} is not installed or not on PATH.", $"Install it, e.g. sudo apt install {package}");

    private static string? ReadFileOrNull(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool OnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir, name)));

    /// <summary>A packaged Linux install: &lt;app&gt;/resources/core/yawble-core, with the script at &lt;app&gt;/install-apparmor.sh.</summary>
    private static string? FindApparmorScript()
    {
        var script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "install-apparmor.sh"));
        return File.Exists(script) ? script : null;
    }
}
