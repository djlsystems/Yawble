using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// THE BUILT-IN DECLARATIONS ARE THE MEASURED ONES. Each built-in language-model preset says how its
/// CLI takes an issued credential, with the variables, precedence and version measured on the CLI in
/// the image; presets that launch one command declare the same kinds, because one value is stored per
/// command; and no declaration names a variable the launch sets itself.
/// </summary>
public sealed class IssuedCredentialDeclarationTests
{
    /// <summary>What the launches measured, per command: kind and variable, what is displaced, and
    /// the precedence over a home login of the headless preset.</summary>
    private static readonly Dictionary<string, (string[] Kinds, string[] Displaces, string Precedence)> Measured = new()
    {
        ["claude"] = (["apiKey=ANTHROPIC_API_KEY", "token=CLAUDE_CODE_OAUTH_TOKEN"],
            ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "CLAUDE_CODE_OAUTH_TOKEN"], IssuedCredential.CredentialWins),
        ["codex"] = (["apiKey=CODEX_API_KEY"], ["CODEX_ACCESS_TOKEN", "CODEX_API_KEY", "OPENAI_API_KEY"], IssuedCredential.CredentialWins),
        ["copilot"] = (["token=COPILOT_GITHUB_TOKEN"], ["COPILOT_GITHUB_TOKEN"], IssuedCredential.CredentialWins),
        ["grok"] = (["apiKey=XAI_API_KEY"], ["GROK_CODE_XAI_API_KEY", "XAI_API_KEY"], IssuedCredential.LoginWins),
    };

    [Fact]
    public void Every_built_in_language_model_preset_declares_its_issued_credential()
    {
        var models = AgentCatalogFile.BuiltIns().Where(p => p.Launch.LanguageModel).ToList();
        Assert.NotEmpty(models);

        foreach (var preset in models)
        {
            var declaration = preset.IssuedCredential;
            Assert.True(declaration is not null, $"'{preset.Name}' declares no issued credential.");

            var command = RunCredentials.CommandOf(preset);
            Assert.True(Measured.ContainsKey(command), $"'{preset.Name}' launches `{command}`, which was not measured.");
            var measured = Measured[command];

            Assert.Equal(measured.Kinds, declaration.Kinds.Select(k => $"{k.Kind}={k.Variable}").Order());
            Assert.Equal(measured.Displaces, declaration.Displaces.Order(StringComparer.Ordinal));
            Assert.False(string.IsNullOrWhiteSpace(declaration.MeasuredWith), $"'{preset.Name}' names no measured version.");
            Assert.Null(IssuedCredential.Refusal(preset.Name, declaration));

            // Every variable a person's credential can go in is one the run displaces first.
            Assert.All(declaration.Kinds, k => Assert.Contains(k.Variable, declaration.Displaces));

            if (preset.Mode == AgentMode.Headless) Assert.Equal(measured.Precedence, declaration.LoginPrecedence);
        }

        // The Concierge's codex is a TUI no plain terminal could drive, so it says unmeasured.
        Assert.Equal(IssuedCredential.Unmeasured, AgentCatalogFile.BuiltIns().Single(p => p.Name == "codex").IssuedCredential!.LoginPrecedence);
        Assert.Equal(IssuedCredential.CredentialWins, AgentCatalogFile.BuiltIns().Single(p => p.Name == "claude").IssuedCredential!.LoginPrecedence);
        Assert.Equal(IssuedCredential.LoginWins, AgentCatalogFile.BuiltIns().Single(p => p.Name == "grok").IssuedCredential!.LoginPrecedence);
    }

    [Fact]
    public void Presets_launching_the_same_command_declare_the_same_kinds()
    {
        Assert.Null(AgentCredentials.DeclarationRefusal(AgentCatalogFile.BuiltIns()));

        foreach (var group in AgentCatalogFile.BuiltIns().Where(p => p.IssuedCredential is not null).GroupBy(RunCredentials.CommandOf))
        {
            Assert.All(group, p => Assert.True(p.IssuedCredential!.SameKinds(group.First().IssuedCredential!), group.Key));
        }

        // A custom preset launching claude with another variable would split one stored value in two.
        var claude = AgentCatalogFile.BuiltIns().First(p => p.Name == "claude-headless");
        var differs = new AgentDefinition("my-claude", AgentMode.Headless, claude.Launch,
            IssuedCredential: claude.IssuedCredential! with { Kinds = [new(IssuedCredential.ApiKey, "MY_KEY")] });
        Assert.Contains("declare different issued-credential kinds", AgentCredentials.DeclarationRefusal([.. AgentCatalogFile.BuiltIns(), differs]));
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
        foreach (var preset in AgentCatalogFile.BuiltIns().Where(p => p.IssuedCredential is not null))
        {
            var declaration = preset.IssuedCredential!;
            Assert.DoesNotContain(variable, declaration.Kinds.Select(k => k.Variable).Concat(declaration.Displaces).Concat(declaration.HomeVariables ?? []));
        }

        var refused = new IssuedCredential([new(IssuedCredential.ApiKey, variable)], [], IssuedCredential.Unmeasured, "1");
        Assert.NotNull(IssuedCredential.Refusal("custom", refused));

        var displaced = new IssuedCredential([new(IssuedCredential.ApiKey, "MY_KEY")], [variable], IssuedCredential.Unmeasured, "1");
        Assert.NotNull(IssuedCredential.Refusal("custom", displaced));
    }

    [Fact]
    public void A_declaration_round_trips_through_the_catalog_file_in_its_contract_shape()
    {
        var claude = AgentCatalogFile.BuiltIns().First(p => p.Name == "claude-headless");
        var json = System.Text.Json.JsonSerializer.Serialize(claude, AgentCatalogFile.JsonOptions);

        Assert.Contains("\"issuedCredential\"", json);
        Assert.Contains("\"kind\": \"apiKey\"", json);
        Assert.Contains("\"loginPrecedence\": \"credential\"", json);

        var back = System.Text.Json.JsonSerializer.Deserialize<AgentDefinition>(json, AgentCatalogFile.JsonOptions)!;
        Assert.True(back.IssuedCredential!.SameKinds(claude.IssuedCredential!));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(claude, AgentCatalogFile.JsonOptions),
            System.Text.Json.JsonSerializer.Serialize(back, AgentCatalogFile.JsonOptions));
    }
}
