using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;
using Yawble.Core.Tests.Policy;
using Xunit;

namespace Yawble.Core.Tests.AgentCommands;

/// <summary>Card 46 (a): the shown command line is the launched one (one function), custom changes are validated.</summary>
public class CommandLineTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var (cli, mode) in new[]
        {
            (AgentCli.Claude, CredentialMode.ClaudeGatewaySignIn), (AgentCli.Grok, CredentialMode.GrokGatewaySignIn),
            (AgentCli.Codex, CredentialMode.CodexGatewaySignIn), (AgentCli.Copilot, CredentialMode.CopilotGatewaySignIn),
            (AgentCli.Antigravity, CredentialMode.AntigravityHostSignIn),
        })
            foreach (var kind in new[] { RunKind.Headless, RunKind.Interactive })
                yield return [cli, mode, kind];
    }

    [Theory, MemberData(nameof(Cases))]
    public void Shown_argv_equals_launched_argv_with_placeholders(AgentCli cli, CredentialMode mode, RunKind kind)
    {
        var l = PolicyFixtures.Layout(TargetOs.Windows, cli);
        var launched = AgentPresets.Arguments(l, mode, kind, AgentPresets.PromptPlaceholder, AgentPresets.SessionPlaceholder, "m-1");
        var shown = AgentPresets.Describe(l, mode, kind, "m-1");
        Assert.Equal(kind, shown.Kind);
        Assert.Equal(launched.Count, shown.Argv.Count);
        for (var i = 0; i < launched.Count; i++)
        {
            if (launched[i].Contains(l.Workspace, StringComparison.OrdinalIgnoreCase))
                Assert.StartsWith("<workspace>", shown.Argv[i].TrimStart('@'), StringComparison.Ordinal);
            else Assert.Equal(launched[i], shown.Argv[i]);
        }
        Assert.DoesNotContain(shown.Argv, a => a.Contains(l.Workspace, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(shown.EnvNames.OrderBy(x => x, StringComparer.Ordinal), shown.EnvNames);
        Assert.All(shown.EnvNames, n => Assert.DoesNotContain("=", n));
    }

    [Fact]
    public void Describe_shows_mcp_path_and_session_placeholders_for_claude()
    {
        var l = PolicyFixtures.Layout(TargetOs.Windows, AgentCli.Claude);
        var d = AgentPresets.Describe(l, CredentialMode.ClaudeGatewaySignIn, RunKind.Headless, null);
        Assert.Contains("<workspace>/.yawble/mcp.json", d.Argv);
        Assert.Contains("<session id>", d.Argv);
        Assert.Contains("<prompt>", d.Argv);
        Assert.Contains("CLAUDE_CODE_OAUTH_TOKEN", d.EnvNames);
        Assert.DoesNotContain(d.Argv, a => a == "--model");
    }

    [Theory, MemberData(nameof(Cases))]
    public void No_run_token_in_argv_or_env_names(AgentCli cli, CredentialMode mode, RunKind kind)
    {
        var l = PolicyFixtures.Layout(TargetOs.Windows, cli);
        var d = AgentPresets.Describe(l, mode, kind, null);
        Assert.DoesNotContain(d.Argv, a => a.Contains("gho_") || a.Contains(AgentPresets.RunTokenPlaceholder));
    }

    [Fact]
    public void Custom_before_after_and_removed_are_applied_and_prompt_stays_last()
    {
        var l = PolicyFixtures.Layout(TargetOs.Windows, AgentCli.Codex);
        var custom = new ArgvCustomization(["--before=1"], ["--after=1"], ["-c"]);
        var argv = AgentPresets.Arguments(l, CredentialMode.CodexGatewaySignIn, RunKind.Headless, "do it", "s", null, custom);
        Assert.DoesNotContain("check_for_update_on_startup=false", argv);
        var list = argv.ToList();
        Assert.Equal(l.CliCommand.Count, list.IndexOf("--before=1"));
        Assert.Equal("do it", argv[^1]);
        Assert.Equal("--after=1", argv[^2]);
    }

    [Fact]
    public void Custom_equals_built_in_when_empty()
    {
        var l = PolicyFixtures.Layout(TargetOs.Windows, AgentCli.Grok);
        Assert.Equal(
            AgentPresets.Arguments(l, CredentialMode.GrokGatewaySignIn, RunKind.Headless, "p", "s", null),
            AgentPresets.Arguments(l, CredentialMode.GrokGatewaySignIn, RunKind.Headless, "p", "s", null, ArgvCustomization.None));
    }

    [Theory]
    [InlineData(AgentCli.Claude, "--mcp-config")]
    [InlineData(AgentCli.Claude, "--session-id=abc")]
    [InlineData(AgentCli.Claude, "--dangerously-skip-permissions")]
    [InlineData(AgentCli.Grok, "--output-format")]
    [InlineData(AgentCli.Codex, "exec")]
    [InlineData(AgentCli.Copilot, "-p")]
    public void Locked_argument_is_refused_with_the_reason(AgentCli cli, string arg)
    {
        var problems = AgentPresets.ValidateCustomization(cli, new ArgvCustomization([arg], [], []));
        var p = Assert.Single(problems);
        Assert.Contains("locked", p);
        Assert.Throws<ArgumentException>(() => AgentPresets.Arguments(PolicyFixtures.Layout(TargetOs.Windows, cli),
            AgentPresets.HostSignInMode(cli), RunKind.Headless, "x", "s", null, new ArgvCustomization([], [arg], [])));
    }

    [Fact]
    public void Locked_builtin_cannot_be_removed_and_blank_or_control_items_are_refused()
    {
        Assert.Single(AgentPresets.ValidateCustomization(AgentCli.Claude, new ArgvCustomization([], [], ["--strict-mcp-config"])));
        Assert.Single(AgentPresets.ValidateCustomization(AgentCli.Claude, new ArgvCustomization([" "], [], [])));
        Assert.Single(AgentPresets.ValidateCustomization(AgentCli.Claude, new ArgvCustomization(["--x\n--y"], [], [])));
        Assert.Empty(AgentPresets.ValidateCustomization(AgentCli.Claude, new ArgvCustomization(["--effort", "high; rm -rf /"], [], [])));
    }

    [Fact]
    public void Groups_for_claude_mark_the_team_tools_locked_and_the_prompt_locked_or_tail()
    {
        var l = PolicyFixtures.Layout(TargetOs.Windows, AgentCli.Claude);
        var headless = AgentPresets.DescribeGroups(l, CredentialMode.ClaudeGatewaySignIn, RunKind.Headless, null);
        var mcp = Assert.Single(headless.Groups, g => g.Tokens[0] == "--mcp-config");
        Assert.True(mcp.Locked);
        Assert.Equal("<workspace>/.yawble/mcp.json", mcp.Tokens[1]);
        Assert.Contains("tools", mcp.Reason);
        // Claude's headless prompt rides on -p, the first built-in (that is how it is launched): locked, not the tail.
        var prompt = Assert.Single(headless.Groups, g => g.Tokens.Contains("<prompt>"));
        Assert.Equal(["-p", "<prompt>"], prompt.Tokens);
        Assert.True(prompt.Locked);
        Assert.All(headless.Groups, g => Assert.True(g.Locked));
        Assert.All(headless.Groups, g => Assert.False(g.Removed));
        // Interactive: the prompt is the positional tail.
        var interactive = AgentPresets.DescribeGroups(l, CredentialMode.ClaudeGatewaySignIn, RunKind.Interactive, null);
        var tail = Assert.Single(interactive.Groups, g => g.Tail);
        Assert.Equal(["<prompt>"], tail.Tokens);
        Assert.True(tail.Locked);
        Assert.Same(tail, interactive.Groups[^1]);
    }

    [Theory, MemberData(nameof(Cases))]
    public void Groups_rebuild_the_described_argv(AgentCli cli, CredentialMode mode, RunKind kind)
    {
        var l = PolicyFixtures.Layout(TargetOs.Windows, cli);
        var shown = AgentPresets.Describe(l, mode, kind, "m-1");
        var parts = AgentPresets.DescribeGroups(l, mode, kind, "m-1");
        Assert.Equal(shown.Argv, [.. parts.Launch, .. parts.Groups.SelectMany(g => g.Tokens)]);
        Assert.All(parts.Groups.Where(g => g.Locked), g => Assert.False(string.IsNullOrEmpty(g.Reason)));
        Assert.All(parts.Groups.Where(g => !g.Locked), g => Assert.Null(g.Reason));
    }

    [Fact]
    public void Codex_update_check_group_is_unlocked()
    {
        var l = PolicyFixtures.Layout(TargetOs.Windows, AgentCli.Codex);
        var groups = AgentPresets.DescribeGroups(l, CredentialMode.CodexGatewaySignIn, RunKind.Headless, null).Groups;
        var update = Assert.Single(groups, g => g.Tokens.SequenceEqual(["-c", "check_for_update_on_startup=false"]));
        Assert.False(update.Locked);
        Assert.Single(groups, g => !g.Locked);
        Assert.True(Assert.Single(groups, g => g.Tail).Locked);
    }

    [Fact]
    public void A_removed_built_in_is_marked_removed_and_left_out_of_the_argv()
    {
        var l = PolicyFixtures.Layout(TargetOs.Windows, AgentCli.Grok);
        var custom = new ArgvCustomization([], [], ["--no-auto-update"]);
        var groups = AgentPresets.DescribeGroups(l, CredentialMode.GrokGatewaySignIn, RunKind.Headless, null, custom).Groups;
        Assert.True(Assert.Single(groups, g => g.Tokens[0] == "--no-auto-update").Removed);
        Assert.Single(groups, g => g.Removed);
        Assert.DoesNotContain("--no-auto-update", AgentPresets.Describe(l, CredentialMode.GrokGatewaySignIn, RunKind.Headless, null, custom).Argv);
        // Without the customization nothing is removed.
        Assert.All(AgentPresets.DescribeGroups(l, CredentialMode.GrokGatewaySignIn, RunKind.Headless, null).Groups, g => Assert.False(g.Removed));
    }
}
