using System.Text.RegularExpressions;

namespace Harness.Host;

/// <summary>
/// A team's own named values, validated before they reach a child process's environment.
///
/// COORDINATION, NOT CONFINEMENT. These live in SQLite in plaintext, and every agent runs with
/// permissions bypassed and can read that file. What this decides is which variable is in a child's
/// environment, never which secrets a child could obtain. The rule that governs
/// <c>AgentDefinition.Env</c> carries over unchanged: only values every team admin in the tenant is
/// already entitled to.
/// </summary>
public static partial class TeamEnv
{
    /// <summary>The reserved prefix. A team may not set one, because these are how a container is
    /// told who it is and what credential it holds.</summary>
    public const string ReservedPrefix = "HARNESS_";

    /// <summary>At most this many entries. The whole map is handed to EVERY child process, and a
    /// Windows environment block is finite - an oversized one fails the spawn rather than the
    /// call, which presents as an agent that will not start for no stated reason.</summary>
    public const int MaxEntries = 64;

    /// <summary>At most this many characters in one value, for the same reason.</summary>
    public const int MaxValueChars = 4096;

    /// <summary>
    /// A POSIX-shaped environment variable name. An allowlist rather than a denylist of awkward
    /// characters, the same choice and for the same reason as <c>ContainerId.IsLegalName</c>: a
    /// denylist is only ever as current as the last sink somebody thought of, and this name is
    /// handed to a process launcher, written into a `.env`-shaped world by whatever the agent runs,
    /// and substituted into a prompt as `{env:NAME}`.
    /// </summary>
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex LegalName();

    /// <summary>The account a team's members bootstrap a test instance with.</summary>
    public const string AdminEmail = "TEST_ADMIN_EMAIL";

    /// <summary>Its password.</summary>
    public const string AdminPassword = "TEST_ADMIN_PASSWORD";

    /// <summary>
    /// What a brand-new team is born holding.
    ///
    /// A test account on a disposable instance is a COORDINATION TOKEN, not a secret: nobody needs
    /// to protect it, everybody needs to agree on it. Deciding it here - once, at creation - is what
    /// removes the human from the loop and closes the window in which a member meets an
    /// un-bootstrapped instance and invents something only it will ever know.
    ///
    /// GENERATED, not derived. A formula every instance shares would be a known credential on every
    /// instance forever, which is a poor thing to bake in even for test accounts. Per team, so two
    /// teams sharing an instance cannot sign in as each other.
    ///
    /// The address is built from the team's identifier and a `.local` suffix reserved for exactly
    /// this: it is unique per team, obviously not a real address, and readable by whoever finds it
    /// in a log. Lowercased because an email address is compared that way, and the identifier may
    /// carry capitals.
    ///
    /// Overwritable like any other entry. A team that must test against something real has these
    /// replaced by hand and nothing here is in the way.
    /// </summary>
    public static IReadOnlyDictionary<string, string> SeedFor(string teamId) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AdminEmail] = $"test-admin@{teamId.ToLowerInvariant()}.harness.local",

            // 192 bits, base64url so it survives a command line, a URL and a JSON string without
            // quoting - these get pasted into shells by agents. Comfortably past the 8-character
            // minimum the auth routes enforce, and there is no reason to be near it.
            [AdminPassword] = Convert.ToBase64String(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(24))
                .Replace('+', '-').Replace('/', '_').TrimEnd('='),
        };

    /// <summary>
    /// Validates and normalises. Throws <see cref="ArgumentException"/> naming the offending key,
    /// which the route turns into a 400.
    ///
    /// REFUSES rather than dropping or truncating. An entry silently discarded is a variable the
    /// person believes they set and the team does not have; a truncated value is a credential that
    /// fails to authenticate with nothing saying why. Both are worse than being told.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Validate(
        IReadOnlyDictionary<string, string>? env)
    {
        var validated = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (suppliedKey, suppliedValue) in env ?? new Dictionary<string, string>())
        {
            var key = suppliedKey?.Trim() ?? "";

            if (!LegalName().IsMatch(key))
            {
                throw new ArgumentException(
                    $"'{suppliedKey}' is not a legal environment variable name. Use letters, "
                    + "digits and underscores, starting with a letter or underscore.");
            }

            // Refused at the WRITE so the person is told, and dropped again at the MERGE
            // (AgentEnvironment) so a hand-edited row cannot slip one through. Both, not either:
            // repairing a database by hand is a supported operator action, and this prefix is what
            // carries a container's own credential.
            if (key.StartsWith(ReservedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"'{key}' is reserved. Names beginning '{ReservedPrefix}' are the platform's "
                    + "own and cannot be set by a team.");
            }

            if (!validated.TryAdd(key, suppliedValue ?? ""))
            {
                throw new ArgumentException($"'{key}' is set twice.");
            }

            if ((suppliedValue ?? "").Length > MaxValueChars)
            {
                throw new ArgumentException(
                    $"'{key}' is longer than {MaxValueChars} characters. The whole environment is "
                    + "handed to every child process this team launches.");
            }
        }

        if (validated.Count > MaxEntries)
        {
            throw new ArgumentException(
                $"A team may hold at most {MaxEntries} environment entries; {validated.Count} were "
                + "supplied.");
        }

        return validated;
    }
}
