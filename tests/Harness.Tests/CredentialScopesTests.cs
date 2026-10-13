using System.Text.Json;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// THE ONE PLACE A RUN'S CREDENTIAL SCOPE IS DECIDED: a run of each preset is scoped away from exactly
/// the variables other commands declare, never its own and never the team's git token, and the scope
/// carries names only.
/// </summary>
public sealed class CredentialScopesTests
{
    private static AgentCatalog Catalog() => new(AgentCatalogFile.BuiltIns());

    [Fact]
    public void A_run_is_scoped_away_from_every_other_commands_declared_variable_and_keeps_its_own()
    {
        var catalog = Catalog();

        foreach (var definition in catalog.Definitions)
        {
            var scope = CredentialScopes.For(catalog, definition.Name);

            // Exactly what every other command declares: none of its own, never the git token.
            var command = CredentialScopes.CommandOf(definition);
            var others = catalog.Definitions
                .Where(d => !string.Equals(CredentialScopes.CommandOf(d), command, StringComparison.OrdinalIgnoreCase))
                .SelectMany(HomeLaunchBed.Declared)
                .Except([AgentEnvironment.GitHubVariable])
                .Distinct()
                .Order(StringComparer.Ordinal);
            Assert.Equal(others, scope.OtherCommands);
            Assert.Empty(scope.OtherCommands.Intersect(HomeLaunchBed.Declared(definition)));
        }

        // A preset whose command nothing declares loses every declared variable.
        var undeclared = catalog.Definitions.First(d => !catalog.Definitions.Any(
            o => o.CredentialVariables is not null
                && string.Equals(CredentialScopes.CommandOf(o), CredentialScopes.CommandOf(d), StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(
            catalog.Definitions.SelectMany(HomeLaunchBed.Declared).Distinct().Order(StringComparer.Ordinal),
            CredentialScopes.For(catalog, undeclared.Name).OtherCommands);

        // An unknown preset is nothing known: nothing removed.
        Assert.Same(CredentialScope.None, CredentialScopes.For(catalog, "no-such-preset"));
    }

    [Fact]
    public void Applying_a_scope_removes_what_was_not_handed_in_and_keeps_what_was()
    {
        var environment = new Dictionary<string, string?> { ["A_KEY"] = "host", ["B_KEY"] = "host", ["OTHER"] = "kept" };

        new CredentialScope(["A_KEY", "B_KEY"]).ApplyTo(environment, new Dictionary<string, string> { ["B_KEY"] = "team" });

        Assert.False(environment.ContainsKey("A_KEY"));
        Assert.Equal("host", environment["B_KEY"]);
        Assert.Equal("kept", environment["OTHER"]);
    }

    [Fact]
    public void A_scope_carries_names_only_and_round_trips_as_json()
    {
        var scope = CredentialScopes.For(Catalog(), "claude-headless");
        var json = JsonSerializer.Serialize(scope);

        Assert.Equal(scope.OtherCommands, JsonSerializer.Deserialize<CredentialScope>(json)!.OtherCommands);
        Assert.Contains("XAI_API_KEY", json);
    }
}
