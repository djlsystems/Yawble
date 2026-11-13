namespace Yawble.Core.Integrations;

/// <summary>
/// The universal credential floor: for EVERY CLI, known or not, a call whose command words or flag names say it
/// reveals or changes a credential is refused before any rule, tick or model answer is looked at. Word based, not substring:
/// each argument is split on every non-alphanumeric character (get-session-token -> get session token) and compared with a
/// table of stems. Words of a flag's value (--flag=value, or a quoted free-text argument that holds a space) are not checked;
/// every other argument is, wherever it stands, so "kubectl -n ns get secret x" is caught as well as "kubectl get secret x".
/// A false positive (docker login, gh secret list, --max-tokens) costs the person a terminal run; a false negative leaks a
/// credential: floors are never loosened.
/// </summary>
public static class CredentialFloor
{
    /// <summary>Stems that hit a word that STARTS with them (secretsmanager, tokens, credentials).</summary>
    private static readonly string[] PrefixStems =
    [
        "token", "secret", "password", "passwd", "passphrase", "credential", "apikey", "privatekey", "sshkey", "accesskey", "masterkey",
        "signingkey", "keyring", "kubeconfig", "connectionstring", "bearer", "cookie", "jwt", "oauth", "identitytoken", "decrypt", "unseal", "reveal",
    ];

    /// <summary>Short stems that hit only a whole word (so sasha, otpt are not hit).</summary>
    private static readonly string[] ExactStems = ["sas", "cred", "otp", "mfa"];

    /// <summary>Two adjacent words; "a|b" alternatives per word.</summary>
    private static readonly (string First, string Second)[] Pairs =
    [
        ("access", "key|keys"), ("api", "key|keys"), ("private", "key|keys"), ("ssh", "key|keys"), ("secret", "key|keys"), ("account", "key|keys"),
        ("connection", "string|strings"), ("keys", "list"), ("list", "keys"), ("get", "key"), ("show", "key"), ("with", "decryption"),
        ("show", "token"), ("print", "access"), ("print", "identity"), ("configure", "get|list"), ("login", "password"), ("get", "login"),
        ("for", "rbac"),
    ];

    /// <summary>The first floor hit in the arguments (e.g. "token", "configure get"), or null.</summary>
    public static string? Check(IReadOnlyList<string> args)
    {
        var sequence = new List<string>();
        var flagWords = new List<string>();
        foreach (var raw in args)
        {
            var arg = (raw ?? "").Trim();
            if (arg.Length == 0) continue;
            if (arg.StartsWith('-'))
            {
                var name = arg.Split('=', 2)[0];
                var words = Words(name);
                if (Single(words) is { } hit) return hit;
                if (PairHit(words) is { } pair) return pair;
                flagWords.AddRange(words);
                continue;
            }
            if (arg.Any(char.IsWhiteSpace)) continue; // free text ("token refresh" as a search): a value, not a command word
            sequence.AddRange(Words(arg));
        }
        if (Single(sequence) is { } word) return word;
        if (PairHit(sequence) is { } p) return p;
        // "kubectl config view --raw": the raw flag turns a harmless view into one that prints credentials.
        if (sequence.Contains("config") && sequence.Contains("view") && flagWords.Contains("raw")) return "config view --raw";
        return null;
    }

    /// <summary>Check a pattern (a rule's text): its literal words, with * as a break between them.</summary>
    public static string? CheckPattern(string pattern) =>
        Check(pattern.Split('*', StringSplitOptions.RemoveEmptyEntries).SelectMany(s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToList());

    public static string Reason(string word) => $"floor-word:{word}";

    private static List<string> Words(string arg) =>
        System.Text.RegularExpressions.Regex.Split(arg.ToLowerInvariant(), "[^a-z0-9]+").Where(w => w.Length > 0).ToList();

    private static string? Single(IEnumerable<string> words)
    {
        foreach (var w in words)
        {
            foreach (var s in PrefixStems) if (w.StartsWith(s, StringComparison.Ordinal)) return w;
            foreach (var s in ExactStems) if (w == s) return w;
        }
        return null;
    }

    private static string? PairHit(IReadOnlyList<string> words)
    {
        for (var i = 0; i + 1 < words.Count; i++)
            foreach (var (a, b) in Pairs)
                if (a == words[i] && b.Split('|').Contains(words[i + 1])) return $"{words[i]} {words[i + 1]}";
        return null;
    }
}
