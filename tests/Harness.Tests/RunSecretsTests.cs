using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// WHAT A MEMBER RUN'S OWN CREDENTIAL IS: its issued value and every credential variable the
/// environment its child is given carries, nothing else; kept per member until its next run; and,
/// for a finished transcript, joined with the set a run would get now. Names come from the catalog
/// and the probe file, never from a literal.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class RunSecretsTests
{
    private const string Marker = DiagnosticRedaction.Placeholder;

    private static readonly ContainerId Dev = new("alpha", "dev");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AgentCatalog Catalog() => new(AgentCatalogFile.BuiltIns());

    /// <summary>A probe-file provider variable, and a declared one that is not a probe-file variable when there is one.</summary>
    private static (string Provider, string Declared, string Issued) Names(AgentCatalog catalog)
    {
        var provider = AgentEnvironment.ProviderVariables.Order(StringComparer.Ordinal).First();
        var declared = catalog.Definitions.SelectMany(HomeLaunchBed.Declared).Order(StringComparer.Ordinal).ToList();
        var issued = catalog.Definitions.First(d => d.IssuedCredential is not null).IssuedCredential!.Kinds[0].Variable;
        var other = declared.FirstOrDefault(n => !AgentEnvironment.ProviderVariables.Contains(n) && n != issued)
            ?? declared.First(n => n != provider && n != issued);
        return (provider, other, issued);
    }

    [Fact]
    public void The_set_is_the_issued_value_and_every_credential_variable_the_environment_carries()
    {
        var catalog = Catalog();
        var (provider, declared, issued) = Names(catalog);
        var homeVariable = catalog.Definitions.SelectMany(d => d.IssuedCredential?.HomeVariables ?? []).FirstOrDefault();

        var credential = new RunCredential(
            CredentialSource.Issued, new Dictionary<string, string> { [issued] = "fake-issued-value-Rb5kM2" }, [], [], true, null);

        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [provider] = "fake-provider-value-Lw8dT4",
            [declared] = "fake-declared-value-Qy1nZ6",
            ["HOME"] = "/fake/home/folder-long-enough",
            ["PATH"] = "/fake/bin:/fake/usr/bin-long-enough",
            ["HARNESS_URL"] = "http://127.0.0.1:5391/long-enough",
            ["TMPDIR"] = "/fake/member/tmp-long-enough",
        };
        if (homeVariable is not null) environment[homeVariable] = "/fake/config/folder-long-enough";

        var redactor = RunSecrets.Of(environment, credential, catalog);
        var text = string.Join(" ", environment.Values.Append("fake-issued-value-Rb5kM2"));
        var redacted = redactor.Apply(text);

        foreach (var value in new[] { "fake-issued-value-Rb5kM2", "fake-provider-value-Lw8dT4", "fake-declared-value-Qy1nZ6" })
        {
            Assert.DoesNotContain(value, redacted, StringComparison.Ordinal);
        }

        foreach (var name in new[] { "HOME", "PATH", "HARNESS_URL", "TMPDIR" }.Concat(homeVariable is null ? [] : [homeVariable]))
        {
            Assert.Contains(environment[name]!, redacted, StringComparison.Ordinal);
        }

        Assert.Equal(3, redacted.Split(Marker).Length - 1);

        // A run given no credential variable has nothing to redact.
        Assert.Same(ValueRedactor.Empty, RunSecrets.Of(new Dictionary<string, string?>(), RunCredential.Home, catalog));
    }

    [Fact]
    public void A_member_keeps_its_last_runs_set_until_its_next_run()
    {
        var secrets = new RunSecrets(Catalog());
        Assert.Same(ValueRedactor.Empty, secrets.For(Dev));

        secrets.Remember(Dev, ValueRedactor.For(["fake-first-run-Kc3v"]));
        Assert.Equal(Marker, secrets.For(Dev).Apply("fake-first-run-Kc3v"));

        secrets.Remember(Dev, ValueRedactor.For(["fake-second-run-Jw7s"]));
        Assert.Equal("fake-first-run-Kc3v", secrets.For(Dev).Apply("fake-first-run-Kc3v"));
        Assert.Equal(Marker, secrets.For(Dev).Apply("fake-second-run-Jw7s"));

        Assert.Same(ValueRedactor.Empty, secrets.For(new ContainerId("alpha", "other")));
    }

    [Fact]
    public async Task A_finished_transcript_is_read_with_the_runs_set_and_the_set_a_run_would_get_now()
    {
        var catalog = Catalog();
        var (provider, declared, issued) = Names(catalog);
        var preset = catalog.Definitions.First(d => d.IssuedCredential is not null).Name;

        using var host = new EnvironmentScope([new(provider, "fake-host-key-Vn2xB9")]);
        var resolver = new FixedCredentials(new RunCredential(
            CredentialSource.Issued, new Dictionary<string, string> { [issued] = "fake-issued-now-Ps6gH1" }, [], [], true, null));
        var member = new Dictionary<string, string> { [declared] = "fake-member-env-Td4jW8" };

        // An empty registry: a Host that restarted since the run.
        var secrets = new RunSecrets(catalog, resolver);
        var redactor = await secrets.ForReadAsync(Dev, preset, member, Ct);

        var text = "fake-host-key-Vn2xB9 fake-issued-now-Ps6gH1 fake-member-env-Td4jW8 fake-run-only-Ue5rQ3";
        Assert.Equal($"{Marker} {Marker} {Marker} fake-run-only-Ue5rQ3", redactor.Apply(text));

        // With the run's own set still held, that is redacted too.
        secrets.Remember(Dev, ValueRedactor.For(["fake-run-only-Ue5rQ3"]));
        redactor = await secrets.ForReadAsync(Dev, preset, member, Ct);
        Assert.Equal($"{Marker} {Marker} {Marker} {Marker}", redactor.Apply(text));
    }

    private sealed class FixedCredentials(RunCredential credential) : IRunCredentials
    {
        public Task<RunCredential> ResolveAsync(string agent, AgentDefinition? definition, CancellationToken ct) =>
            Task.FromResult(credential);
    }
}
