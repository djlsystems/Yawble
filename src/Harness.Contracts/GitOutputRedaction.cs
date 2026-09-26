using System.Text.RegularExpressions;

namespace Harness.Contracts;

/// <summary>
/// TAKES CREDENTIALS OUT OF GIT'S OWN OUTPUT - AND OUT OF THE REPOSITORY URL THAT PRODUCED IT -
/// BEFORE EITHER REACHES THE MESSAGE LOG.
///
/// <para>
/// <b>IT IS NOT ONLY GIT'S OUTPUT, AND THAT WAS THE SECOND DEFECT.</b> The first pass put this in
/// front of <c>GitRunner</c>'s captured streams, which covered the push path and nothing else.
/// <c>RepoUrls.Validate</c> ACCEPTS USERINFO - it enforces scheme and derived folder name, so
/// <c>https://x-access-token:&lt;token&gt;@host/repo.git</c> is a legal, working, configured remote
/// and is how the platform's own clone authenticates - and <c>RepoSetupMessage.Line</c> then quoted
/// that URL verbatim into the instruction written to the Manager at TEAM CREATION, before anything
/// had pushed. Accepting the URL is correct; refusing it would refuse the only credential form the
/// clone can carry. Quoting it onto an append-only row is the defect, so the same redactor now
/// stands in front of the configured URL as well as in front of git's account of using it.
/// </para>
///
/// <para>
/// <b>AT THE WRITE, NEVER AT THE READ</b> - the rule <see cref="DiagnosticRedaction"/> already
/// records, applied to the one text this platform now produces that the diagnostics store does not.
/// The message log is append-only and is read back into other agents' prompts, so a credential that
/// lands on it is on it forever and travels.
/// </para>
///
/// <para>
/// <b>WHY A SECOND REDACTOR AND NOT JUST THE FIRST ONE.</b> The shape that matters here is a
/// credential in the USERINFO of a remote URL - <c>https://x-access-token:ghs_…@github.com/o/r.git</c>
/// - which git quotes verbatim into <c>fatal: unable to access '…'</c> on every failed push. That
/// text carries no <c>password=</c> name for rule 1 to find, and rule 2 only fires on a run of 32+
/// base64url characters carrying all three of upper, lower and digit: a short token, an all-lower
/// token, or one broken by a <c>.</c> or <c>-</c> survives it intact. Userinfo is redacted
/// STRUCTURALLY here - everything between <c>://</c> and <c>@</c>, whatever it looks like - and
/// <see cref="DiagnosticRedaction"/> then runs as the backstop it was written to be.
/// </para>
///
/// <para>
/// <b>THE HOST IS KEPT.</b> <c>https://[redacted]@github.com/o/r.git</c> still tells a person WHICH
/// remote refused them, which is the whole actionable content of the message. A redactor that ate
/// the URL would make every transport failure read the same and send the reader to the transcript,
/// where nothing is redacted at all.
/// </para>
///
/// <para>
/// <b>THE USERNAME GOES WITH THE PASSWORD, DELIBERATELY.</b> A username is not usually a secret, but
/// <c>x-access-token</c>, <c>oauth2</c> and <c>PRIVATE-TOKEN</c> schemes put the CREDENTIAL in the
/// user half and a constant in the password half, and some put it in the user half alone
/// (<c>https://ghp_…@github.com/…</c>). Keeping the user half would preserve exactly the tokens this
/// exists to remove. There is no rule that can tell a username from a token by looking at it, so
/// neither is kept.
/// </para>
///
/// <para>
/// <b>SCP-STYLE <c>git@github.com:org/repo</c> IS LEFT ALONE</b> and that is not an oversight: that
/// form has no password field at all - it is an ssh login name, the secret is a key file on disk,
/// and redacting it would blank the one identifier in an ssh failure while removing nothing.
/// </para>
/// </summary>
public static partial class GitOutputRedaction
{
    /// <summary>What replaces a secret. <see cref="DiagnosticRedaction.Placeholder"/>'s spelling and
    /// not a second one, so a reader searching for redactions finds both with one query.</summary>
    public const string Placeholder = DiagnosticRedaction.Placeholder;

    /// <summary>
    /// How many lines of git output survive. Git says what went wrong on its first lines and then
    /// explains at length; this text is composed into a row that is kept forever and read back into
    /// prompts, so the essay is not worth its tokens. <see cref="DiagnosticRedaction.MaximumLength"/>
    /// still caps the characters underneath this.
    /// </summary>
    public const int MaximumLines = 8;

    /// <summary>The suffix a line-trimmed value carries, so nobody reads a cut-off message as a
    /// complete one.</summary>
    public const string Trimmed = "... and more, trimmed.";

    /// <summary>
    /// Redacts userinfo, applies <see cref="DiagnosticRedaction"/> as the backstop, then bounds the
    /// line count. Null and whitespace pass through untouched - "nothing to record" is not something
    /// to redact.
    /// </summary>
    public static string? Redact(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        // ORDER IS LOAD-BEARING. Userinfo first, because it is the one rule that fires on SHAPE
        // rather than on what the value looks like; the backstop then catches a named secret or a
        // credential-shaped run anywhere else in the same text.
        var redacted = DiagnosticRedaction.Redact(RedactUserInfo(text)) ?? string.Empty;

        return Bound(redacted);
    }

    /// <summary>
    /// THE STRUCTURAL HALF ALONE - userinfo out, and NOTHING ELSE TOUCHED. No backstop, no length
    /// cap, no line bound: the text comes back the same length it went in unless it actually carried
    /// a credential.
    ///
    /// <para>
    /// <b>FOR A CALLER WHOSE TEXT MUST SURVIVE INTACT.</b> <see cref="Redact"/> ends in
    /// <see cref="Bound"/> (8 lines) and <see cref="DiagnosticRedaction"/>'s 4,000-character ceiling,
    /// both of which are right for a short git sentence composed into a row and CATASTROPHIC for the
    /// two callers below: an addressed instruction is a whole prompt, routinely longer than either
    /// limit, and silently truncating one would break the product to close a leak. The repo routes
    /// are the other - they already bound to twenty lines with their own wording, and a second bound
    /// underneath would make theirs unreachable and their arithmetic a lie.
    /// </para>
    ///
    /// <para>
    /// <b>WHAT IT GIVES UP, SAID PLAINLY.</b> A <c>password=</c> or a credential-shaped run that is
    /// NOT in a URL's userinfo survives this. That is the trade: the shape a repository URL leaks is
    /// userinfo, and a redactor that mangles every long payload to catch a shape that cannot arrive
    /// by this route would be turned off within a week.
    /// </para>
    /// </summary>
    public static string? RedactUserInfo(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? text
            : UrlUserInfo().Replace(text, match => match.Groups["scheme"].Value + Placeholder + "@");

    private static string Bound(string text)
    {
        // Trim the trailing newline BEFORE counting: `GitRunner` appends one after the last real
        // line, and splitting without trimming turns it into a phantom final element.
        var normalized = text.Replace("\r\n", "\n").TrimEnd('\n');
        var lines = normalized.Split('\n');

        return lines.Length <= MaximumLines
            ? normalized
            : string.Join('\n', lines.Take(MaximumLines)) + '\n' + Trimmed;
    }

    /// <summary>
    /// The userinfo of an absolute URL: everything between <c>scheme://</c> and the <c>@</c> that
    /// ends it.
    ///
    /// <para>
    /// The run excludes <c>/</c>, whitespace and <c>@</c> itself, so it cannot reach past the
    /// authority into a path and cannot cross from one URL into a later one on the same line. A URL
    /// with no <c>@</c> does not match at all, which is why an ordinary remote is untouched.
    /// </para>
    /// </summary>
    [GeneratedRegex(
        @"(?<scheme>[A-Za-z][A-Za-z0-9+.\-]*://)[^/@\s]+@",
        RegexOptions.CultureInvariant)]
    private static partial Regex UrlUserInfo();
}
