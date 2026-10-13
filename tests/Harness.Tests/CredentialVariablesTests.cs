using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// THE BUILT-IN DECLARATIONS ARE THE MEASURED ONES. Each built-in language-model preset names the
/// variables its CLI reads for authentication, with the version measured on the CLI in the image, and
/// no declaration names a variable the launch sets itself.
/// </summary>
public sealed class CredentialVariablesTests
{
    /// <summary>What the launches measured, per command: every variable the CLI reads for authentication.</summary>
    private static readonly Dictionary<string, string[]> Measured = new()
    {
        ["claude"] = ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "CLAUDE_CODE_OAUTH_TOKEN"],
        ["codex"] = ["CODEX_ACCESS_TOKEN", "CODEX_API_KEY", "OPENAI_API_KEY"],
        ["copilot"] = ["COPILOT_GITHUB_TOKEN"],
        ["grok"] = ["GROK_CODE_XAI_API_KEY", "XAI_API_KEY"],
    };

    [Fact]
    public void Every_built_in_language_model_preset_declares_its_credential_variables()
    {
        var models = AgentCatalogFile.BuiltIns().Where(p => p.Launch.LanguageModel).ToList();
        Assert.NotEmpty(models);

        foreach (var preset in models)
        {
            var declaration = preset.CredentialVariables;
            Assert.True(declaration is not null, $"'{preset.Name}' declares no credential variables.");

            var command = CredentialScopes.CommandOf(preset);
            Assert.True(Measured.ContainsKey(command), $"'{preset.Name}' launches `{command}`, which was not measured.");

            Assert.Equal(Measured[command], declaration.Variables.Order(StringComparer.Ordinal));
            Assert.False(string.IsNullOrWhiteSpace(declaration.MeasuredWith), $"'{preset.Name}' names no measured version.");
            Assert.Null(CredentialVariables.Refusal(preset.Name, declaration));
        }
    }

    [Theory]
    [InlineData("HOME")]
    [InlineData("PATH")]
    [InlineData("TMPDIR")]
    [InlineData("HARNESS_KEY")]
    [InlineData("harness_url")]
    [InlineData("NOT A NAME")]
    public void No_declaration_names_a_variable_the_launch_reserves(string variable)
    {
        foreach (var preset in AgentCatalogFile.BuiltIns().Where(p => p.CredentialVariables is not null))
        {
            Assert.DoesNotContain(variable, preset.CredentialVariables!.Variables);
        }

        Assert.NotNull(CredentialVariables.Refusal("custom", new CredentialVariables([variable], "1")));
        Assert.NotNull(CredentialVariables.Refusal("custom", new CredentialVariables(["MY_KEY", variable], "1")));
        Assert.NotNull(CredentialVariables.Refusal("custom", new CredentialVariables([], "1")));
    }

    [Fact]
    public void A_declaration_round_trips_through_the_catalog_file_in_its_contract_shape()
    {
        var claude = AgentCatalogFile.BuiltIns().First(p => p.Name == "claude-headless");
        var json = System.Text.Json.JsonSerializer.Serialize(claude, AgentCatalogFile.JsonOptions);

        Assert.Contains("\"credentialVariables\"", json);
        Assert.Contains("\"ANTHROPIC_API_KEY\"", json);

        var back = System.Text.Json.JsonSerializer.Deserialize<AgentDefinition>(json, AgentCatalogFile.JsonOptions)!;
        Assert.Equal(claude.CredentialVariables!.Variables, back.CredentialVariables!.Variables);
        Assert.Equal(json, System.Text.Json.JsonSerializer.Serialize(back, AgentCatalogFile.JsonOptions));
    }
}
