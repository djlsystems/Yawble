using System.Net;

namespace Yawble.Core.Policy;

/// <summary>
/// Rules nobody can loosen: not an override, not the loosen block, not the global policy (the floor rules,
/// and the hosts never on the CONNECT allowlist).
/// </summary>
public static class FloorRules
{
    /// <summary>The model vendors' API and sign-in hosts.</summary>
    public static IReadOnlyList<string> VendorHosts { get; } =
    [
        "api.anthropic.com", "platform.claude.com", "claude.ai", "claude.com",
        "api.x.ai", "cli-chat-proxy.grok.com", "code.grok.com", "auth.x.ai", "accounts.x.ai", "grok.com",
    ];

    /// <summary>
    /// The vendors' registrable domains. Any host equal to or under one of these is refused, so a sibling
    /// sign-in or API host (console.anthropic.com, a regional api.x.ai) cannot slip past the exact list.
    /// </summary>
    public static IReadOnlyList<string> VendorDomains { get; } = ["anthropic.com", "claude.ai", "claude.com", "x.ai", "grok.com"];

    /// <summary>The gateway's own virtual hosts; never a CONNECT target.</summary>
    public const string ReservedSuffix = "yawble.internal";

    /// <summary>A loosened timeout may not exceed this (24 h).</summary>
    public const int TimeoutCeilingSec = 24 * 60 * 60;

    /// <summary>The hard caps (not enforced): memory up to 75% of the machine, CPU up to the whole machine.</summary>
    public const int MemoryCeilingPercentOfMachine = 75;

    public const int CpuPercentCeiling = 100;
    public const int MaxProcessesCeiling = 4096;
    public const int MaxConcurrentAgentsCeiling = 64;

    /// <summary>Why a host may not be on any allowHosts list, or null when it may.</summary>
    public static string? HostProblem(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return "an empty host";
        var h = host.Trim();
        if (h != host || h != h.ToLowerInvariant()) return $"'{host}' must be a lower-case host name with no spaces";
        if (h.Contains('*')) return $"'{host}': wildcards are not supported; list each host";
        if (IPAddress.TryParse(h.Trim('[', ']'), out _)) return $"'{host}': IP addresses are not allowed; the policy is host-name based";
        if (Uri.CheckHostName(h) != UriHostNameType.Dns || !h.Contains('.')) return $"'{host}' is not a valid DNS host name";
        if (IsUnder(h, ReservedSuffix)) return $"'{host}' is a gateway virtual host (*.{ReservedSuffix})";
        foreach (var d in VendorDomains)
            if (IsUnder(h, d))
                return $"'{host}' is a model vendor's API or sign-in host; no one can add it (a floor rule)";
        return null;
    }

    public static bool IsVendorHost(string host) => VendorDomains.Any(d => IsUnder(host.Trim().ToLowerInvariant(), d));

    private static bool IsUnder(string host, string domain) =>
        host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);
}
