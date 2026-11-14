using System.Text.Json.Nodes;
using Yawble.Core.Launch;
using Yawble.Core.Policy;
using Yawble.Core.Sandboxing;
using Yawble.Core.Tests.Policy;
using Xunit;

namespace Yawble.Core.Tests.Launch;

/// <summary>
/// B028: the settings the app seeds for a member turn the CLI's own AI attribution (co-author trailer, "generated with"
/// line) off where the CLI has an option for it, and keep every other seeded key. Fakes only: layouts and seed files,
/// no real CLI.
/// </summary>
public class AttributionSeedTests
{
    private static JsonNode Seed(RunLayout l, CredentialMode mode, string fileName) =>
        JsonNode.Parse(AgentPresets.Seeds(l, mode).Single(s => Path.GetFileName(s.Path) == fileName).Content)!;

    private static string CopilotFile(RunLayout l, string name) => AgentPresets.Join(l, l.Workspace, ".copilot", name);

    public static TheoryData<TargetOs, CredentialMode> ClaudeModes() => new()
    {
        { TargetOs.Windows, CredentialMode.ClaudeSubscription },
        { TargetOs.Windows, CredentialMode.ClaudeGatewaySignIn },
        { TargetOs.Linux, CredentialMode.ClaudeHostSignIn },
        { TargetOs.MacOS, CredentialMode.ClaudeSubscription },
    };

    [Theory]
    [MemberData(nameof(ClaudeModes))]
    public void TheClaudeSettingsHaveAttributionOffAndTheOtherSettingsIntact(TargetOs os, CredentialMode mode)
    {
        var l = PolicyFixtures.Layout(os, AgentCli.Claude);
        var settings = Seed(l, mode, "settings.json").AsObject();

        Assert.Equal("", settings["attribution"]!["commit"]!.GetValue<string>());
        Assert.Equal("", settings["attribution"]!["pr"]!.GetValue<string>());
        Assert.Equal(2, settings["attribution"]!.AsObject().Count);
        Assert.False(settings["includeCoAuthoredBy"]!.GetValue<bool>());
        // Every key the seed had before B028 is still there, unchanged.
        Assert.Equal("bypassPermissions", settings["permissions"]!["defaultMode"]!.GetValue<string>());
        Assert.True(settings["skipDangerousModePermissionPrompt"]!.GetValue<bool>());
        Assert.Equal(["permissions", "skipDangerousModePermissionPrompt", "attribution", "includeCoAuthoredBy"],
            settings.Select(p => p.Key));
        // The settings file is the member's own (under its CLAUDE_CONFIG_DIR), never the person's ~/.claude.
        var path = AgentPresets.Seeds(l, mode).Single(s => Path.GetFileName(s.Path) == "settings.json").Path;
        Assert.StartsWith(AgentPresets.ClaudeConfigDir(l), path);
        Assert.StartsWith(l.Workspace, path);
    }

    [Fact]
    public void TheClaudeStateFileIsUnchanged()
    {
        var l = PolicyFixtures.Layout(TargetOs.Windows, AgentCli.Claude);
        var state = Seed(l, CredentialMode.ClaudeSubscription, ".claude.json").AsObject();
        Assert.Equal(["hasCompletedOnboarding", "hasSeenAutoDefaultNudge", "projects"], state.Select(p => p.Key));
        Assert.Null(state["attribution"]);
    }

    [Fact]
    public void OnDiskTheSeedTurnsAttributionBackOffAndKeepsWhatTheCliKept()
    {
        var root = Directory.CreateTempSubdirectory("yawble-attribution-").FullName;
        try
        {
            var l = TempLayout(root);
            var seeds = AgentPresets.Seeds(l, CredentialMode.ClaudeSubscription);
            RunFolders.Prepare(l, seeds);
            var file = Path.Combine(AgentPresets.ClaudeConfigDir(l), "settings.json");
            // Between runs the CLI (or the model) writes its own keys and turns attribution back on.
            var kept = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            kept["theme"] = "dark";
            kept["attribution"] = new JsonObject { ["commit"] = "Co-Authored-By: someone", ["pr"] = "Generated with x" };
            kept["includeCoAuthoredBy"] = true;
            File.WriteAllText(file, kept.ToJsonString());

            RunFolders.Prepare(l, seeds);
            var after = JsonNode.Parse(File.ReadAllText(file))!;
            Assert.Equal("dark", after["theme"]!.GetValue<string>());
            Assert.Equal("", after["attribution"]!["commit"]!.GetValue<string>());
            Assert.Equal("", after["attribution"]!["pr"]!.GetValue<string>());
            Assert.False(after["includeCoAuthoredBy"]!.GetValue<bool>());
            Assert.True(after["skipDangerousModePermissionPrompt"]!.GetValue<bool>());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(TargetOs.Windows, CredentialMode.CopilotGatewaySignIn)]
    [InlineData(TargetOs.Windows, CredentialMode.CopilotHostSignIn)]
    [InlineData(TargetOs.Linux, CredentialMode.CopilotGatewaySignIn)]
    public void TheCopilotUserSettingsHaveTheCoAuthorTrailerOff(TargetOs os, CredentialMode mode)
    {
        var l = PolicyFixtures.Layout(os, AgentCli.Copilot);
        var seeds = AgentPresets.Seeds(l, mode);
        // Current Copilot CLI reads user settings from settings.json in COPILOT_HOME (~/.copilot, the member's home).
        var settings = JsonNode.Parse(seeds.Single(s => s.Path == CopilotFile(l, "settings.json")).Content)!.AsObject();
        Assert.False(settings["includeCoAuthoredBy"]!.GetValue<bool>());
        Assert.Equal(["includeCoAuthoredBy"], settings.Select(p => p.Key));
        Assert.StartsWith(l.Workspace, CopilotFile(l, "settings.json"));
    }

    [Theory]
    [InlineData(CredentialMode.CopilotGatewaySignIn)]
    [InlineData(CredentialMode.CopilotHostSignIn)]
    public void TheCopilotConfigHasTheCoAuthorTrailerOffAndTheTrustedFolderIntact(CredentialMode mode)
    {
        var l = PolicyFixtures.Layout(TargetOs.Windows, AgentCli.Copilot);
        var seeds = AgentPresets.Seeds(l, mode);
        var config = JsonNode.Parse(seeds.Single(s => s.Path == CopilotFile(l, "config.json")).Content)!.AsObject();

        // Earlier Copilot versions read user settings from config.json; current ones ignore them there.
        Assert.False(config["includeCoAuthoredBy"]!.GetValue<bool>());
        Assert.Equal([l.ClonePath], config["trustedFolders"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(["trustedFolders", "includeCoAuthoredBy"], config.Select(p => p.Key));
        // The MCP config beside it is not touched.
        var mcp = JsonNode.Parse(seeds.Single(s => s.Path == AgentPresets.CopilotMcpConfigPath(l)).Content)!;
        Assert.Null(mcp["includeCoAuthoredBy"]);
        Assert.NotNull(mcp["mcpServers"]!["bus"]);
        Assert.Equal(3, seeds.Count);
    }

    [Fact]
    public void OnDiskTheCopilotSeedTurnsTheTrailerBackOffAndKeepsWhatTheCliWrote()
    {
        var root = Directory.CreateTempSubdirectory("yawble-attribution-").FullName;
        try
        {
            var file = Path.Combine(root, "settings.json");
            // Between runs the person's /settings (or the CLI) adds its own keys and turns the trailer back on.
            File.WriteAllText(file, "{\"model\": \"some-model\", \"includeCoAuthoredBy\": true}");
            var merged = JsonNode.Parse(RunFolders.MergeJsonSeed(new SeedFile(file, "{\"includeCoAuthoredBy\": false}")))!;
            Assert.Equal("some-model", merged["model"]!.GetValue<string>());
            Assert.False(merged["includeCoAuthoredBy"]!.GetValue<bool>());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(CredentialMode.CodexGatewaySignIn, AgentCli.Codex)]
    [InlineData(CredentialMode.GrokGatewaySignIn, AgentCli.Grok)]
    [InlineData(CredentialMode.AntigravityHostSignIn, AgentCli.Antigravity)]
    public void TheClisWithNoAttributionOptionGetNoInventedKey(CredentialMode mode, AgentCli cli)
    {
        var l = PolicyFixtures.Layout(TargetOs.Windows, cli);
        foreach (var seed in AgentPresets.Seeds(l, mode))
        {
            Assert.DoesNotContain("attribution", seed.Content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("CoAuthored", seed.Content, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static RunLayout TempLayout(string root) =>
        RunLayout.Standard(TargetOsExtensions.Host, Path.Combine(root, "home"), Path.Combine(root, "app"), Path.Combine(root, "install"),
            new MemberRef("alpha", "dev1"), AgentCli.Claude, Agents.TestClis.InstallFor(root, Path.Combine(root, "home"), AgentCli.Claude), []);
}
