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
