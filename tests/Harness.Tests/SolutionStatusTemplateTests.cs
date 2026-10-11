using Harness.Host.Solutions;

namespace Harness.Tests;

/// <summary>
/// A PACKAGE'S STATUS LINE (<c>panel.status</c>): four placeholders filled from the primary site's
/// data and the last run, and nothing else. The template is text, never code: a brace that is not a
/// placeholder is refused, missing data counts as nothing, and what looks like HTML stays the
/// characters it is.
/// </summary>
public sealed class SolutionStatusTemplateTests
{
    private static readonly SolutionLastRun Ran = new(new DateTimeOffset(2026, 9, 30, 8, 0, 5, TimeSpan.Zero), "completed");

    private static SolutionStatusTemplate Template(string text)
    {
        var (template, refusal) = SolutionStatusTemplate.Parse(text);
        Assert.Null(refusal);
        return template!;
    }

    private static Func<string, IReadOnlyList<string>?> Data(params (string Collection, string[] Documents)[] collections) =>
        name => collections.FirstOrDefault(c => c.Collection == name) is { Documents: { } documents } ? documents : null;

    [Fact]
    public void Every_placeholder_is_filled_from_the_sites_data_and_the_last_run()
    {
        var template = Template("{data.jobs.count status=new} new of {data.jobs.count} · last checked {lastRun.at} ({lastRun.outcome})");

        var line = template.Fill(Data(("jobs", ["{\"status\":\"new\"}", "{\"status\":\"drafted\"}", "{\"status\":\"new\"}"])), Ran);

        Assert.Equal("2 new of 3 · last checked 2026-09-30T08:00:05Z (completed)", line);
        Assert.Equal(["jobs"], template.Collections);
        Assert.True(template.ReadsData);
    }

    [Fact]
    public void A_count_compares_numbers_and_booleans_as_json_writes_them_and_skips_what_is_not_an_object()
    {
        var template = Template("{data.jobs.count score=3} {data.jobs.count open=true}");

        var line = template.Fill(Data(("jobs", ["{\"score\":3,\"open\":true}", "{\"score\":\"3\"}", "[1,2]", "not json", "{\"open\":false}"])), null);

        Assert.Equal("2 1", line);
    }

    [Fact]
    public void Missing_data_counts_as_nothing_and_no_run_reads_never()
    {
        var template = Template("{data.jobs.count} jobs, {data.saved.count status=new} saved · {lastRun.at} {lastRun.outcome}");

        Assert.Equal("0 jobs, 0 saved · never none", template.Fill(_ => null, null));
        Assert.Equal("0 jobs, 0 saved · never none", template.Fill(Data(("jobs", [])), null));
    }

    [Fact]
    public void A_value_that_looks_like_html_stays_text_and_is_matched_by_its_characters()
    {
        // The template's own text is answered as written, never interpreted: the UI renders it as text.
        var template = Template("<img src=x onerror=alert(1)> {data.jobs.count status=<b>new</b>} <script>x</script>");

        var line = template.Fill(Data(("jobs", ["{\"status\":\"<b>new</b>\"}", "{\"status\":\"new\"}"])), null);

        Assert.Equal("<img src=x onerror=alert(1)> 1 <script>x</script>", line);
    }

    [Fact]
    public void An_outcome_that_looks_like_html_is_filled_as_the_characters_it_is()
    {
        var line = Template("{lastRun.outcome}").Fill(_ => null, Ran with { Outcome = "<i>failed</i>" });

        Assert.Equal("<i>failed</i>", line);
    }

    [Theory]
    [InlineData("{data.jobs}")]
    [InlineData("{data.jobs.sum}")]
    [InlineData("{data.jobs.count status = new}")]
    [InlineData("{data.Jobs!.count}")]
    [InlineData("{lastRun.by}")]
    [InlineData("{event.title}")]
    [InlineData("{solution}")]
    [InlineData("{{data.jobs.count}}")]
    [InlineData("{ lastRun.at }")]
    [InlineData("{constructor.constructor('alert(1)')()}")]
    public void Anything_but_the_four_placeholders_is_refused(string text)
    {
        var (template, refusal) = SolutionStatusTemplate.Parse(text);

        Assert.Null(template);
        Assert.NotNull(refusal);
    }

    [Theory]
    [InlineData("jobs: {lastRun.at")]
    [InlineData("jobs} {lastRun.at}")]
    public void An_unbalanced_brace_is_refused(string text)
    {
        Assert.NotNull(SolutionStatusTemplate.Parse(text).Refusal);
    }

    [Fact]
    public void A_template_longer_than_the_line_is_refused_and_a_filled_line_is_clipped()
    {
        Assert.NotNull(SolutionStatusTemplate.Parse(new string('x', SolutionStatusTemplate.MaximumLength + 1)).Refusal);

        var template = Template(new string('x', SolutionStatusTemplate.MaximumLength - 40) + "{lastRun.at}{lastRun.at}{lastRun.at}");
        var line = template.Fill(_ => null, Ran);

        Assert.Equal(SolutionStatusTemplate.MaximumLength, line.Length);
        Assert.EndsWith("…", line);
    }

    [Fact]
    public void Plain_text_with_no_placeholder_is_a_template_that_reads_nothing()
    {
        var template = Template("Tracking jobs");

        Assert.False(template.ReadsData);
        Assert.Equal("Tracking jobs", template.Fill(_ => throw new InvalidOperationException("no data is read"), null));
    }

    [Fact]
    public void Without_a_template_the_line_is_the_last_run_and_the_state()
    {
        Assert.Equal("No runs yet · idle", SolutionStatusTemplate.Default(null, "idle"));
        Assert.Equal("Last run 2026-09-30T08:00:05Z (completed) · paused", SolutionStatusTemplate.Default(Ran, "paused"));
    }
}
