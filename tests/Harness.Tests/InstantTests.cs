using Harness.Contracts;

namespace Harness.Tests;

/// <summary>
/// One way to store an instant: UTC, round-trip form. A value that arrives with another offset is
/// stored as the same instant in UTC; a stored stamp in either spelling reads back as its instant;
/// no store assembly stamps a column its own way.
/// </summary>
public sealed class InstantTests
{
    [Fact]
    public void A_stamp_is_the_instant_in_utc_whatever_offset_it_arrived_with()
    {
        var at = new DateTimeOffset(2026, 10, 20, 14, 30, 0, TimeSpan.FromHours(2));

        var stamp = Instant.Stamp(at);

        Assert.Equal("2026-10-20T12:30:00.0000000+00:00", stamp);
        Assert.Equal(at, Instant.Parse(stamp));
        Assert.Equal(TimeSpan.Zero, Instant.Parse(stamp).Offset);
    }

    [Fact]
    public void A_z_stamp_and_a_plus_zero_stamp_read_as_the_same_instant_and_stamps_sort_as_instants()
    {
        Assert.Equal(Instant.Parse("2026-10-20T12:30:00Z"), Instant.Parse("2026-10-20T12:30:00.0000000+00:00"));

        // 01:00+02:00 on the 21st is 23:00Z on the 20th: earlier than 23:00-05:00 on the 20th, which is 04:00Z on the 21st.
        var byLocalDateLater = Instant.Stamp(new DateTimeOffset(2026, 10, 21, 1, 0, 0, TimeSpan.FromHours(2)));
        var byLocalDateEarlier = Instant.Stamp(new DateTimeOffset(2026, 10, 20, 23, 0, 0, TimeSpan.FromHours(-5)));

        Assert.True(string.CompareOrdinal(byLocalDateLater, byLocalDateEarlier) < 0, "the stamps sort as instants, not as local dates");
    }

    [Fact]
    public void No_store_stamps_a_column_its_own_way()
    {
        var root = FindRepoRoot();
        var offenders = new List<string>();

        foreach (var assembly in new[] { "Harness.Identity", "Harness.Messaging", "Harness.Backlog", "Harness.Skills" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", assembly), "*.cs", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(file);
                if (text.Contains("ToString(\"O\"", StringComparison.Ordinal)) offenders.Add($"{Path.GetRelativePath(root, file)}: stamps its own way");
                if (text.Contains("DateTimeStyles.RoundtripKind", StringComparison.Ordinal)) offenders.Add($"{Path.GetRelativePath(root, file)}: parses its own way");
            }
        }

        Assert.Empty(offenders);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Harness.slnx was not found above the test binaries.");
    }
}
