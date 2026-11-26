namespace Yawble.Core.Policy;

/// <summary>
/// Linux: the host paths through which a name lookup inside the sandbox reaches a resolver OUTSIDE the sandbox's private
/// network namespace, so the lookup would leave without the gateway. Every one that is on the host is denied in
/// every Linux request (<see cref="RunLayout.ImplicitDenied"/>), which the sandbox turns into a mask: an empty tmpfs over a
/// folder, /dev/null over a file. The sandbox binds /run/systemd/resolve, /run/NetworkManager and /run/resolvconf
/// read-only, which does not stop connect(2) on a socket: without the masks getaddrinfo asks systemd-resolved, the
/// system bus, Avahi or nscd on the host. With /etc/nsswitch.conf and /etc/resolv.conf masked, glibc loads no lookup
/// module and asks the sandbox's own loopback. A user who exists only in sssd or LDAP has no passwd entry inside.
/// docs/architecture.md ("Linux name lookups") gives the reasoning.
/// </summary>
public static class LinuxResolverMasks
{
    /// <summary>systemd-resolved's varlink socket (nss-resolve): the guardrail check connects to it directly.</summary>
    public const string ResolvedVarlinkSocket = "/run/systemd/resolve/io.systemd.Resolve";

    /// <summary>The paths masked, in order, when present on the host.</summary>
    public static IReadOnlyList<string> Candidates { get; } =
    [
        "/run/systemd/resolve",
        "/run/dbus",
        "/run/avahi-daemon",
        "/run/nscd",
        "/etc/nsswitch.conf",
        "/etc/resolv.conf",
    ];

    /// <summary>The sockets the guardrail check connects to from inside: each must be unreachable (id, path).</summary>
    public static IReadOnlyList<(string Id, string Path)> Sockets { get; } =
    [
        ("net.dns-varlink", ResolvedVarlinkSocket),
        ("net.dns-dbus-system", "/run/dbus/system_bus_socket"),
        ("net.dns-avahi", "/run/avahi-daemon/socket"),
        ("net.dns-nscd", "/run/nscd/socket"),
    ];

    /// <summary>
    /// The candidates to deny on this host. <paramref name="realPath"/> gives a path's real location with every symlink
    /// followed, or null when it is missing or a symlink that does not resolve: the sandbox cannot bind over a missing
    /// path and refuses to start on a dangling symlink, so neither is emitted. A candidate whose real path lies inside
    /// another emitted candidate is already hidden by that mask (Fedora's and Ubuntu's /etc/resolv.conf link into
    /// /run/systemd/resolve) and is not emitted again.
    /// </summary>
    public static IReadOnlyList<string> Present(Func<string, string?> realPath)
    {
        var found = Candidates.Select(c => (Path: c, Real: realPath(c))).Where(c => c.Real is not null).ToList();
        // Inside another one's mask, or the same real object as an earlier one: hidden already.
        return found
            .Where((c, i) => !found.Where((o, j) => j != i && (Inside(c.Real!, o.Real!) || (c.Real == o.Real && j < i))).Any())
            .Select(c => c.Path)
            .ToList();
    }

    /// <summary>The real disk: <paramref name="path"/> with every symlink followed, or null when missing or dangling.</summary>
    public static string? RealPath(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (info.LinkTarget is null) return info.Exists ? Path.GetFullPath(path) : null;
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            return target is { Exists: true } ? target.FullName : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool Inside(string path, string folder) =>
        path.StartsWith(folder.TrimEnd('/') + "/", StringComparison.Ordinal);
}
