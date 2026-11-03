using Harness.Contracts;
using Harness.Messaging;

namespace Harness.Tests;

/// <summary>
/// WHAT A MEMBER'S RUN IS PRICED AT (<see cref="EffectiveCost"/>), the one function both snapshot
/// writers and the member read use: a member's own positive rate, agent or plugin, is <c>member</c>; an
/// own "0" is not priced and does not fall through; an agent with none takes the tenant rate or none; a
/// plugin with none is never priced at the agent rate; nothing is ever a priced zero.
/// </summary>
public sealed class EffectiveCostTests
{
    private const string Account = "3f2b8c1e-9a4d-4e2f-b6a1-0c5d7e8f9a10";

    [Theory]
    // A member's own positive rate, agent or plugin, in the tenant currency, whatever the tenant rate.
    [InlineData("claude", "62.5", "60", "eur", "62.5", "EUR", ExecutionSnapshotFacts.RateSourceMember)]
    [InlineData("claude", "62.50", null, null, "62.5", "USD", ExecutionSnapshotFacts.RateSourceMember)]
    [InlineData("plugin:fixture", "15", "60", "usd", "15", "USD", ExecutionSnapshotFacts.RateSourceMember)]
    // An own zero is not priced, and does not fall through to the tenant rate.
    [InlineData("claude", "0", "60", "usd", null, null, ExecutionSnapshotFacts.RateSourceNone)]
    [InlineData("plugin:fixture", "0", "60", "usd", null, null, ExecutionSnapshotFacts.RateSourceNone)]
    // Unreadable or negative own text (the PATCH never stores it) is not priced either.
    [InlineData("claude", "lots", "60", "usd", null, null, ExecutionSnapshotFacts.RateSourceNone)]
    [InlineData("claude", "-5", "60", "usd", null, null, ExecutionSnapshotFacts.RateSourceNone)]
    // An agent with none takes the tenant default, or none.
    [InlineData("claude", null, "60", "eur", "60", "EUR", ExecutionSnapshotFacts.RateSourceTenant)]
    [InlineData("claude", null, null, "eur", null, null, ExecutionSnapshotFacts.RateSourceNone)]
    [InlineData("claude", null, "0", "eur", null, null, ExecutionSnapshotFacts.RateSourceNone)]
    [InlineData("claude", null, "sixty", "eur", null, null, ExecutionSnapshotFacts.RateSourceNone)]
    // A plugin with none is never priced at the agent rate.
    [InlineData("plugin:fixture", null, "60", "usd", null, null, ExecutionSnapshotFacts.RateSourceNone)]
    public void A_members_effective_rate(
        string agent, string? own, string? tenantRate, string? currency,
        string? amount, string? expectedCurrency, string source)
    {
        var cost = EffectiveCost.Of(new EffectiveCost.Member(agent, own, Account), tenantRate, currency);

        Assert.Equal(new EffectiveCost(amount, expectedCurrency, source, Account, agent, MemberRef.KindOf(agent)), cost);
        Assert.NotEqual("0", cost.Amount);
    }

    [Fact]
    public void A_member_off_the_roster_has_no_kind_no_preset_and_no_price()
    {
        Assert.Equal(
            new EffectiveCost(null, null, ExecutionSnapshotFacts.RateSourceNone, null, null, null),
            EffectiveCost.Of(null, "60", "usd"));
    }

    [Fact]
    public void A_blank_account_is_no_account()
    {
        Assert.Null(EffectiveCost.Of(new EffectiveCost.Member("claude", null, " "), "60", "usd").BillingAccountId);
    }
}
