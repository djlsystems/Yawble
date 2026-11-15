using System.Net;
using System.Reflection;

namespace Yawble.Core.Policy;

/// <summary>
/// The Public Suffix List (ICANN and PRIVATE sections), from a pinned copy embedded in the core
/// (Policy/public_suffix_list.dat, the list's VERSION and COMMIT in <see cref="Version"/> and <see cref="Commit"/>).
/// It is never fetched at run time: a new copy is a reviewed change to that file.
/// The registrable domain (eTLD+1) is what a domain allow ("*.example.com") is offered and checked over.
/// </summary>
public static class PublicSuffix
{
    /// <summary>The embedded resource's logical name.</summary>
    public const string ResourceName = "Yawble.Core.Policy.public_suffix_list.dat";

    private static readonly Lazy<Rules> List = new(() => Load(), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The pinned list's "// VERSION:" line, e.g. 2026-11-13_14-44-35_UTC.</summary>
    public static string Version => List.Value.Version;

    /// <summary>The pinned list's "// COMMIT:" line (publicsuffix/list).</summary>
    public static string Commit => List.Value.Commit;

    /// <summary>
    /// The host's registrable domain (eTLD+1, both sections of the list), lower-case; null for a host that is itself a
    /// public suffix (com, co.uk, github.io), a bare TLD, an IP address, an empty or malformed name, a name with a
    /// trailing dot or a wildcard, and any name that is not plain ASCII letters, digits and hyphens or has an xn-- label.
    /// </summary>
    public static string? RegistrableDomain(string host) => Match(host)?.Registrable;

    /// <summary>
    /// The domain allow that may be offered for this host, "*.&lt;eTLD+1&gt;", or null when only the exact host may be:
    /// the host has no registrable domain (<see cref="RegistrableDomain"/>), its registrable domain sits under a PRIVATE
    /// (multi-tenant) suffix such as github.io or herokuapp.com, other public suffixes sit under it (amazonaws.com, whose
    /// s3.amazonaws.com and the like are tenants' spaces), or the pattern fails another floor rule
    /// (<see cref="FloorRules.HostProblem"/>: a vendor domain, the gateway's virtual hosts, a preset-only host).
    /// The pattern covers subdomains only; the apex is not included.
    /// </summary>
    public static string? DomainPatternFor(string host)
    {
        var domain = RegistrableDomain(host);
        if (domain is null) return null;
        var pattern = "*." + domain;
        return FloorRules.HostProblem(pattern) is null ? pattern : null;
    }

    /// <summary>
    /// Why "*.<paramref name="domain"/>" may not be a domain allow as far as the list goes, or null when it may:
    /// <paramref name="domain"/> must be exactly a registrable domain, not under a PRIVATE suffix, with no public suffix under it.
    /// </summary>
    internal static string? PatternDomainProblem(string domain)
    {
        var m = Match(domain);
        if (m is null || m.Registrable != domain)
        {
            // A plain name with no registrable part is a public suffix or a bare TLD.
            if (m is null && IsPlainName(domain))
                return $"'*.{domain}': {domain} is a public suffix (anyone can register under it); give a registrable domain such as *.example.{domain}";
            return m is null
                ? $"'*.{domain}': a domain allow needs a registrable domain after '*.' (plain ASCII letters, digits and hyphens, no xn--)"
                : $"'*.{domain}': a domain allow is over the registrable domain only; use *.{m.Registrable} or list the host";
        }
        if (m.SuffixIsPrivate)
            return $"'*.{domain}': {m.Suffix} is a shared suffix anyone can own a subdomain of, so a domain allow is never offered under it; list the host";
        if (List.Value.HasSuffixUnder(domain))
            return $"'*.{domain}': other public suffixes sit under {domain} (tenants' spaces), so a domain allow over it is never offered; list the host";
        return null;
    }

    internal sealed record Result(string Suffix, string Registrable, bool SuffixIsPrivate);

    /// <summary>The longest matching rule (an exception rule wins); the implicit "*" when none matches.</summary>
    internal static Result? Match(string host)
    {
        if (string.IsNullOrEmpty(host)) return null;
        var h = host.ToLowerInvariant();
        if (!IsPlainName(h)) return null;
        var labels = h.Split('.');
        if (labels.Length < 2) return null;
        var rules = List.Value;

        // An exception rule ("!www.ck") prevails over every other rule: its name minus the leftmost label is the suffix.
        int suffixLabels = 1; // the implicit "*" rule (ICANN-like: an unlisted TLD)
        bool isPrivate = false;
        bool found = false;
        for (var n = labels.Length; n >= 1; n--)
            if (rules.Exceptions.TryGetValue(string.Join('.', labels[^n..]), out var exPrivate))
            {
                suffixLabels = n - 1;
                isPrivate = exPrivate;
                found = true;
                break;
            }
        // Otherwise the longest matching rule: a plain rule, or a wildcard ("*.ck" matches any one label over ck).
        for (var n = labels.Length; n >= 1 && !found; n--)
        {
            if (rules.Normal.TryGetValue(string.Join('.', labels[^n..]), out var p)) { suffixLabels = n; isPrivate = p; found = true; }
            else if (n >= 2 && rules.Wildcards.TryGetValue(string.Join('.', labels[^(n - 1)..]), out var wp)) { suffixLabels = n; isPrivate = wp; found = true; }
        }
        if (suffixLabels >= labels.Length) return null;
        var suffix = string.Join('.', labels[^suffixLabels..]);
        var registrable = string.Join('.', labels[^(suffixLabels + 1)..]);
        return new Result(suffix, registrable, isPrivate);
    }

    /// <summary>ASCII letters, digits and hyphens in non-empty labels of at most 63, no label starting or ending with a hyphen, no xn--, not an IP.</summary>
    internal static bool IsPlainName(string h)
    {
        if (h.Length is 0 or > 253) return false;
        if (IPAddress.TryParse(h.Trim('[', ']'), out _)) return false;
        foreach (var label in h.Split('.'))
        {
            if (label.Length is 0 or > 63) return false;
            if (label[0] == '-' || label[^1] == '-') return false;
            if (label.StartsWith("xn--", StringComparison.Ordinal)) return false;
            foreach (var ch in label)
                if (!(ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')) return false;
        }
        // An all-numeric last label is an address form (1.2.3.4 is caught above; 1.2.3 is not a host name either).
        return !h[(h.LastIndexOf('.') + 1)..].All(char.IsAsciiDigit);
    }

    internal sealed class Rules
    {
        /// <summary>Rule → whether it is in the PRIVATE section.</summary>
        public Dictionary<string, bool> Normal { get; } = new(StringComparer.Ordinal);

        /// <summary>"*.ck" is stored as "ck".</summary>
        public Dictionary<string, bool> Wildcards { get; } = new(StringComparer.Ordinal);

        /// <summary>"!www.ck" is stored as "www.ck".</summary>
        public Dictionary<string, bool> Exceptions { get; } = new(StringComparer.Ordinal);

        /// <summary>Every name with a rule strictly under it (s3.amazonaws.com puts amazonaws.com here).</summary>
        public HashSet<string> Parents { get; } = new(StringComparer.Ordinal);

        public string Version { get; set; } = "";
        public string Commit { get; set; } = "";

        public bool HasSuffixUnder(string name) => Parents.Contains(name);
    }

    internal static Rules Parse(TextReader reader)
    {
        var rules = new Rules();
        bool? inPrivate = null;
        while (reader.ReadLine() is { } raw)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("//", StringComparison.Ordinal))
            {
                if (line.Contains("===BEGIN ICANN DOMAINS===", StringComparison.Ordinal)) inPrivate = false;
                else if (line.Contains("===BEGIN PRIVATE DOMAINS===", StringComparison.Ordinal)) inPrivate = true;
                else if (line.Contains("===END ", StringComparison.Ordinal)) inPrivate = null;
                else if (line.StartsWith("// VERSION:", StringComparison.Ordinal)) rules.Version = line["// VERSION:".Length..].Trim();
                else if (line.StartsWith("// COMMIT:", StringComparison.Ordinal)) rules.Commit = line["// COMMIT:".Length..].Trim();
                continue;
            }
            if (inPrivate is not { } priv) continue; // outside both sections: not a rule
            // A rule is the first whitespace-delimited word; names in other scripts (Unicode) never match an ASCII host.
            var rule = line.Split([' ', '\t'], 2)[0].ToLowerInvariant();
            string name;
            if (rule.StartsWith('!')) { name = rule[1..]; rules.Exceptions[name] = priv; }
            else if (rule.StartsWith("*.", StringComparison.Ordinal)) { name = rule[2..]; rules.Wildcards[name] = priv; }
            else { name = rule; rules.Normal[name] = priv; }
            // Every proper ancestor of the rule's name (and, for a wildcard, the name itself) has a suffix under it.
            if (rule.StartsWith("*.", StringComparison.Ordinal)) rules.Parents.Add(name);
            for (var dot = name.IndexOf('.'); dot >= 0; dot = name.IndexOf('.', dot + 1))
                rules.Parents.Add(name[(dot + 1)..]);
        }
        return rules;
    }

    private static Rules Load()
    {
        using var stream = typeof(PublicSuffix).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"the embedded Public Suffix List ({ResourceName}) is missing from {typeof(PublicSuffix).Assembly.GetName().Name}");
        using var reader = new StreamReader(stream);
        var rules = Parse(reader);
        if (rules.Normal.Count == 0) throw new InvalidOperationException("the embedded Public Suffix List has no rules");
        return rules;
    }
}
