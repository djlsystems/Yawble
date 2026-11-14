using Yawble.Core.Teams;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// AI attribution in commit messages: each kind is found, a human co-author is left alone, and a member's branch whose
/// new commits carry it is refused at fetch-back with the plain message (the commits are never rewritten).
/// </summary>
public class CommitAttributionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>")]
    [InlineData("Co-authored-by: Claude <noreply@anthropic.com>")]
    [InlineData("co-authored-by: Someone <noreply@anthropic.com>")]
    [InlineData("Co-Authored-By: Anthropic Bot <bot@example.com>")]
    [InlineData("Co-Authored-By: OpenAI Codex <codex@openai.com>")]
    [InlineData("Co-Authored-By: Codex <noreply@openai.com>")]
    [InlineData("Co-Authored-By: ChatGPT <x@example.com>")]
    [InlineData("Co-Authored-By: GPT-5 <x@example.com>")]
    [InlineData("Co-Authored-By: Copilot <175728472+Copilot@users.noreply.github.com>")]
    [InlineData("Co-Authored-By: GitHub Copilot <copilot@github.com>")]
    [InlineData("Co-Authored-By: Grok <grok@x.ai>")]
    [InlineData("Co-Authored-By: xAI <a@b.c>")]
    [InlineData("Co-Authored-By: Gemini <gemini@google.com>")]
    [InlineData("Co-Authored-By: Antigravity <a@b.c>")]
    [InlineData("  Co-Authored-By:Claude Sonnet 5.5")]
    // Tools named like people: caught on their bot account, vendor domain or "AI"/"bot"/"Code" after the name.
    [InlineData("Co-Authored-By: devin-ai-integration[bot] <158243242+devin-ai-integration[bot]@users.noreply.github.com>")]
    [InlineData("Co-Authored-By: Devin AI <devin@example.com>")]
    [InlineData("Co-Authored-By: Devin <devin@cognition.ai>")]
    [InlineData("Co-Authored-By: Cody Bot <cody@example.com>")]
    [InlineData("Co-Authored-By: Claude Code <claude@example.com>")]
    [InlineData("Co-Authored-By: claude[bot] <209825114+claude[bot]@users.noreply.github.com>")]
    // Claude is caught on the bare word (spec, card 134 item 3), so a person named Claude is caught too: accepted.
    [InlineData("Co-Authored-By: Claude <claude@example.com>")]
    [InlineData("Co-Authored-By: Claude Dupont <claude.dupont@example.fr>")]
    [InlineData("Claude-Session: 0f3c9a2e-1b2c")]
    [InlineData("claude-session: abc")]
    [InlineData("🤖 Generated with [Claude Code](https://claude.com/claude-code)")]
    [InlineData("Generated with Claude Code")]
    [InlineData("Generated with GitHub Copilot")]
    public void EachPatternIsCaught(string line)
    {
        var message = "Add the widget\n\nIt does the thing.\n\n" + line + "\n";
        Assert.Equal([line.Trim()], CommitAttribution.Find(message));
        Assert.True(CommitAttribution.Has(message));
    }

    [Theory]
    [InlineData("Co-Authored-By: Jane <jane@x>")]
    [InlineData("Co-Authored-By: Ivan Grokhov <ivan@example.com>")]
    // People who share a first name with an AI tool.
    [InlineData("Co-Authored-By: Devin Smith <devin.smith@example.com>")]
    [InlineData("Co-Authored-By: Devin Aird <devin@example.com>")]
    [InlineData("Co-Authored-By: Cody Botha <cody.botha@example.com>")]
    [InlineData("Co-Authored-By: Bard Olsen <bard@example.no>")]
    [InlineData("Generated with the new protoc script")]
    [InlineData("Fix the Claude-Session header parsing")] // not a trailer: the line does not start with it
    [InlineData("Mention Claude and Copilot support in the docs")]
    // Prose that names the trailers mid-line is not a trailer.
    [InlineData("  tool, Claude-Session: and \"Generated with ...\" naming one; a human")]
    [InlineData("- CommitAttribution: finds Co-Authored-By naming an AI vendor")]
    [InlineData("- Generated with Claude, the line is caught; a human one is not")]
    [InlineData("Notes: Co-Authored-By: Claude <noreply@anthropic.com> is refused")]
    public void HumanLinesAreLeftAlone(string line)
    {
        Assert.Empty(CommitAttribution.Find("Subject\n\n" + line + "\n"));
    }

    [Fact]
    public void TheB028CommitBodyItself_DescribesTheTrailers_ButCarriesNone()
    {
        const string body = """
            B028: refuse fetching back commits that carry AI attribution lines

            - CommitAttribution: finds Co-Authored-By naming an AI vendor, model or
              tool, Claude-Session: and "Generated with ..." naming one; a human
              co-author is left alone.
            - LandFetchedAsync checks the fetched work's new commits (those no host
              branch has) and refuses to land them with a plain message telling the
              member to amend its own unpushed commits. Refuse, never strip: the app
              does not rewrite history; the work stays at the host-only staging ref.
            - member.md and the team-git skill say commit messages carry no AI
              attribution lines; docs/features.md says how it is enforced.
            - CommitAttributionTests: each pattern, human lines, log parsing, the
              refusal text, and a fetch-back refusal then acceptance after amend.
            """;
        Assert.Empty(CommitAttribution.Find(body));
    }

    [Fact]
    public void ParseLog_NamesEachAttributedCommitAndItsLines_CrlfToo()
    {
        var log = "aaaa\0Clean one\n\nCo-Authored-By: Jane <jane@x>\n\x1e\n"
            + "bbbb\0Bad one\r\n\r\nClaude-Session: 1\r\nCo-Authored-By: Claude <noreply@anthropic.com>\r\n\x1e\n"
            + "cccc\0Also bad\n\n🤖 Generated with [Claude Code](https://claude.com/claude-code)\n";
        var found = CommitAttribution.ParseLog(log);
        Assert.Equal(["bbbb", "cccc"], found.Select(c => c.Sha));
        Assert.Equal(["Claude-Session: 1", "Co-Authored-By: Claude <noreply@anthropic.com>"], found[0].Lines);
    }

    [Fact]
    public void RefusalText_TellsTheMemberToAmendItsOwnUnpushedCommits()
    {
        var text = CommitAttribution.RefusalText("Ada", "members/alpha/ada",
            [new AttributedCommit("0123456789abcdef", ["Claude-Session: 1"])]);
        Assert.Contains("Ada must amend its own unpushed commits", text);
        Assert.Contains("0123456789ab", text);
        Assert.Contains("Claude-Session: 1", text);
        Assert.Contains("never rewrites", text);
    }

    [Fact]
    public async Task FetchBack_RefusesABranchWithAttributedCommits_LeavesItUnchanged_AndTakesTheAmendedWork()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team();
        var checkout = await f.Repos.PrepareAsync(ada, Ct);
        var team = f.Repos.RepoOf(ada.TeamId)!.Path;

        // A human co-author passes.
        File.WriteAllText(Path.Combine(checkout.ClonePath, "a.txt"), "a\n");
        AgentGit.Ok(checkout, "add", "a.txt");
        AgentGit.Ok(checkout, "commit", "-q", "-m", "add a\n\nCo-Authored-By: Jane <jane@x>");
        var good = AgentGit.Ok(checkout, "rev-parse", "HEAD");
        Assert.True((await f.Repos.FetchBackAsync(ada, Ct)).Fetched);
        Assert.Equal(good, AgentGit.Host(team, "rev-parse", "refs/heads/members/alpha/ada"));

        // An AI trailer is refused: the branch does not move, and the message says what to do.
        File.WriteAllText(Path.Combine(checkout.ClonePath, "b.txt"), "b\n");
        AgentGit.Ok(checkout, "add", "b.txt");
        AgentGit.Ok(checkout, "commit", "-q", "-m", "add b\n\nCo-Authored-By: Claude <noreply@anthropic.com>");
        var bad = AgentGit.Ok(checkout, "rev-parse", "HEAD");
        var refused = await f.Repos.FetchBackAsync(ada, Ct);
        Assert.False(refused.Fetched);
        Assert.Contains("must amend its own unpushed commits", refused.Error);
        Assert.Contains(bad[..12], refused.Error);
        Assert.Equal(good, AgentGit.Host(team, "rev-parse", "refs/heads/members/alpha/ada"));
        // Never rewritten: the member's commit is exactly as it made it.
        Assert.Equal(bad, AgentGit.Ok(checkout, "rev-parse", "HEAD"));

        // Amended by the member: brought in.
        AgentGit.Ok(checkout, "commit", "-q", "--amend", "-m", "add b");
        var amended = AgentGit.Ok(checkout, "rev-parse", "HEAD");
        var fetched = await f.Repos.FetchBackAsync(ada, Ct);
        Assert.True(fetched.Fetched, fetched.Error);
        Assert.Equal(amended, AgentGit.Host(team, "rev-parse", "refs/heads/members/alpha/ada"));
    }
}
