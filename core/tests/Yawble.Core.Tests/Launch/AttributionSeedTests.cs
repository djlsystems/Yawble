using System.Text.Json.Nodes;
using Yawble.Core.Launch;
using Yawble.Core.Policy;
using Yawble.Core.Sandboxing;
using Yawble.Core.Tests.Policy;
using Xunit;

namespace Yawble.Core.Tests.Launch;

/// <summary>
/// B028: the settings the app seeds for a non-Claude member turn the CLI's own AI attribution (co-author trailer) off
/// where the CLI has an option for it, and keep every other seeded key. Claude's settings are covered in LauncherTests.
/// Fakes only: layouts and seed files, no real CLI.
/// </summary>
public class AttributionSeedTests
{
    private static string CopilotFile(RunLayout l, string name) => AgentPresets.Join(l, l.Workspace, ".copilot", name);

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
    public void TheCopilotConfigKeepsTheTrustedFolderAndTheMcpConfigIsUntouched(CredentialMode mode)
    {
        var l = PolicyFixtures.Layout(TargetOs.Windows, AgentCli.Copilot);
        var seeds = AgentPresets.Seeds(l, mode);
        var config = JsonNode.Parse(seeds.Single(s => s.Path == CopilotFile(l, "config.json")).Content)!.AsObject();

        Assert.Equal([l.ClonePath], config["trustedFolders"]!.AsArray().Select(n => n!.GetValue<string>()));
        // Earlier Copilot versions read user settings from config.json; current ones ignore them there.
        Assert.False(config["includeCoAuthoredBy"]!.GetValue<bool>());
        Assert.Equal(["trustedFolders", "includeCoAuthoredBy"], config.Select(p => p.Key));
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
}
