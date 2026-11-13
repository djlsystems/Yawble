using Yawble.Core.Agents.Profiles;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;
using Yawble.Core.Tests.Policy;
using Xunit;

namespace Yawble.Core.Tests.AgentCommands;

/// <summary>
/// A custom agent cannot smuggle a locked argument back in. Locks hold for a whole flag family (long and
/// short aliases), for attached values (--flag=value, -mVALUE, -pfoo), for short clusters, for Codex's -c/--config keys
/// that set the model, tools, sandbox or approvals, and for "--". Pure: no CLI runs.
/// </summary>
public class LockedArgumentTests
{
    public static TheoryData<AgentCli, string[]> Smuggled() => new()
    {
        // Claude: the long form of -p, settings, permission mode, session flags, input format, fallback model, attached.
        { AgentCli.Claude, ["--print"] },
        { AgentCli.Claude, ["-pfoo"] },
        { AgentCli.Claude, ["--print=hello"] },
        { AgentCli.Claude, ["--settings", "{}"] },
        { AgentCli.Claude, ["--settings={}"] },
        { AgentCli.Claude, ["--permission-mode", "default"] },
        { AgentCli.Claude, ["--permission-mode=plan"] },
        { AgentCli.Claude, ["--resume"] },
        { AgentCli.Claude, ["-r", "abc"] },
        { AgentCli.Claude, ["--continue"] },
        { AgentCli.Claude, ["-c"] },
        { AgentCli.Claude, ["--fork-session"] },
        { AgentCli.Claude, ["--input-format", "stream-json"] },
        { AgentCli.Claude, ["--fallback-model", "haiku"] },
        { AgentCli.Claude, ["--mcp-config=other.json"] },
        { AgentCli.Claude, ["--strict-mcp-config"] },
        { AgentCli.Claude, ["--model=opus"] },
        { AgentCli.Claude, ["--dangerously-load-anything"] },
        { AgentCli.Claude, ["-dp"] }, // a short cluster that holds -p
        { AgentCli.Claude, ["--"] },
        // Codex: -c / --config keys, attached forms, profile, sandbox, approvals, model, folder.
        { AgentCli.Codex, ["-c", "model=o3"] },
        { AgentCli.Codex, ["-cmodel=o3"] },
        { AgentCli.Codex, ["-c=model=o3"] },
        { AgentCli.Codex, ["--config", "model=o3"] },
        { AgentCli.Codex, ["--config=model=o3"] },
        { AgentCli.Codex, ["-c", "mcp_servers.x.command=evil"] },
        { AgentCli.Codex, ["-c", "model_provider=openai"] },
        { AgentCli.Codex, ["-c", "sandbox_mode=read-only"] },
        { AgentCli.Codex, ["-c", "approval_policy=never"] },
        { AgentCli.Codex, ["-c", "profile=x"] },
        { AgentCli.Codex, ["-c"] }, // a -c with nothing after it would take the app's next argument
        { AgentCli.Codex, ["-mo3"] },
        { AgentCli.Codex, ["-m", "o3"] },
        { AgentCli.Codex, ["--model=o3"] },
        { AgentCli.Codex, ["-p", "fast"] },
        { AgentCli.Codex, ["--profile=fast"] },
        { AgentCli.Codex, ["-s", "read-only"] },
        { AgentCli.Codex, ["--sandbox=read-only"] },
        { AgentCli.Codex, ["-a", "never"] },
        { AgentCli.Codex, ["--ask-for-approval", "never"] },
        { AgentCli.Codex, ["--full-auto"] },
        { AgentCli.Codex, ["--json"] },
        { AgentCli.Codex, ["-C", "elsewhere"] },
        { AgentCli.Codex, ["--cd=elsewhere"] },
        { AgentCli.Codex, ["exec"] },
        { AgentCli.Codex, ["--"] },
        // Copilot and Grok: session, prompt, tools, model.
        { AgentCli.Copilot, ["--resume"] },
        { AgentCli.Copilot, ["--prompt", "x"] },
        { AgentCli.Copilot, ["--prompt=x"] },
        { AgentCli.Copilot, ["-ix"] },
        { AgentCli.Copilot, ["--model=gpt-5"] },
        { AgentCli.Copilot, ["--allow-all-tools"] },
        { AgentCli.Copilot, ["--additional-mcp-config=@x.json"] },
        { AgentCli.Copilot, ["--"] },
        { AgentCli.Grok, ["--resume"] },
        { AgentCli.Grok, ["--prompt", "x"] },
        { AgentCli.Grok, ["--mcp-config", "x.json"] },
        { AgentCli.Grok, ["-m", "grok-4"] },
        { AgentCli.Grok, ["--model=grok-4"] },
        { AgentCli.Grok, ["--session-id=s"] },
        { AgentCli.Grok, ["--"] },
        { AgentCli.Antigravity, ["--prompt=x"] },
        { AgentCli.Antigravity, ["--settings={}"] },
        { AgentCli.Antigravity, ["-mgemini"] },
        { AgentCli.Antigravity, ["--"] },
    };

    [Theory]
    [MemberData(nameof(Smuggled))]
    public void A_locked_family_alias_or_attached_form_is_refused_in_both_lists_and_at_launch(AgentCli cli, string[] args)
    {
        foreach (var (before, after) in new[] { (args, Array.Empty<string>()), (Array.Empty<string>(), args) })
        {
            var problems = AgentArgsValidator.Validate(cli, before, after, []);
            Assert.NotEmpty(problems);
            Assert.Contains(problems, p => p.Reason.Contains("locked", StringComparison.Ordinal) || p.Reason.Contains("needs", StringComparison.Ordinal));
            Assert.Throws<ArgumentException>(() => AgentPresets.Arguments(PolicyFixtures.Layout(TargetOs.Windows, cli),
                AgentPresets.HostSignInMode(cli), RunKind.Headless, "the prompt", "s", null, new ArgvCustomization(before, after, [])));
        }
    }

    public static TheoryData<AgentCli, string[], string[]> Allowed() => new()
    {
        { AgentCli.Claude, ["--effort", "high"], [] },
        { AgentCli.Claude, ["--debug", "--add-dir", "x"], ["--append-system-prompt", "Be brief."] },
        { AgentCli.Claude, ["--append-system-prompt=Be brief."], [] },
        { AgentCli.Codex, ["-c", "model_reasoning_effort=high"], [] },
        { AgentCli.Codex, ["--config=model_reasoning_effort=high"], [] },
        { AgentCli.Codex, [], ["-i", "picture.png"] },
        { AgentCli.Grok, ["--max-turns", "50"], [] },
        { AgentCli.Copilot, [], ["--log-level=debug"] },
    };

    [Theory]
    [MemberData(nameof(Allowed))]
    public void Free_arguments_still_pass(AgentCli cli, string[] before, string[] after)
    {
        Assert.Empty(AgentArgsValidator.Validate(cli, before, after, []));
        AgentPresets.Arguments(PolicyFixtures.Layout(TargetOs.Windows, cli), AgentPresets.HostSignInMode(cli), RunKind.Headless, "p", "s", null,
            new ArgvCustomization(before, after, []));
    }

    [Theory]
    [InlineData(AgentCli.Codex)]
    [InlineData(AgentCli.Copilot)]
    [InlineData(AgentCli.Claude)]
    public void A_flag_with_no_value_cannot_end_the_after_list_where_it_could_take_the_prompt(AgentCli cli)
    {
        var problem = Assert.Single(AgentArgsValidator.Validate(cli, [], ["--add-dir"], []));
        Assert.Contains("--add-dir", problem.Reason);
        Assert.Contains("prompt", problem.Reason);
        Assert.Empty(AgentArgsValidator.Validate(cli, [], ["--add-dir=x"], []));
        Assert.Empty(AgentArgsValidator.Validate(cli, [], ["--add-dir", "x"], []));
    }

    [Theory]
    [InlineData(AgentCli.Claude, "--append-system-prompt")]
    [InlineData(AgentCli.Claude, "--add-dir")]
    [InlineData(AgentCli.Grok, "--max-turns")]
    [InlineData(AgentCli.Copilot, "--log-level")]
    [InlineData(AgentCli.Antigravity, "--model-config")]
    public void No_before_list_can_end_in_a_bare_flag_because_it_could_swallow_the_apps_next_locked_argument(AgentCli cli, string flag)
    {
        // e.g. Claude interactive: ["--append-system-prompt"] then the app's "--strict-mcp-config" would become its value.
        var problem = Assert.Single(AgentArgsValidator.Validate(cli, [flag], [], []));
        Assert.Contains(flag, problem.Reason);
        Assert.Empty(AgentArgsValidator.Validate(cli, [flag, "value"], [], []));
        Assert.Empty(AgentArgsValidator.Validate(cli, [flag + "=value"], [], []));
    }

    [Fact]
    public void Codex_before_list_cannot_end_in_a_bare_flag_either_because_exec_follows()
    {
        Assert.Single(AgentArgsValidator.Validate(AgentCli.Codex, ["--add-dir"], [], []));
        Assert.Empty(AgentArgsValidator.Validate(AgentCli.Codex, ["--add-dir", "x"], [], []));
    }

    [Fact]
    public void The_removable_codex_update_check_can_still_be_removed_but_a_locked_builtin_cannot()
    {
        Assert.Empty(AgentArgsValidator.Validate(AgentCli.Codex, [], [], ["-c check_for_update_on_startup=false"]));
        Assert.Single(AgentArgsValidator.Validate(AgentCli.Codex, [], [], ["--dangerously-bypass-approvals-and-sandbox"]));
        Assert.Single(AgentArgsValidator.Validate(AgentCli.Claude, [], [], ["--print"]));
    }
}
