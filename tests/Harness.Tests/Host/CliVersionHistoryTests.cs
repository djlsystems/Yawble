using System.Net;
using System.Net.Http.Json;
using Harness.Host;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// What <c>scripts/ensure-agent-clis.sh</c> appends at each start is read back newest
/// first, a torn line hides nothing before it, and only a person reads it on the Diagnostics route.
/// </summary>
public sealed class CliVersionHistoryTests(HostFixture host) : IClassFixture<HostFixture>
{
    // The shape the start script writes, byte for byte, including a CLI that was not installed.
    private const string Older =
        """{"at":"2026-09-20T08:00:00Z","versions":{"claude":"2.1.270 (Claude Code)","agy":null}}""";

    private const string Newer =
        """{"at":"2026-09-23T08:00:00Z","versions":{"claude":"2.1.280 (Claude Code)","agy":null}}""";

    [Fact]
    public async Task Starts_read_newest_first_and_a_torn_line_is_skipped()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), $"harness-cli-versions-{Guid.NewGuid():N}.jsonl");

        try
        {
            await File.WriteAllLinesAsync(path, [Older, """{"at":"2026-09-21T08""", Newer], ct);

            var starts = await new CliVersionHistory(path).ReadAsync(20, ct);

            Assert.Equal(
                [DateTimeOffset.Parse("2026-09-23T08:00:00Z"), DateTimeOffset.Parse("2026-09-20T08:00:00Z")],
                starts.Select(start => start.At));
            Assert.Equal("2.1.280 (Claude Code)", starts[0].Versions["claude"]);
            Assert.Null(starts[0].Versions["agy"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// TWO WRITERS AT ONCE LOSE NOTHING: an update's line and the re-measure pass that its end starts
    /// can append together. Each used to read the file and write it back whole, so one line was lost.
    /// </summary>
    [Fact]
    public async Task Appends_made_at_once_all_land()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), $"harness-cli-versions-{Guid.NewGuid():N}.jsonl");

        try
        {
            await File.WriteAllLinesAsync(path, [Older], ct);
            var history = new CliVersionHistory(path);

            var written = await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
                Task.Run(() => history.AppendAsync(new Dictionary<string, string?> { [$"cli{i}"] = "1" }, $"by{i}", ct), ct)));

            Assert.All(written, Assert.True);
            var bys = (await history.ReadAsync(CliVersionHistory.MaxTake, ct)).Select(start => start.By).ToList();
            Assert.Equal(21, bys.Count);
            Assert.Equal(Enumerable.Range(0, 20).Select(i => $"by{i}").Order(), bys.OfType<string>().Order());
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A READER NEVER SEES THE RECORD EMPTY while a line is added: the file used to be emptied and
    /// then written back, and a read in between found no lines at all.
    /// </summary>
    [Fact]
    public async Task A_reader_never_sees_the_record_empty_while_lines_are_added()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), $"harness-cli-versions-{Guid.NewGuid():N}.jsonl");

        try
        {
            await File.WriteAllLinesAsync(path, [Older], ct);
            var history = new CliVersionHistory(path);
            using var done = new CancellationTokenSource();
            var emptyReads = 0;

            var reader = Task.Run(async () =>
            {
                while (!done.IsCancellationRequested)
                {
                    try
                    {
                        if ((await File.ReadAllLinesAsync(path, ct)).All(string.IsNullOrWhiteSpace)) Interlocked.Increment(ref emptyReads);
                    }
                    catch (IOException) { }
                }
            }, ct);

            for (var i = 0; i < 150; i++)
                await history.AppendAsync(new Dictionary<string, string?> { ["claude"] = $"{i}" }, "update", ct);

            await done.CancelAsync();
            await reader;
            Assert.Equal(0, emptyReads);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>At the cap the oldest lines go and the newest stay, with no line left over.</summary>
    [Fact]
    public async Task At_the_cap_the_oldest_lines_go_and_the_newest_stay()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), $"harness-cli-versions-{Guid.NewGuid():N}.jsonl");

        try
        {
            var history = new CliVersionHistory(path);
            for (var i = 0; i < CliVersionHistory.MaxTake + 5; i++)
                Assert.True(await history.AppendAsync(new Dictionary<string, string?> { ["claude"] = $"{i}" }, $"by{i}", ct));

            var lines = await File.ReadAllLinesAsync(path, ct);
            Assert.Equal(CliVersionHistory.MaxTake, lines.Length);
            var starts = await history.ReadAsync(CliVersionHistory.MaxTake, ct);
            Assert.Equal($"by{CliVersionHistory.MaxTake + 4}", starts[0].By);
            Assert.Equal("by5", starts[^1].By);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_volume_with_no_record_answers_an_empty_history()
    {
        var starts = await new CliVersionHistory(Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}"))
            .ReadAsync(20, TestContext.Current.CancellationToken);

        Assert.Empty(starts);
    }

    [Fact]
    public async Task A_person_reads_the_history_on_the_diagnostics_route_and_a_container_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var history = host.Services.GetRequiredService<CliVersionHistory>();
        await File.WriteAllLinesAsync(history.Path, [Older, Newer], ct);

        var person = await host.PersonAsync();
        var starts = await person.GetFromJsonAsync<List<Start>>("/api/diagnostics/cli-versions", ct);

        Assert.NotNull(starts);
        Assert.Equal(2, starts.Count);
        Assert.Equal("2.1.280 (Claude Code)", starts[0].Versions["claude"]);

        var container = await host.Container(host.AlphaContainerKey)
            .GetAsync("/api/diagnostics/cli-versions", ct);

        Assert.Equal(HttpStatusCode.Forbidden, container.StatusCode);
    }

    private sealed record Start(DateTimeOffset At, Dictionary<string, string?> Versions);
}
