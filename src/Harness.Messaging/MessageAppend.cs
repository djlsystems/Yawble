using Harness.Contracts;

namespace Harness.Messaging;

/// <summary>
/// THE RULES OF AN APPEND THAT ARE NOT SQL, shared by both providers' logs so a row is refused,
/// redacted and defaulted the same way whichever database stores it. See
/// <c>SqliteMessageStore.AppendAsync</c> for why the redaction sits at the store.
/// </summary>
public static class MessageAppend
{
    /// <summary>A message with no type or no source is refused before anything is opened.</summary>
    public static void Validate(NewMessage message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message.Type);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.Source);
    }

    /// <summary>
    /// The payload with any URL userinfo removed: what the row holds, and what the stored message
    /// hands back. `?? message.Payload` only ever fires on a null the compiler cannot rule out:
    /// `RedactUserInfo` returns its argument unchanged when there is nothing to redact.
    /// </summary>
    public static string Redacted(NewMessage message) =>
        GitOutputRedaction.RedactUserInfo(message.Payload) ?? message.Payload;

    /// <summary>The text written to the payload column: an empty payload is stored as <c>{}</c>.</summary>
    public static string RowPayload(string redacted) => string.IsNullOrEmpty(redacted) ? "{}" : redacted;

    /// <summary>
    /// Refused rather than silently treated as a root. A dangling cause would report depth 0 for a
    /// message arbitrarily deep in a chain, which is precisely the value the loop bound depends on.
    /// </summary>
    public static InvalidOperationException NoSuchCause(long cause) =>
        new($"Message {cause} does not exist, so nothing can be caused by it.");
}

/// <summary>Where on the log an instant falls, found by halving the seq range.</summary>
public static class LogPosition
{
    /// <summary>
    /// The highest seq written before <paramref name="at"/>, or 0. <paramref name="firstAtOrAfter"/>
    /// answers the first row at or after a seq with its stamp, or null past the end: rows are
    /// appended in time order, so whether it was written before <paramref name="at"/> only ever turns
    /// from yes to no as the seq rises, gaps from deletion included.
    /// </summary>
    public static async Task<long> LastSeqBeforeAsync(
        Func<long, Task<(long Seq, string OccurredAt)?>> firstAtOrAfter, long highest, DateTimeOffset at)
    {
        async Task<long?> BeforeAsync(long seq) =>
            await firstAtOrAfter(seq) is { } row && MessageRows.ReadStamp(row.OccurredAt) < at ? row.Seq : null;

        // The highest seq whose first row at or after it was written before `at`.
        var low = 0L;
        var high = highest;
        var found = 0L;

        while (low <= high)
        {
            var mid = low + (high - low) / 2;

            if (await BeforeAsync(mid) is { } before)
            {
                found = before;
                low = before + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return found;
    }
}
