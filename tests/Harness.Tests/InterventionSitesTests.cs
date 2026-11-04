using System.Text.RegularExpressions;
using Harness.Contracts;
using Harness.Host;
using Harness.Tests.Host;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// A MISSED WRITER SITE SILENTLY LOSES A PERSON'S ACT, so the sites are a list
/// (<see cref="InterventionSites"/>) held here: each listed route exists; every kind in the closed set has
/// a site or is named as having no act today; and every Host call that hands the log a person's
/// principal either records an act with it or is named below as no act of the set. A new person-act
/// route that forgets fails the last one. Each listed route is driven by a person in
/// <c>InterventionLedgerTests</c> and <c>PluginNeedsPersonTests</c>.
/// </summary>
public sealed partial class InterventionSitesTests(HostFixture host) : IClassFixture<HostFixture>
{
    /// <summary>
    /// The Host's appends for a person's principal that record no act of the set, by file and count: a
    /// wake cancel, the Manager's instruction a resume sends after its <c>workflow.resumed</c> row, a
    /// person's kanban plan, card move, edit and comment, a dispatch's actor (its act is recorded where the
    /// dispatched row is appended), and the repository actions (pull request, bring current, merge).
    /// </summary>
    private static readonly Dictionary<string, int> NoActAppends = new(StringComparer.Ordinal)
    {
        ["WorkflowEndpoints.cs"] = 2,
        ["BacklogEndpoints.cs"] = 2,
        ["KanbanEndpoints.cs"] = 3,
        ["RepoPullRequestEndpoints.cs"] = 1,
        ["RepoEndpoints.cs"] = 2,
    };

    [Fact]
    public void Every_listed_route_exists_with_its_method()
    {
        var endpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? [])
                .Select(method => $"{method} {e.RoutePattern.RawText}"))
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(InterventionSites.Routes, site => Assert.Contains($"{site.Method} {site.Route}", endpoints));
    }

    [Fact]
    public void Every_kind_has_a_writer_site_or_is_named_as_having_no_act_today()
    {
        var written = InterventionSites.Routes.Select(s => s.Kind).Append(InterventionKinds.ConciergeSession).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            InterventionKinds.All.Order(StringComparer.Ordinal),
            written.Concat(InterventionSites.NoActToday).Distinct().Order(StringComparer.Ordinal));
        Assert.Empty(written.Intersect(InterventionSites.NoActToday));
        Assert.All(InterventionSites.Routes, site => Assert.True(InterventionKinds.IsKnown(site.Kind)));
    }

    [Fact]
    public void Every_append_for_a_persons_principal_records_its_act_or_is_named_as_no_act()
    {
        var root = RepoRoot();
        var unrecorded = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", "Harness.Host"), "*.cs", SearchOption.AllDirectories))
        {
            foreach (var line in File.ReadLines(file).Where(l => l.Contains("AppendContexts.From(", StringComparison.Ordinal)))
            {
                if (line.TrimStart().StartsWith("///", StringComparison.Ordinal) || line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                if (line.Contains("Intervening(", StringComparison.Ordinal) || line.Contains("InterventionAct.Of(", StringComparison.Ordinal)) continue;
                if (Path.GetFileName(file) == "AppendContexts.cs") continue;

                var name = Path.GetFileName(file);
                unrecorded[name] = unrecorded.GetValueOrDefault(name) + 1;
            }
        }

        Assert.Equal(
            NoActAppends.OrderBy(p => p.Key, StringComparer.Ordinal),
            unrecorded.OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    [Fact]
    public void Nothing_outside_the_writers_inserts_into_the_ledger()
    {
        var root = RepoRoot();
        var writers = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => InsertInto().IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal);

        Assert.Equal(["InterventionLedgerRow.cs"], writers);
    }

    [GeneratedRegex(@"INSERT\s+INTO\s+intervention_ledger", RegexOptions.IgnoreCase)]
    private static partial Regex InsertInto();

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Harness.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find the repository root.");
    }
}

/// <summary>
/// THE CLOSED KIND SET AND THE ACTOR RULE (<see cref="InterventionKinds"/>, <see cref="InterventionAct"/>):
/// ten words spelled once; a word outside them refused; a person or a person's key acts, nobody else;
/// only a Concierge session carries a measured duration; and a credential's save records that it
/// happened, for a person, and never a clear.
/// </summary>
public sealed class InterventionKindsTests
{
    [Fact]
    public void The_set_is_the_ten_kinds_and_a_word_outside_it_is_refused()
    {
        Assert.Equal(
            ["dispatch", "instruction", "decision_answered", "resume", "nudge", "close", "outcome_changed",
             "credential_supplied", "access_resolved", "concierge_session"],
            InterventionKinds.All);
        Assert.Equal([InterventionKinds.ConciergeSession], InterventionKinds.Measured);

        Assert.False(InterventionKinds.IsKnown("Close"));
        Assert.False(InterventionKinds.IsKnown(null));
        var refused = Assert.Throws<ArgumentException>(() => InterventionKinds.Require("pressed_a_button"));
        Assert.StartsWith("'pressed_a_button' is not an intervention kind; the kinds are dispatch, instruction", refused.Message);
    }

    [Fact]
    public void A_person_is_their_id_and_email_a_key_is_its_owner_and_nobody_else_acts()
    {
        Assert.Equal(
            new InterventionAct(InterventionKinds.Close, "user-1", "ada@example.test", "Alpha", 42),
            InterventionAct.Of(InterventionKinds.Close, new AppendContext(PrincipalKind.User, "user-1", Email: "ada@example.test"), "Alpha", 42));
        Assert.Equal(
            new InterventionAct(InterventionKinds.Instruction, "user-2", null, "Alpha"),
            InterventionAct.Of(InterventionKinds.Instruction, new AppendContext(PrincipalKind.ApiKey, "apikey-1", OwnerUserId: "user-2"), "Alpha"));

        foreach (var nobody in new AppendContext?[]
        {
            new AppendContext(PrincipalKind.Container, "Alpha/Worker"),
            new AppendContext(PrincipalKind.Concierge, "c-1", OwnerUserId: "user-1"),
            new AppendContext(PrincipalKind.TenantConcierge, "c-2", OwnerUserId: "user-1"),
            new AppendContext(PrincipalKind.ApiKey, "apikey-orphan"),
            null,
        })
        {
            Assert.Null(InterventionAct.Of(InterventionKinds.Instruction, nobody, "Alpha"));
            Assert.Null(nobody?.Intervening(InterventionKinds.Instruction, "Alpha").Intervention);
        }
    }

    [Fact]
    public void Only_a_concierge_session_carries_a_measured_duration()
    {
        var session = InterventionAct.Session("user-1", "Alpha", 90);
        Assert.Equal((InterventionKinds.ConciergeSession, 90L, true), (session.Kind, session.DurationSeconds, session.DurationMeasured));
        Assert.Equal(0, InterventionAct.Session("user-1", null, -5).DurationSeconds);

        Assert.Throws<ArgumentException>(() => InterventionAct.Of(InterventionKinds.ConciergeSession, new AppendContext(PrincipalKind.User, "user-1"), null));
        Assert.Throws<ArgumentException>(() => new InterventionAct(InterventionKinds.Nudge, "user-1", null, null, null, 30).Checked());
        Assert.False(InterventionAct.Of(InterventionKinds.Nudge, new AppendContext(PrincipalKind.User, "user-1"), null)!.DurationMeasured);
    }

    [Fact]
    public void A_credentials_save_by_a_person_records_that_it_happened_and_a_clear_or_the_platform_records_none()
    {
        var person = new CredentialActor("user-1", "ada@example.test");
        Assert.Equal(
            new InterventionAct(InterventionKinds.CredentialSupplied, "user-1", "ada@example.test", "Alpha"),
            person.Supplied(TenantActions.MemberSecretSet, "Alpha"));
        Assert.NotNull(person.Supplied(TenantActions.SecretReplaced, null));
        Assert.Null(person.Supplied(TenantActions.MemberSecretCleared, "Alpha"));
        Assert.Null(person.Supplied(TenantActions.SecretCleared, null));
        Assert.Null(new CredentialActor(null, "operator").Supplied(TenantActions.SecretSet, null));

        var connector = new ConnectionActor("user-1", "ada@example.test");
        Assert.Equal(InterventionKinds.CredentialSupplied, connector.Row(TenantActions.ConnectionConnected, "c", "C", new { }).Intervention?.Kind);
        Assert.Null(connector.Row(TenantActions.ConnectionRenamed, "c", "C", new { }).Intervention);
        Assert.Null(ConnectionActor.Operator.Row(TenantActions.ConnectionConnected, "c", "C", new { }).Intervention);
        Assert.Null(ConnectionActor.ForMember(new ContainerId("Alpha", "Worker")).Row(TenantActions.ConnectionConnected, "c", "C", new { }).Intervention);
    }
}
