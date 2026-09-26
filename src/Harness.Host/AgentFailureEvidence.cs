using System.Globalization;
using System.Text.RegularExpressions;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// What one failure FINDING is: the class, and the moment the provider said to come back when it
/// said one. Null <see cref="RetryAfter"/> is "nobody stated one", never "come back now".
/// </summary>
public sealed record FailureFinding(string FailureClass, DateTimeOffset? RetryAfter = null);

/// <summary>
/// WHAT A BRAND'S CLI SAYS WHEN IT REFUSES, per brand, keyed by the SAME
/// <c>AgentLaunch.UsageFormat</c> that already says how that brand reports usage.
///
/// <para>
/// THE EVIDENCE IS PER BRAND AND THE KEY IS ALREADY THERE. Usage parsing is per brand
/// through `UsageFormat`, and a preset without one is honestly silent about usage; classification
/// belongs in the same place with the same property, so a preset this table has no entry for yields
/// NOTHING and the container files the failure as <see cref="FailureClasses.Unknown"/>. That is the
/// design decision this file exists to hold. `UsageFormat` is already set on every built-in preset,
/// so keying on it needs no second catalog field naming a failure format.
/// </para>
///
/// <para>
/// IT ONLY EVER ANSWERS FROM EVIDENCE. There is no "non-zero exit means agent-fault" rule here and
/// there must not be: the class decides whether the platform spends money by itself, `unknown` is
/// the default, and a table that answered for every failure would make `unknown` unreachable and
/// this whole taxonomy a guess wearing a name. A brand that printed nothing recognisable produces
/// null, which is the same answer a brand this table has never heard of produces.
/// </para>
///
/// <para>
/// IT DOES NOT REBUILD THE DETECTION LAYER. `EnvelopeFailure` still tells
/// a 429 from a crash inside an envelope that exited 0; this is given that sentence along with the
/// rest of the output and says what KIND it was - the answer that detection had nowhere to put.
/// </para>
/// </summary>
public static partial class AgentFailureEvidence
{
    /// <summary>
    /// How long a rate failure backs off for when the provider refused without saying how long.
    ///
    /// THE PLATFORM MAY CHOOSE THIS ONE AND MAY NOT CHOOSE A QUOTA'S. The failure taxonomy words
    /// rate as "backs off and retries on a SHORTER HORIZON" - a behaviour whose delay is ours - and
    /// quota as "resumes at the STATED RESET", which is a moment only the provider knows. So a rate
    /// refusal with no retry-after still gets a resume, and a quota stop with no reset time gets
    /// none: inventing the second would be this platform deciding when somebody's budget renews.
    /// </summary>
    public static readonly TimeSpan DefaultRateBackoff = TimeSpan.FromMinutes(5);

    /// <summary>
    /// One rule: a pattern that identifies a class, in the words one brand actually prints.
    /// </summary>
    /// <param name="Pattern">Matched against stdout and stderr together, case-insensitively.</param>
    /// <param name="FailureClass">What that evidence means.</param>
    private sealed record Rule(Regex Pattern, string FailureClass);

    /// <summary>
    /// THE BRANDS, AND NOTHING BUT THE BRANDS. Every key here is a `UsageFormat` value
    /// `ProcessAgentRunner` already knows; adding a brand means adding its sentences here and
    /// nowhere else.
    ///
    /// ORDER MATTERS WITHIN A BRAND. Rate is tested before quota wherever both could match the same
    /// line, because "rate limit" and "usage limit" appear together in several providers' refusals
    /// and the narrower evidence has to win; the per-brand lists below say which is which.
    /// </summary>
    private static readonly Dictionary<string, IReadOnlyList<Rule>> ByBrand =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // A typical refusal: "You've hit your monthly spend limit - raise it at
            // claude.ai/settings/usage - your session limit resets 12am (America/New_York)",
            // exit 1. The reset time is in the payload and has to be read from it.
            ["claude-json"] =
            [
                new(ClaudeRate(), FailureClasses.Rate),
                new(ClaudeQuota(), FailureClasses.Quota),
                new(Transport(), FailureClasses.Transport),
            ],

            ["codex-total"] =
            [
                new(CodexRate(), FailureClasses.Rate),
                new(CodexQuota(), FailureClasses.Quota),
                new(Transport(), FailureClasses.Transport),
            ],

            ["copilot-usage-file"] =
            [
                new(CopilotRate(), FailureClasses.Rate),
                new(CopilotQuota(), FailureClasses.Quota),
                new(Transport(), FailureClasses.Transport),
            ],

            ["grok-json"] =
            [
                new(GrokRate(), FailureClasses.Rate),
                new(GrokQuota(), FailureClasses.Quota),
                new(Transport(), FailureClasses.Transport),
            ],

            // THE ONE BRAND WHOSE ENVELOPE SAYS SO IN A FIELD. agy can answer `status: ERROR` with
            // `RESOURCE_EXHAUSTED (code 429)` and still exit 0 - which is why
            // `EnvelopeFailure` exists at all. The envelope's own error text reaches this the same
            // way the rest of the output does.
            ["antigravity-json"] =
            [
                new(AntigravityRate(), FailureClasses.Rate),
                new(AntigravityQuota(), FailureClasses.Quota),
                new(Transport(), FailureClasses.Transport),
            ],
        };

    /// <summary>
    /// What this brand's own words say went wrong, or NULL when nothing here recognised them.
    ///
    /// Null is the ordinary answer and the caller turns it into <see cref="FailureClasses.Unknown"/>
    /// - see this class's own doc comment for why there is no catch-all rule.
    /// </summary>
    /// <param name="usageFormat">The preset's `UsageFormat`; null or unknown yields null.</param>
    /// <param name="text">
    /// Everything the run said - stdout, stderr, and an envelope's own error field. One string
    /// rather than three parameters because several CLIs write their refusal to whichever of the
    /// three they feel like, and a rule that had to name the stream would miss it there.
    /// </param>
    /// <param name="now">Read once by the caller so a resume time and the run's own clock agree.</param>
    public static FailureFinding? Classify(string? usageFormat, string? text, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(usageFormat) || string.IsNullOrWhiteSpace(text)) return null;
        if (!ByBrand.TryGetValue(usageFormat, out var rules)) return null;

        foreach (var rule in rules)
        {
            if (!rule.Pattern.IsMatch(text)) continue;

            var stated = RetryAfterIn(text, now);

            // RATE MAY FALL BACK, QUOTA MAY NOT. See DefaultRateBackoff.
            var retryAfter = rule.FailureClass switch
            {
                FailureClasses.Rate => stated ?? now + DefaultRateBackoff,
                FailureClasses.Quota => stated,
                _ => null,
            };

            return new FailureFinding(rule.FailureClass, retryAfter);
        }

        return null;
    }

    /// <summary>
    /// THE MOMENT THE PROVIDER NAMED, in whichever of the four shapes it named it, or null.
    ///
    /// The shapes are shared across brands rather than declared per brand, because they are: an
    /// epoch stamp, a wall-clock reset in a named zone, an HTTP `retry-after`, and prose. A brand
    /// that prints none of them still classifies - it simply carries no time, which for a quota
    /// means no automatic resume.
    ///
    /// NEVER RETURNS A MOMENT IN THE PAST. A reset that has already passed is a run that may go
    /// again now, and a resume scheduled for yesterday would fire on the next sweep tick - which is
    /// the same thing, said without a lie in the field.
    /// </summary>
    internal static DateTimeOffset? RetryAfterIn(string text, DateTimeOffset now)
    {
        if (EpochStamp().Match(text) is { Success: true } epoch
            && long.TryParse(epoch.Groups[1].Value, CultureInfo.InvariantCulture, out var seconds))
        {
            return AtOrAfter(DateTimeOffset.FromUnixTimeSeconds(seconds), now);
        }

        if (ResetsAt().Match(text) is { Success: true } resets && ZonedReset(resets, now) is { } at)
        {
            return at;
        }

        if (RetryAfterSeconds().Match(text) is { Success: true } after
            && int.TryParse(after.Groups[1].Value, CultureInfo.InvariantCulture, out var delay))
        {
            return now.AddSeconds(delay);
        }

        if (TryAgainIn().Match(text) is { Success: true } prose && Duration(prose.Groups[1].Value) is { } span)
        {
            return now + span;
        }

        return null;
    }

    /// <summary>
    /// "resets 12am (America/New_York)" as an instant: the NEXT time that wall clock comes round in
    /// that zone.
    ///
    /// THE ZONE IS THE PROVIDER'S AND NOT THIS MACHINE'S, which is the whole reason this is not a
    /// `DateTime.Parse`. A Host in London resuming a New York account's midnight five hours early
    /// spends against a budget that has not renewed, and the failure it gets back looks exactly like
    /// the one it was resuming from.
    ///
    /// A ZONE THIS MACHINE CANNOT RESOLVE YIELDS NULL rather than falling back to local time. There
    /// is no honest fallback: local time is a different moment, and a quota with no readable reset
    /// is a quota with no automatic resume, which is the state this design is already comfortable
    /// with.
    /// </summary>
    private static DateTimeOffset? ZonedReset(Match match, DateTimeOffset now)
    {
        if (!int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var hour)) return null;

        var minute = match.Groups[2].Success
            && int.TryParse(match.Groups[2].Value, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;

        var meridiem = match.Groups[3].Value;

        if (meridiem.Length > 0)
        {
            if (hour is < 1 or > 12) return null;

            hour = meridiem.StartsWith('p') || meridiem.StartsWith('P')
                ? hour == 12 ? 12 : hour + 12
                : hour == 12 ? 0 : hour;
        }

        if (hour is < 0 or > 23 || minute is < 0 or > 59) return null;

        TimeZoneInfo zone;

        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(match.Groups[4].Value.Trim());
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }

        var local = TimeZoneInfo.ConvertTime(now, zone);
        var candidate = new DateTime(local.Year, local.Month, local.Day, hour, minute, 0, DateTimeKind.Unspecified);

        for (var day = 0; day < 2; day++)
        {
            var when = candidate.AddDays(day);

            // A wall clock that does not exist - the hour a zone skips going into summer time -
            // is not a moment anything can wait until. The next day's is.
            if (zone.IsInvalidTime(when)) continue;

            var instant = new DateTimeOffset(when, zone.GetUtcOffset(when));

            if (instant > now) return instant;
        }

        return null;
    }

    /// <summary>"2h13m", "45 seconds", "5 min" as a span, or null when none of it parsed.</summary>
    private static TimeSpan? Duration(string text)
    {
        var total = TimeSpan.Zero;
        var any = false;

        foreach (Match part in DurationPart().Matches(text))
        {
            if (!int.TryParse(part.Groups[1].Value, CultureInfo.InvariantCulture, out var value)) continue;

            var unit = part.Groups[2].Value.ToLowerInvariant();

            total += unit[0] switch
            {
                'h' => TimeSpan.FromHours(value),
                'm' => TimeSpan.FromMinutes(value),
                _ => TimeSpan.FromSeconds(value),
            };

            any = true;
        }

        return any && total > TimeSpan.Zero ? total : null;
    }

    /// <summary>
    /// A stated moment, never earlier than now.
    ///
    /// A stamp that has already passed is a run that may go again NOW, so this clamps rather than
    /// discarding it: discarding would turn "the provider told us when, and it was five minutes ago"
    /// into "the provider said nothing", which is the one thing a null in this field is supposed to
    /// mean.
    /// </summary>
    private static DateTimeOffset AtOrAfter(DateTimeOffset at, DateTimeOffset now) =>
        at > now ? at : now;

    // ---------------------------------------------------------------------------------------------
    // THE SENTENCES. Every pattern below is evidence a provider prints; none of them is a catch-all,
    // and a failure matching none of them is `unknown` on purpose.

    /// <summary>
    /// **MATCH ON THE WORDS, NEVER ON THE PUNCTUATION.** Wherever a pattern below has to cross the
    /// gap between two words, it crosses it with THIS and never with a literal character.
    ///
    /// THE PROVIDER'S SENTENCE IS NOT ASCII ON THE WIRE. The quotation above uses ordinary
    /// hyphens as an ILLUSTRATION of the wording, not a transcript of the bytes: a logged payload
    /// can render those dashes as U+FFFD, because what the provider prints is an en or em dash and
    /// whatever read it could not represent it. A pattern anchored on "limit - raise it", or on
    /// any one dash, passes every test written from an ASCII quotation and then fails on real
    /// traffic.
    ///
    /// So this spans space, underscore, colon, ASCII hyphen, the whole U+2010..U+2015 dash block,
    /// the minus sign, and U+FFFD itself. WRITTEN AS ESCAPES rather than as the characters, so the
    /// rule survives a tool that reads this file as anything but UTF-8 - which is the same accident
    /// that produces a U+FFFD in the first place.
    ///
    /// It spans BETWEEN words that carry the meaning, and nowhere else: no rule below is ever
    /// satisfied by punctuation alone.
    /// </summary>
    private const string Gap = @"[\s_:\-\u2010-\u2015\u2212\uFFFD]";

    /// <summary>
    /// Claude's spend and session limits. "monthly spend limit" and "session limit resets" are the
    /// measured wording; "usage limit reached" is the form its CLI prints with an epoch
    /// stamp after a pipe, and a credit balance is the prepaid spelling of the same fact.
    /// </summary>
    [GeneratedRegex(
        @"(spend" + Gap + @"+limit"
        + @"|usage" + Gap + @"+limit" + Gap + @"+reached"
        + @"|session" + Gap + @"+limit"
        + @"|monthly" + Gap + @"+limit"
        + @"|credit" + Gap + @"+balance(" + Gap + @"+is)?" + Gap + @"+too" + Gap + @"+low"
        + @"|insufficient" + Gap + @"+(credit|quota))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClaudeQuota();

    /// <summary>
    /// Claude's too-much-too-fast refusals. AHEAD OF the quota rule: `rate_limit_error` and a
    /// 429 both appear in bodies that also carry the word "limit".
    /// </summary>
    [GeneratedRegex(
        @"(rate" + Gap + @"*limit|\b429\b|overloaded_error|too" + Gap + @"+many" + Gap + @"+requests)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClaudeRate();

    [GeneratedRegex(
        @"(usage" + Gap + @"+limit"
        + @"|quota" + Gap + @"+exceeded"
        + @"|exceeded" + Gap + @"+your" + Gap + @"+(usage|plan)"
        + @"|out" + Gap + @"+of" + Gap + @"+credits"
        + @"|billing" + Gap + @"+(hard" + Gap + @"+)?limit)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CodexQuota();

    [GeneratedRegex(
        @"(rate" + Gap + @"*limit|\b429\b|too" + Gap + @"+many" + Gap + @"+requests)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CodexRate();

    [GeneratedRegex(
        @"(premium" + Gap + @"+request"
        + @"|quota" + Gap + @"+exceeded"
        + @"|monthly" + Gap + @"+(usage|request)" + Gap + @"+limit"
        + @"|exceeded" + Gap + @"+your" + Gap + @".{0,40}allowance)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CopilotQuota();

    [GeneratedRegex(
        @"(rate" + Gap + @"*limit|\b429\b|too" + Gap + @"+many" + Gap + @"+requests)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CopilotRate();

    [GeneratedRegex(
        @"(credits?" + Gap + @"+(exhausted|remaining)"
        + @"|quota" + Gap + @"+exceeded|usage" + Gap + @"+limit|billing)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GrokQuota();

    [GeneratedRegex(
        @"(rate" + Gap + @"*limit|\b429\b|too" + Gap + @"+many" + Gap + @"+requests)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GrokRate();

    [GeneratedRegex(
        @"(quota" + Gap + @"+exceeded|usage" + Gap + @"+limit|billing"
        + @"|out" + Gap + @"+of" + Gap + @"+credits)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AntigravityQuota();

    /// <summary>
    /// `RESOURCE_EXHAUSTED (code 429)` - the envelope failure this exists for. gRPC's own spelling, which is what the agy CLI reports.
    /// </summary>
    [GeneratedRegex(
        @"(RESOURCE_EXHAUSTED|rate" + Gap + @"*limit|\b429\b|too" + Gap + @"+many" + Gap + @"+requests)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AntigravityRate();

    /// <summary>
    /// Network, DNS, socket - the same evidence whatever is printing it, because these are the
    /// operating system's words rather than a provider's. SHARED BY EVERY BRAND for that reason,
    /// and LAST in every brand's list: a provider refusal that happens to mention a connection is a
    /// refusal first.
    /// </summary>
    [GeneratedRegex(
        @"(ECONNRESET|ECONNREFUSED|ENOTFOUND|EAI_AGAIN|ETIMEDOUT|EPIPE|getaddrinfo"
        + @"|socket" + Gap + @"*hang" + Gap + @"*up"
        + @"|network(" + Gap + @"+is)?" + Gap + @"+(error|unreachable)"
        + @"|connection" + Gap + @"+(reset|refused|timed" + Gap + @"+out)"
        + @"|(temporary|name)" + Gap + @"+resolution" + Gap + @"+fail)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Transport();

    /// <summary>`Claude AI usage limit reached|1758326400` - a unix stamp after a pipe.</summary>
    [GeneratedRegex(@"limit" + Gap + @"+reached" + Gap + @"*\|" + Gap + @"*(\d{9,})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpochStamp();

    /// <summary>
    /// "resets 12am (America/New_York)", "resets at 09:30 (UTC)".
    ///
    /// THE ZONE IS REQUIRED. A wall clock with no zone is not a moment - it is a moment in some zone
    /// this platform would have to guess, and guessing it is how a resume fires hours early against
    /// a budget that has not renewed.
    /// </summary>
    [GeneratedRegex(
        @"reset[s]?\b" + Gap + @"*(?:at" + Gap + @"+)?(\d{1,2})(?::(\d{2}))?" + Gap + @"*(am|pm)?" + Gap + @"*\(([^)]{2,60})\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResetsAt();

    /// <summary>An HTTP `Retry-After` in seconds, however the CLI chose to echo it.</summary>
    [GeneratedRegex(@"retry" + Gap + @"*after[""']?\s*[:=]?\s*[""']?(\d{1,7})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RetryAfterSeconds();

    /// <summary>"Please try again in 2h13m", "retry in 45 seconds".</summary>
    [GeneratedRegex(
        @"(?:try" + Gap + @"+again|retry|available" + Gap + @"+again|back)" + Gap + @"+in" + Gap
        + @"+((?:\d+\s*(?:hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s)\s*)+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TryAgainIn();

    [GeneratedRegex(@"(\d+)\s*(hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DurationPart();
}
