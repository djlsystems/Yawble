using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Harness.Host;

/// <summary>
/// THE PUBLISHED PREVIEW PORTS, and what the Host listens on for them. The operator CLI publishes a
/// range of ports beside the Host's own and says which in <see cref="Variable"/>; the Host listens on
/// each and <see cref="PreviewForwarder"/> forwards it to an app the person's Concierge started.
///
/// <para>
/// THE APP NEVER BINDS A PUBLISHED PORT: it listens on its internal port (<see cref="Map"/>), on
/// loopback. Control and every worker share ONE network namespace - one process in <c>all</c>, one pod
/// under Podman, <c>--network container:</c> under Docker (docs/architecture.md, "The pod") - so once
/// the Host listens on <c>0.0.0.0:8081</c> nothing else can bind 8081 on any address, and the worker's
/// address is loopback in every layout. <see cref="Map"/> is the ONE place a published port's internal
/// port is decided; the Host forwards to it and the Concierge tells the app to bind it.
/// </para>
/// </summary>
public static class PreviewPorts
{
    /// <summary>The ports the CLI published, comma-separated; absent or empty is none. Read through the
    /// Host's configuration, so the environment variable of that name sets it.</summary>
    public const string Variable = "HARNESS_PUBLISHED_PREVIEW_PORTS";

    /// <summary>The step between a published port and the internal ports tried for it: 8081 -> 18081.</summary>
    public const int Step = 10000;

    /// <summary>The lowest internal port: below it a port is privileged.</summary>
    public const int InternalLowest = 1024;

    /// <summary>The highest internal port: above it starts the range the system hands out for outgoing
    /// connections (32768 on Linux, 49152 elsewhere), where an app's port could already be in use.</summary>
    public const int InternalHighest = 32767;

    /// <summary>
    /// The published ports in <paramref name="value"/>, in order, once each. A value that is not a
    /// port is skipped with a line to <paramref name="say"/>.
    /// </summary>
    public static IReadOnlyList<int> Parse(string? value, Action<string> say)
    {
        var ports = new List<int>();

        foreach (var part in (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                || port < 1 || port > IPEndPoint.MaxPort)
            {
                say($"Preview port '{part}' in {Variable} is not a port from 1 to {IPEndPoint.MaxPort}; it is not forwarded.");
                continue;
            }

            if (!ports.Contains(port)) ports.Add(port);
        }

        return ports;
    }

    /// <summary>
    /// THE MAPPING: each published port to the internal port its app listens on. Deterministic for a
    /// given list, whatever range was configured. Ports are taken lowest first, and each gets the first
    /// of <c>N+10000, N-10000, N+20000, N-20000, ...</c> that lies from <see cref="InternalLowest"/> to
    /// <see cref="InternalHighest"/>, is not itself a published port and was not given to a lower one;
    /// failing all of those, the lowest such port from <see cref="Step"/> up. So 8081 -> 18081, and
    /// 28081 (whose 38081 is in the outgoing range) -> 18081. A port with no internal port left is
    /// skipped with a line to <paramref name="say"/>.
    /// </summary>
    public static IReadOnlyDictionary<int, int> Map(IEnumerable<int> published, Action<string> say)
    {
        var ports = published.Distinct().Order().ToList();
        var taken = new HashSet<int>(ports);
        var map = new Dictionary<int, int>();

        foreach (var port in ports)
        {
            var found = 0;
            for (var k = 1; found == 0 && k * Step <= IPEndPoint.MaxPort; k++)
            {
                foreach (var candidate in new[] { port + k * Step, port - k * Step })
                {
                    if (candidate is >= InternalLowest and <= InternalHighest && taken.Add(candidate))
                    {
                        found = candidate;
                        break;
                    }
                }
            }

            for (var candidate = Step; found == 0 && candidate <= InternalHighest; candidate++)
            {
                if (taken.Add(candidate)) found = candidate;
            }

            if (found == 0) say($"Preview port {port} has no free internal port from {InternalLowest} to {InternalHighest}; it is not forwarded.");
            else map[port] = found;
        }

        return map;
    }

    /// <summary>
    /// The published ports this Host will listen on: those in its configuration that are free on this
    /// machine now. A port already taken is skipped with one line, and never stops the Host starting -
    /// Kestrel refuses to start at all when one of its addresses cannot be bound.
    /// </summary>
    public static IReadOnlySet<int> Open(IReadOnlyList<int> published, Func<int, bool> isFree, Action<string> say)
    {
        var open = new HashSet<int>();

        foreach (var port in published)
        {
            if (isFree(port)) open.Add(port);
            else say($"Preview port {port} is already taken on this machine; it is not forwarded.");
        }

        return open;
    }

    /// <summary>
    /// The Host's addresses with the preview ports added on every interface, as the published Host
    /// port is. Setting any address replaces what Kestrel would otherwise have chosen, so the Host's own
    /// is carried over first: <paramref name="urls"/> (<c>urls</c> / <c>ASPNETCORE_URLS</c>), else
    /// <paramref name="httpPorts"/> and <paramref name="httpsPorts"/> (<c>HTTP_PORTS</c>,
    /// <c>HTTPS_PORTS</c>, as an image sets them), else Kestrel's own default.
    /// </summary>
    public static string Urls(string? urls, IEnumerable<int> ports, string? httpPorts = null, string? httpsPorts = null)
    {
        var own = !string.IsNullOrWhiteSpace(urls)
            ? urls.Trim().TrimEnd(';')
            : string.Join(';', [.. OnEvery("http", httpPorts), .. OnEvery("https", httpsPorts)]) is { Length: > 0 } fromPorts
                ? fromPorts
                : "http://localhost:5000";

        return string.Join(';', [own, .. ports.Order().Select(p => $"http://0.0.0.0:{p.ToString(CultureInfo.InvariantCulture)}")]);
    }

    private static IEnumerable<string> OnEvery(string scheme, string? ports) =>
        (ports ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(port => $"{scheme}://*:{port}");

    /// <summary>Whether <paramref name="port"/> can be listened on, on every interface, now.</summary>
    public static bool IsFree(int port)
    {
        try
        {
            var probe = new TcpListener(IPAddress.Any, port);
            probe.Start();
            probe.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
