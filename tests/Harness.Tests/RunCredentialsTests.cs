using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Identity;
using Harness.Messaging;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// THE ONE PLACE A RUN'S CREDENTIAL IS DECIDED. Home sets nothing, reads no store and lists the
/// variables other commands declare; issued places the stored value in the variable its kind
/// declares and lists what the run displaces; anything short of a usable value is missing, which stops the run; and the record never prints
/// or serialises the value.
/// </summary>
public sealed class RunCredentialsTests : IDisposable
{
    private const string Key = "fake-run-credential-5c1e";

    private readonly string _root = Directory.CreateTempSubdirectory("harness-run-credentials-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static AgentCatalog Catalog() => new(AgentCatalogFile.BuiltIns());

    private async Task<string> DatabaseAsync()
    {
        var database = Path.Combine(_root, "messages.db");
        await new SchemaMigrator(database).ApplyAsync(AuthSchema.Steps, ct: Ct);
        return database;
    }

    [Fact]
    public async Task Home_sets_nothing_never_reads_the_store_and_scopes_every_other_commands_declared_variable()
    {
        // No database at all: a home preset must not touch the store.
        var database = Path.Combine(_root, "absent", "messages.db");
        var store = new AgentCredentialStore(database, new EphemeralDataProtectionProvider());
        var catalog = Catalog();
        var credentials = new RunCredentials(catalog, _ => CredentialSource.Home, store);

        foreach (var definition in catalog.Definitions)
        {
            var credential = await credentials.ResolveAsync(definition.Name, null, Ct);

            Assert.Equal(CredentialSource.Home, credential.Source);
            Assert.Empty(credential.Environment);
            Assert.Empty(credential.Displace);
            Assert.False(credential.PerRunHome);
            Assert.Null(credential.Missing);

            // Exactly what every other command declares: none of its own, never the git token.
            var command = RunCredentials.CommandOf(definition);
            var others = catalog.Definitions
                .Where(d => !string.Equals(RunCredentials.CommandOf(d), command, StringComparison.OrdinalIgnoreCase))
                .SelectMany(HomeLaunchBed.Declared)
                .Except([AgentEnvironment.GitHubVariable])
                .Distinct()
                .Order(StringComparer.Ordinal);
            Assert.Equal(others, credential.OtherProviders);
            Assert.Empty(credential.OtherProviders.Intersect(HomeLaunchBed.Declared(definition)));
        }

        // A preset whose command nothing declares loses every declared variable.
        var undeclared = catalog.Definitions.First(d => !catalog.Definitions.Any(
            o => o.IssuedCredential is not null
                && string.Equals(RunCredentials.CommandOf(o), RunCredentials.CommandOf(d), StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(
            catalog.Definitions.SelectMany(HomeLaunchBed.Declared).Distinct().Order(StringComparer.Ordinal),
            (await credentials.ResolveAsync(undeclared.Name, null, Ct)).OtherProviders);

        // An unknown preset is nothing known: nothing set, nothing removed.
        Assert.Same(RunCredential.Home, await credentials.ResolveAsync("no-such-preset", null, Ct));

        Assert.False(File.Exists(database));
        Assert.False(Directory.Exists(Path.GetDirectoryName(database)));
    }

    [Fact]
    public async Task Issued_resolves_the_declared_variable_for_the_kind_set()
    {
        var store = new AgentCredentialStore(await DatabaseAsync(), new EphemeralDataProtectionProvider());
        var catalog = Catalog();
        var credentials = new RunCredentials(catalog, _ => CredentialSource.Issued, store);
        var claude = catalog.Definition("claude-headless")!.IssuedCredential!;

        await store.SetAsync("claude", claude.Kinds.Single(k => k.Kind == IssuedCredential.Token), Key, CredentialActor.Operator, Ct);

        foreach (var preset in new[] { "claude-headless", "claude" })
        {
            var credential = await credentials.ResolveAsync(preset, null, Ct);

            Assert.Equal(CredentialSource.Issued, credential.Source);
            Assert.Null(credential.Missing);
            Assert.True(credential.PerRunHome);
            Assert.Equal(Key, Assert.Single(credential.Environment, e => e.Key == "CLAUDE_CODE_OAUTH_TOKEN").Value);

            // What it displaces is its own declaration's, never the issued variable.
            Assert.Contains("ANTHROPIC_API_KEY", credential.Displace);
            Assert.Contains("CLAUDE_CONFIG_DIR", credential.Displace);
            Assert.DoesNotContain("CLAUDE_CODE_OAUTH_TOKEN", credential.Displace);

            // Every other command's declared variables; never the team's git token.
            Assert.Contains("CODEX_API_KEY", credential.OtherProviders);
            Assert.Contains("COPILOT_GITHUB_TOKEN", credential.OtherProviders);
            Assert.Contains("GROK_CODE_XAI_API_KEY", credential.OtherProviders);
            Assert.DoesNotContain("GH_TOKEN", credential.OtherProviders);
            Assert.DoesNotContain("ANTHROPIC_API_KEY", credential.OtherProviders);
            Assert.Empty(credential.OtherProviders.Intersect(HomeLaunchBed.Declared(catalog.Definition(preset)!)));
        }

        // Nothing stored for grok: missing, with the fix named.
        var grok = await credentials.ResolveAsync("grok-headless", null, Ct);
        Assert.Contains("`grok` is not set", grok.Missing);
        Assert.Contains("Admin > Agents", grok.Missing);
        Assert.Empty(grok.Environment);
    }

    [Fact]
    public async Task An_undecryptable_value_is_missing()
    {
        var database = await DatabaseAsync();
        var claude = Catalog().Definition("claude-headless")!.IssuedCredential!;

        // Written under one key ring and read under another: a lost `keys/`.
        await new AgentCredentialStore(database, new EphemeralDataProtectionProvider())
            .SetAsync("claude", claude.Kinds[0], Key, CredentialActor.Operator, Ct);

        var store = new AgentCredentialStore(database, new EphemeralDataProtectionProvider());
        var credential = await new RunCredentials(Catalog(), _ => CredentialSource.Issued, store).ResolveAsync("claude-headless", null, Ct);

        Assert.NotNull(credential.Missing);
        Assert.Empty(credential.Environment);
        Assert.False((await store.StatusAsync("claude", Ct)).Set);
    }

    [Fact]
    public async Task A_preset_issued_with_no_declaration_is_missing_rather_than_home()
    {
        var store = new AgentCredentialStore(await DatabaseAsync(), new EphemeralDataProtectionProvider());
        var catalog = new AgentCatalog([new AgentDefinition("bare", AgentMode.Headless, new AgentLaunch("claude", []))]);

        var credential = await new RunCredentials(catalog, _ => CredentialSource.Issued, store).ResolveAsync("bare", null, Ct);

        Assert.Equal(CredentialSource.Issued, credential.Source);
        Assert.Contains("declares none", credential.Missing);
    }

    [Fact]
    public async Task A_run_credential_never_serialises_or_prints_its_value()
    {
        var store = new AgentCredentialStore(await DatabaseAsync(), new EphemeralDataProtectionProvider());
        var claude = Catalog().Definition("claude-headless")!.IssuedCredential!;
        await store.SetAsync("claude", claude.Kinds[0], Key, CredentialActor.Operator, Ct);

        var credential = await new RunCredentials(Catalog(), _ => CredentialSource.Issued, store).ResolveAsync("claude-headless", null, Ct);
        Assert.Equal(Key, credential.Environment["ANTHROPIC_API_KEY"]);

        Assert.DoesNotContain(Key, credential.ToString());
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(credential));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(credential, JsonSerializerOptions.Web));

        // Nor through the invocation that carries it.
        var invocation = new AgentInvocation(new ContainerId("T", "M"), "s", "p", "/w", new Dictionary<string, string>(), Credential: credential);
        Assert.DoesNotContain(Key, invocation.ToString());
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(invocation));
    }
}
