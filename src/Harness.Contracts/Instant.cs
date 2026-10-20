using System.Globalization;

namespace Harness.Contracts;

/// <summary>
/// ONE WAY TO STORE AN INSTANT. Every store column that holds a time holds the round-trip form
/// ("O"), in UTC, so the text sorts as the time does and parses the same on every machine. Nine
/// stores wrote the form without converting to UTC first, and a value that reached them with
/// another offset sorted against its neighbours by its offset, not its instant. Message payloads
/// are not this: a trigger's due time is said in the trigger's own zone, on purpose.
/// </summary>
public static class Instant
{
    /// <summary>The instant, in UTC, in the round-trip form.</summary>
    public static string Stamp(DateTimeOffset at) => at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Now, as <see cref="Stamp"/> writes it.</summary>
    public static string Now() => Stamp(DateTimeOffset.UtcNow);

    /// <summary>A stored stamp back to its instant; the offset it carries is kept (a UTC stamp reads as UTC).</summary>
    public static DateTimeOffset Parse(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    /// <summary>As <see cref="Parse"/>, answering whether the text was a stamp at all.</summary>
    public static bool TryParse(string text, out DateTimeOffset at) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out at);
}
