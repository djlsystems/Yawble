using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// A cron or every-N schedule may have a START - it never fires before that
/// instant, and the loop begins once it is reached. The start travels as `fireAt`, the column an
/// every-N schedule also uses as its anchor.
/// </summary>
public sealed class TriggerStartTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_cron_with_a_future_start_first_fires_at_its_first_time_after_the_start() =>
        Assert.Equal(
            new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero),
            Triggers.NextOccurrence(TriggerKind.Cron, "0 0 9 * * *", "UTC", null, Start, Now));

    [Fact]
    public void A_start_that_falls_on_a_cron_time_is_that_time()
    {
        var onTheDot = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal(onTheDot, Triggers.NextOccurrence(TriggerKind.Cron, "0 0 9 * * *", "UTC", null, onTheDot, Now));
    }

    [Fact]
    public void A_past_start_changes_nothing_about_a_cron() =>
        Assert.Equal(
            Triggers.NextOccurrence(TriggerKind.Cron, "0 0 9 * * *", "UTC", null, null, Now),
            Triggers.NextOccurrence(TriggerKind.Cron, "0 0 9 * * *", "UTC", null, Now.AddDays(-3), Now));

    [Fact]
    public void An_every_n_with_a_future_start_first_fires_at_the_start_then_every_n() =>
        Assert.Equal(
            [Start, Start.AddHours(1), Start.AddHours(2)],
            Triggers.Preview(TriggerKind.Every, null, null, 3600, Start, Now, 3).Occurrences);

    [Fact]
    public void The_preview_lists_the_next_firings_from_the_start_in_the_zone()
    {
        var preview = Triggers.Preview(TriggerKind.Cron, "0 30 8 * * 1-5", "America/New_York", null, Start, Now, 3);

        Assert.Null(preview.Error);
        // 2026-10-01 is a Thursday; 08:30 in New York is 12:30 UTC while daylight time holds.
        Assert.Equal(
            [
                new DateTimeOffset(2026, 10, 1, 12, 30, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 10, 2, 12, 30, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 10, 5, 12, 30, 0, TimeSpan.Zero),
            ],
            preview.Occurrences.Select(o => o.ToUniversalTime()));
    }

    [Fact]
    public void The_preview_of_a_bad_expression_is_a_sentence_and_no_times()
    {
        var preview = Triggers.Preview(TriggerKind.Cron, "every tuesday", "UTC", null, null, Now, 5);

        Assert.NotNull(preview.Error);
        Assert.Empty(preview.Occurrences);
    }
}
