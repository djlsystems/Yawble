using System.Text.RegularExpressions;

namespace Harness.Contracts;

/// <summary>
/// TAKES SECRETS OUT OF TEXT BEFORE IT IS STORED.
///
/// <para>
/// <b>AT THE WRITE, NEVER AT THE READ.</b> Redacting on the way out leaves the plaintext on disk,
/// where the pre-migration backup, `VACUUM INTO`, a copied data root and anyone who can read the
/// file all have it - and the read that forgets is the read nobody notices. A credential has
/// reached this product's append-only log once already, quoted into a payload by an agent, and the
/// lesson recorded then was exactly this.
/// </para>
///
/// <para>
/// <b>IT IS A BACKSTOP AND NOT A LICENCE.</b> The first rule is still that a call site does not put
/// a secret in a diagnostic row - no environment dumps, no request bodies, no connection strings.
/// This exists because the one text nobody controls is an exception MESSAGE, which is composed by
/// whatever threw and routinely quotes the input that broke it.
/// </para>
///
/// <para>
/// TWO RULES, AND THE FIRST IS THE ONE THAT DOES THE WORK:
/// </para>
///
/// <list type="number">
///   <item>
///     <b>A NAMED VALUE.</b> `password=`, `token:`, `X-Api-Key: `, `Authorization: Bearer `,
///     `HARNESS_KEY=` - the name is kept and the value replaced, so the row still says WHICH
///     secret was there. This catches a hex-shaped or short secret that rule 2 cannot see.
///   </item>
///   <item>
///     <b>A CREDENTIAL'S SHAPE.</b> A run of 32 or more characters from the base64url alphabet that
///     carries upper case, lower case AND a digit. That is this product's own credential exactly -
///     `Base64UrlEncode(RandomNumberGenerator.GetBytes(32))`, 43 characters - and it is deliberately
///     narrow so it does not eat the text around it: a Windows path breaks on `\`, `/`, `.` and `:`
///     so no segment survives the run; a git sha and any hex digest are one case and are KEPT,
///     which matters because this store records commits and step ids; a long kebab-case identifier
///     is one case and is kept.
///   </item>
/// </list>
///
/// <para>
/// <b>THE LIMIT IS STATED RATHER THAN HIDDEN:</b> a secret that is all one case, is not named, and
/// is under 32 characters survives both rules. Rule 1 is what covers it in practice, and the answer
/// to the rest is that a call site must not pass such a thing in the first place. A redactor
/// presented as a guarantee is worse than one presented as a net.
/// </para>
/// </summary>
public static partial class DiagnosticRedaction
{
    /// <summary>What replaces a secret. One spelling, so a reader can search for it and so a test
    /// can assert on it.</summary>
    public const string Placeholder = "[redacted]";

    /// <summary>
    /// A CEILING ON ONE FIELD'S LENGTH, and it is a redaction concern rather than a formatting one.
    ///
    /// The text nobody controls is an exception message, and the ones that run to megabytes are
    /// dumps - a serialised request, an environment, a whole response body. Those are precisely the
    /// texts most likely to carry something neither rule above can see. Truncating is not a
    /// guarantee either, but an unbounded field in the highest-volume store this product has is a
    /// bound nobody set.
    /// </summary>
    public const int MaximumLength = 4_000;

    /// <summary>The suffix a truncated value carries, so nobody reads a cut-off message as a
    /// complete one.</summary>
    public const string Truncated = "... [truncated]";

    /// <summary>
    /// Redacts, then truncates. Null and whitespace pass through untouched - "nothing to record" is
    /// not something to redact.
    /// </summary>
    public static string? Redact(string? text)
    {
        var redacted = RedactWithoutLimit(text);
        if (string.IsNullOrWhiteSpace(redacted)) return redacted;

        return redacted.Length <= MaximumLength
            ? redacted
            : redacted[..(MaximumLength - Truncated.Length)] + Truncated;
    }

    /// <summary>
    /// The same two rules with no ceiling on length - for text that is a run's OUTPUT rather than a
    /// diagnostic, where cutting it off would lose the work itself.
    /// </summary>
    public static string? RedactWithoutLimit(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var redacted = NamedSecret().Replace(text, match =>
            match.Groups["name"].Value + match.Groups["separator"].Value + Placeholder);

        return CredentialShaped().Replace(redacted, match =>
            LooksRandom(match.Value) ? Placeholder : match.Value);
    }

    /// <summary>
    /// Whether a run from the base64url alphabet carries upper case, lower case AND a digit.
    ///
    /// Done in code rather than in the pattern because the three lookaheads that express it are
    /// unreadable, and this is the one line of the rule somebody will need to reason about: it is
    /// what keeps a git sha, a step id and a kebab-case folder name out of the placeholder.
    /// </summary>
    private static bool LooksRandom(string run)
    {
        var upper = false;
        var lower = false;
        var digit = false;

        foreach (var character in run)
        {
            if (char.IsAsciiLetterUpper(character)) upper = true;
            else if (char.IsAsciiLetterLower(character)) lower = true;
            else if (char.IsAsciiDigit(character)) digit = true;

            if (upper && lower && digit) return true;
        }

        return false;
    }

    /// <summary>
    /// A secret introduced by name.
    ///
    /// <para>
    /// THE SEPARATOR IS REQUIRED TO BE `:` OR `=`, and that is what keeps this off ordinary prose:
    /// "an authorization failure" is a sentence, `authorization: abc` is a header. The optional
    /// closing quote before it is what lets the same pattern read `"password": "hunter2"` out of a
    /// JSON body, where the name arrives quoted.
    /// </para>
    ///
    /// <para>
    /// `Bearer` IS TWO THINGS HERE, and it has to be. The one header shape that carries a live
    /// credential is `Authorization: Bearer xyz`, where the VALUE after the separator is the word
    /// `Bearer` and the secret is the token after THAT - so the separator swallows an optional
    /// `bearer` and the scheme survives in the output while the token does not. Written without
    /// that, this pattern redacted the word `Bearer` and left the credential standing, which is the
    /// worst of both outcomes and is how this spec was found. The second alternative catches a
    /// `Bearer xyz` that arrives with no header name in front of it.
    /// </para>
    ///
    /// <para>
    /// The value form covers a quoted JSON string, a single-quoted one and a bare run to the next
    /// whitespace, comma, semicolon or closing brace - which between them are how a secret appears
    /// in a JSON body, a connection string, a header dump and a `KEY=value` environment line.
    /// </para>
    /// </summary>
    [GeneratedRegex(
        """
        (?ix)
        (?: \b (?<name> password | passwd | pwd | secret | token | api[_-]?key | apikey
                | credential | authorization | x-api-key | harness_key
                | connection[_-]?string | client[_-]?secret | private[_-]?key )
              (?<separator> "? \s* [:=] \s* (?: bearer \s+ )? )
          | \b (?<name> bearer ) (?<separator> \s+ ) )
        (?: " [^"]* " | ' [^']* ' | [^\s,;}"']+ )
        """,
        RegexOptions.CultureInvariant)]
    private static partial Regex NamedSecret();

    /// <summary>A run from the base64url alphabet long enough to be a credential. Whether it
    /// actually is one is decided by <see cref="LooksRandom"/>.</summary>
    [GeneratedRegex("[A-Za-z0-9_-]{32,}", RegexOptions.CultureInvariant)]
    private static partial Regex CredentialShaped();
}
