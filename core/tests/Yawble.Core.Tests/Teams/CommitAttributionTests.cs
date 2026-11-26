using Yawble.Core.Teams;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// AI attribution in commit messages: each kind is found, a human co-author is left alone, and a member's branch whose
/// new commits carry it is refused at fetch-back with the plain message (the commits are never rewritten).
/// A hit is a line that starts, after optional spaces and in any letter case, with Co-Authored-By naming an AI,
/// with Claude-Session:, or with an optional robot and "Generated with" an AI tool. Prose mid-line is not a hit.
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
    // Claude is caught on the bare word (as the rule says), so a person named Claude is caught too: accepted.
    [InlineData("Co-Authored-By: Claude <claude@example.com>")]
    [InlineData("Co-Authored-By: Claude Dupont <claude.dupont@example.fr>")]
    [InlineData("Claude-Session: 0f3c9a2e-1b2c")]
    [InlineData("claude-session: abc")]
    [InlineData("🤖 Generated with [Claude Code](https://claude.com/claude-code)")]
    [InlineData("Generated with Claude Code")]
    [InlineData("Generated with GitHub Copilot")]
    // Other spellings: letter case, leading spaces or a tab, a space before the colon.
    [InlineData("CO-AUTHORED-BY: CLAUDE <NOREPLY@ANTHROPIC.COM>")]
    [InlineData("\tCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>")]
    [InlineData("Co-Authored-By : Claude <noreply@anthropic.com>")]
    [InlineData("Co-Authored-By: Bot <noreply@openai.com>")]
    [InlineData("Co-Authored-By: gemini-cli <x@example.com>")]
    [InlineData("    Claude-Session: 0f3c")]
    [InlineData("GENERATED WITH CLAUDE CODE")]
    [InlineData("   Generated with [Codex](https://example.com)")]
    // A tool's name or account with another address, or with none.
    [InlineData("Co-Authored-By: Claude Code <noreply@example.com>")]
    [InlineData("Co-Authored-By: claude[bot] <claude[bot]@users.noreply.github.com>")]
    [InlineData("Co-Authored-By: Devin AI")]
    [InlineData("Co-Authored-By: Claude Code")]
    [InlineData("Co-Authored-By: claude[bot]")]
    [InlineData("Co-Authored-By: Claude Dupont")]
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
    [InlineData("See the Co-Authored-By: Claude trailer rule in member.md")]
    [InlineData("* Generated with Claude, then reviewed")]
    [InlineData("> Claude-Session: quoted from a log")]
    [InlineData("Co-Authored-By: Bob <bob@gptools.example>")]
    [InlineData("Generated with make")]
    [InlineData("Claude-Sessions are now kept")]
    // A first name shared with a tool, with an address that does not repeat it, or with no address.
    [InlineData("Co-Authored-By: Devin Aird <d.aird@example.com>")]
    [InlineData("Co-Authored-By: Cody Botha <cody@example.com>")]
    [InlineData("Co-Authored-By: Devin Aird")]
    [InlineData("Co-Authored-By: Cody Botha")]
    public void HumanLinesAreLeftAlone(string line)
    {
        Assert.Empty(CommitAttribution.Find("Subject\n\n" + line + "\n"));
    }

    private const string Robot = "\U0001F916";

    [Theory]
    [InlineData(" Generated with [Claude Code](https://claude.com/claude-code)")]
    [InlineData("Generated with [Claude Code](https://claude.com/claude-code)")]
    [InlineData("  Generated with Claude Code")]
    public void TheRobotLine_IsCaught_WithOrWithoutSpacesAroundTheRobot(string rest)
    {
        foreach (var line in new[] { Robot + rest, "  " + Robot + rest })
            Assert.Equal([line.Trim()], CommitAttribution.Find("Subject\n\n" + line));
    }

    [Fact]
    public void ASubjectLineCanItselfBeAHit()
    {
        Assert.True(CommitAttribution.Has("Generated with Claude Code"));
    }

    [Fact]
    public void SeveralHitsInOneMessage_AreReportedInOrder_AndHumanOnesAreSkipped()
    {
        var message = "Subject\n\nCo-Authored-By: Jane <jane@x>\nClaude-Session: 1\n\nCo-Authored-By: Copilot <c@github.com>\n"
            + Robot + " Generated with [Claude Code](https://claude.com/claude-code)\n";
        Assert.Equal(
            ["Claude-Session: 1", "Co-Authored-By: Copilot <c@github.com>", Robot + " Generated with [Claude Code](https://claude.com/claude-code)"],
            CommitAttribution.Find(message));
    }

    [Fact]
    public void AnEmptyOrCleanMessage_HasNoHits()
    {
        Assert.False(CommitAttribution.Has(""));
        Assert.False(CommitAttribution.Has("\n\r\n"));
        Assert.False(CommitAttribution.Has("Fix the build\n\nCo-Authored-By: Jane <jane@x>"));
    }

    [Fact]
    public void TheAttributionCommitBodyItself_DescribesTheTrailers_ButCarriesNone()
    {
        const string body = """
            B002: refuse fetching back commits that carry AI attribution lines

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
    public void ParseLog_EmptyAndCleanLogs_GiveNoCommits_AndAnUnterminatedLastRecordIsRead()
    {
        Assert.Empty(CommitAttribution.ParseLog(""));
        Assert.Empty(CommitAttribution.ParseLog("aaaa\0Clean\n\x1e\nbbbb\0Also clean\n\x1e\n"));
        var found = CommitAttribution.ParseLog("\naaaa\0Clean\n\x1e\ncccc\0Bad\n\nClaude-Session: 9");
        Assert.Equal("cccc", Assert.Single(found).Sha);
    }

    [Fact]
    public void LogFormat_SeparatesShaAndBodyAsParseLogExpects()
    {
        Assert.Equal("--format=%H%x00%B%x1e", CommitAttribution.LogFormat);
    }

    [Fact]
    public void RefusalText_ListsAtMostFiveCommits_SaysHowManyMore_AndCopesWithAShortSha()
    {
        var commits = Enumerable.Range(1, 7)
            .Select(i => new AttributedCommit(i == 1 ? "abc" : new string((char)('a' + i), 40), ["Claude-Session: " + i]))
            .ToList();
        var text = CommitAttribution.RefusalText("Bo", "members/alpha/bo", commits);
        Assert.Contains("abc:", text);
        Assert.Contains("Claude-Session: 5", text);
        Assert.DoesNotContain("Claude-Session: 6", text);
        Assert.Contains("and 2 more", text);
        Assert.Contains("members/alpha/bo", text);
        Assert.Contains("Bo must amend its own unpushed commits", text);
    }

    [Fact]
    public void RefusalText_WithFiveOrFewer_SaysNoMore()
    {
        var text = CommitAttribution.RefusalText("Bo", "b", [new AttributedCommit(new string('a', 40), ["Claude-Session: 1"])]);
        Assert.DoesNotContain("more", text.Split("these do:")[1].Split('.')[0]);
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
        SandboxGit.RequireLocalTransport();
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
