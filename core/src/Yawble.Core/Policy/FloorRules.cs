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

    /// <summary>
    /// The registrable domains of the Codex and Copilot presets' own vendor hosts (AgentPresets.PassthroughHosts): a
    /// domain allow over one of them ("*.githubcopilot.com", "*.openai.com") is refused, so only that CLI's runs reach them.
    /// </summary>
    public static IReadOnlyList<string> PresetOnlyDomains { get; } = ["githubcopilot.com", "chatgpt.com", "openai.com", "oaiusercontent.com"];

    /// <summary>The gateway's own virtual hosts; never a CONNECT target.</summary>
    public const string ReservedSuffix = "yawble.internal";

    /// <summary>A loosened timeout may not exceed this (24 h).</summary>
    public const int TimeoutCeilingSec = 24 * 60 * 60;

    /// <summary>The hard caps (not enforced): memory up to 75% of the machine, CPU up to the whole machine.</summary>
    public const int MemoryCeilingPercentOfMachine = 75;

    public const int CpuPercentCeiling = 100;
    public const int MaxProcessesCeiling = 4096;
    public const int MaxConcurrentAgentsCeiling = 64;

    /// <summary>
    /// Why a host may not be on any allowHosts list, or null when it may. An entry is an exact host name or a domain
    /// allow: exactly one leading "*." over a registrable domain (eTLD+1 by the bundled Public Suffix List,
    /// <see cref="PublicSuffix"/>), e.g. "*.digicert.com". A domain allow matches every subdomain and never the apex
    /// (list the apex separately). Refused as patterns: bare "*", "**.x", "a.*.b", "*.com", "*.co.uk", "*.github.io"
    /// (a public suffix), "*.sub.example.com" (not the registrable domain), any domain under a PRIVATE (multi-tenant)
    /// suffix or with public suffixes under it (amazonaws.com), and any pattern over a vendor domain, the gateway's
    /// virtual hosts or a CLI preset's own vendor hosts. The pattern's domain is held to the exact-host rules too, plus
    /// plain ASCII letters, digits and hyphens with no xn-- label.
    /// </summary>
    public static string? HostProblem(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return "an empty host";
        var h = host.Trim();
        if (h != host || h != h.ToLowerInvariant()) return $"'{host}' must be a lower-case host name with no spaces";
        if (h.StartsWith("*.", StringComparison.Ordinal)) return PatternProblem(h, h[2..]);
        if (h.Contains('*'))
            return $"'{host}': the only wildcard is one leading '*.' over a registrable domain (*.example.com); otherwise list each host";
        return ExactProblem(h);
    }

    private static string? ExactProblem(string h)
    {
        if (IPAddress.TryParse(h.Trim('[', ']'), out _)) return $"'{h}': IP addresses are not allowed; the policy is host-name based";
        if (Uri.CheckHostName(h) != UriHostNameType.Dns || !h.Contains('.')) return $"'{h}' is not a valid DNS host name";
        if (IsUnder(h, ReservedSuffix)) return $"'{h}' is a gateway virtual host (*.{ReservedSuffix})";
        foreach (var d in VendorDomains)
            if (IsUnder(h, d))
                return $"'{h}' is a model vendor's API or sign-in host; no one can add it (a floor rule)";
        return null;
    }

    private static string? PatternProblem(string pattern, string domain)
    {
        if (domain.Contains('*')) return $"'{pattern}': a domain allow has exactly one leading '*.' and no other wildcard";
        // The floor's own refusals first, so the reason names them (not "use the registrable domain").
        if (IsUnder(domain, ReservedSuffix)) return $"'{pattern}' is over the gateway virtual hosts (*.{ReservedSuffix})";
        foreach (var d in VendorDomains)
            if (IsUnder(domain, d))
                return $"'{pattern}' is over a model vendor's API or sign-in hosts; no one can add it (a floor rule)";
        // A CLI preset's own vendor domains (Copilot's, Codex's) are added by the preset for that CLI's runs only; no
        // domain allow may cover them for every agent.
        foreach (var d in PresetOnlyDomains)
            if (IsUnder(domain, d))
                return $"'{pattern}' is over {d}, a vendor domain only a CLI preset adds for its own runs; no policy can add it as a domain allow (a floor rule)";
        // Then the list: exactly a registrable domain, not a tenant's space under a shared suffix.
        if (PublicSuffix.IsPlainName(domain) && PublicSuffix.PatternDomainProblem(domain) is { } q) return q;
        // A registrable domain cannot be over a vendor's domain; kept so a later list change cannot open one.
        foreach (var vendor in VendorHosts.Concat(Gateway.Vhosts.NeverConnect))
            if (IsUnder(vendor, domain))
                return $"'{pattern}' covers {vendor}, a model vendor's API or sign-in host; no one can add it (a floor rule)";
        if (ExactProblem(domain) is { } p) return $"'{pattern}': {p}";
        if (!PublicSuffix.IsPlainName(domain))
            return $"'{pattern}': a domain allow is plain ASCII letters, digits and hyphens with no xn-- label";
        return null;
    }
    public static bool IsVendorHost(string host) => VendorDomains.Any(d => IsUnder(host.Trim().ToLowerInvariant(), d));

    private static bool IsUnder(string host, string domain) =>
        host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);
}
