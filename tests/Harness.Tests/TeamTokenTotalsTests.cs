using Harness.Contracts;

namespace Harness.Tests;

/// <summary>
/// The team Tokens tile's projection. Cache reads and cache writes are summed beside tokensIn,
/// never into it, and the billable figure uses the weights <see cref="InvocationUsage.BillableTokens"/>
/// uses for one run.
/// </summary>
public sealed class TeamTokenTotalsTests
{
    private static string Split(int tokensIn, int tokensOut, int cachedIn, int cacheCreation, string source = "claude") =>
        $$"""{"output":"done","tokensIn":{{tokensIn}},"tokensOut":{{tokensOut}},"tokensTotal":null,"tokensCachedIn":{{cachedIn}},"tokensCacheCreation":{{cacheCreation}},"tokensReasoning":null,"tokensSource":"{{source}}"}""";

    private static string Combined(int total) =>
        $$"""{"output":"done","tokensIn":null,"tokensOut":null,"tokensTotal":{{total}},"tokensCachedIn":null,"tokensCacheCreation":null,"tokensReasoning":null,"tokensSource":"codex"}""";

    [Fact]
    public async Task Cache_reads_and_writes_are_summed_beside_tokensIn_and_billed_at_the_spend_weights()
    {
        await using var bed = new ContainerTestBed();
        var ct = TestContext.Current.CancellationToken;

        // Below the floor: a predecessor's row, not counted.
        var old = await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.Completed, Split(5, 5, 5_000_000, 5_000_000), "Alpha/Manager"), ct);

        await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.Completed, Split(1_000, 200, 50_000, 4_000), "Alpha/Manager"), ct);
        // Integer division per row, as the spend SQL does: 19 / 10 = 1, 3 * 5 / 4 = 3.
        await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.Failed, Split(7, 3, 19, 3), "Alpha/Manager"), ct);
        await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.Completed, Split(999_999, 1, 999_999, 1, UsageSource.ExcludedEstimate), "Alpha/Manager"), ct);
        await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.Completed, Combined(9_000), "Alpha/Writer"), ct);
        await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.Completed, """{"output":"before capture"}""", "Alpha/Old"), ct);

        var rows = await bed.Store.SumUsageForTeamAsync("Alpha", old.Seq, ct);
        var manager = rows.Single(r => r.Source == "Alpha/Manager");
        var writer = rows.Single(r => r.Source == "Alpha/Writer");
        var unmeasured = rows.Single(r => r.Source == "Alpha/Old");

        Assert.Equal(1_007, manager.TokensIn);
        Assert.Equal(203, manager.TokensOut);
        Assert.Equal(50_019, manager.TokensCachedIn);
        Assert.Equal(4_003, manager.TokensCacheCreation);

        var expected =
            new InvocationUsage(1_000, 200, "claude", cachedIn: 50_000, cacheCreation: 4_000).BillableTokens()
            + new InvocationUsage(7, 3, "claude", cachedIn: 19, cacheCreation: 3).BillableTokens();
        Assert.Equal(11_214, expected);
        Assert.Equal(expected, manager.TokensBillable);

        Assert.Equal(InvocationUsage.Combined(9_000, "codex").BillableTokens(), writer.TokensBillable);
        Assert.Equal(0, writer.TokensCachedIn);
        Assert.Equal(0, unmeasured.TokensBillable);

        // Per member: measured figures where there are some, null where nothing was measured.
        var managerLine = MemberTokenTotals.FromUsage("Manager", "claude-headless", manager, true);
        Assert.Equal(1_007, managerLine.TokensIn);
        Assert.Equal(50_019, managerLine.TokensCachedIn);
        Assert.Equal(4_003, managerLine.TokensCacheCreation);
        Assert.Equal(11_214, managerLine.TokensBillable);

        var writerLine = MemberTokenTotals.FromUsage("Writer", "codex-headless", writer, true);
        Assert.Null(writerLine.TokensIn);
        Assert.Null(writerLine.TokensCachedIn);
        Assert.Null(writerLine.TokensCacheCreation);
        Assert.Equal(9_000, writerLine.TokensBillable);

        var oldLine = MemberTokenTotals.FromUsage("Old", "claude-headless", unmeasured, true);
        Assert.Null(oldLine.TokensCachedIn);
        Assert.Null(oldLine.TokensCacheCreation);
        Assert.Null(oldLine.TokensBillable);
        Assert.Equal(1, oldLine.Runs);
    }
}
