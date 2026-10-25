using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.DataProtection;

namespace Harness.Tests.Contracts;

/// <summary>
/// The shape of the Host stores' contracts (the Monitor's rows, a plugin member's settings and secrets,
/// the plugin keys, a removal's row, the connections, and the database's health and housekeeping): an
/// <see cref="IdentityContract"/>, whose database also gives the Host's rows, with what several of them
/// need - a team and a member for the rows that reference one, one key ring per test, and a person.
/// </summary>
public abstract class HostStoreContract : IdentityContract
{
    protected const string Email = "person@example.test";

    /// <summary>This test's key ring: every store a test builds seals and opens with the same keys, as
    /// one instance's stores do.</summary>
    protected IDataProtectionProvider Protection { get; } = new EphemeralDataProtectionProvider();

    protected static CredentialActor Person { get; } = new(null, Email);

    /// <summary>A team and a member of it, for the rows that reference <c>team_members</c>.</summary>
    protected static async Task HireAsync(IdentityDatabase database, string team, params string[] members)
    {
        var teams = database.Teams();
        await teams.SaveTeamAsync(new PersistedTeam(team, null), Ct);
        foreach (var member in members)
        {
            await teams.SaveMemberAsync(new PersistedMember(team, member, null, "plugin:fixture", null, [], [], 0, null), Ct);
        }
    }

    /// <summary>Every team named, from the beginning of time: a read's scope that hides nothing.</summary>
    protected static Dictionary<string, DateTimeOffset> Everyone(params string[] teams) =>
        teams.ToDictionary(team => team, _ => DateTimeOffset.MinValue, StringComparer.OrdinalIgnoreCase);
}
