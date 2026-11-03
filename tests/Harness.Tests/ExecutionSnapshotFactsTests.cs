using Harness.Contracts;
using Harness.Host;
using Harness.Messaging;
using Microsoft.Extensions.Configuration;

namespace Harness.Tests;

/// <summary>
/// THE SNAPSHOT READS THE SETTINGS THE SCREENS READ. <see cref="ExecutionSnapshotFacts"/> lives below
/// the Host, so it names the tenant's rate and currency rows itself; these hold those names and the
/// currency's default to <see cref="TenantSettings"/>' own, and pin the rate rule: an agent at the
/// tenant rate, nothing priced at zero, no plugin at the agent rate.
/// </summary>
public sealed class ExecutionSnapshotFactsTests
{
    [Fact]
    public void The_snapshot_reads_the_tenant_settings_rows_the_screens_price_from()
    {
        Assert.Equal(TenantSettings.OutcomesAgentHourlyRateName, ExecutionSnapshotFacts.AgentHourlyRateSetting);
        Assert.Equal(TenantSettings.OutcomesCurrencyName, ExecutionSnapshotFacts.CurrencySetting);

        var settings = new TenantSettings(null!, new ConfigurationBuilder().Build(), 4, 0);
        var currency = Assert.Single(settings.Definitions, d => d.Name == TenantSettings.OutcomesCurrencyName);
        Assert.Equal(ExecutionSnapshotFacts.DefaultCurrency, currency.BuiltInDefault);
    }

    [Theory]
    [InlineData(MemberRef.AgentKind, "60", "eur", "60", "EUR", ExecutionSnapshotFacts.RateSourceTenant)]
    [InlineData(MemberRef.AgentKind, "60", null, "60", "USD", ExecutionSnapshotFacts.RateSourceTenant)]
    [InlineData(MemberRef.AgentKind, "62.50", "usd", "62.5", "USD", ExecutionSnapshotFacts.RateSourceTenant)]
    [InlineData(MemberRef.AgentKind, "0", "usd", null, null, ExecutionSnapshotFacts.RateSourceNone)]
    [InlineData(MemberRef.AgentKind, null, "usd", null, null, ExecutionSnapshotFacts.RateSourceNone)]
    [InlineData(MemberRef.AgentKind, "not a number", "usd", null, null, ExecutionSnapshotFacts.RateSourceNone)]
    [InlineData(MemberRef.PluginKind, "60", "usd", null, null, ExecutionSnapshotFacts.RateSourceNone)]
    [InlineData(null, "60", "usd", null, null, ExecutionSnapshotFacts.RateSourceNone)]
    public void An_agent_takes_the_tenant_rate_and_nothing_else_is_priced(
        string? kind, string? rate, string? currency, string? amount, string? expectedCurrency, string source)
    {
        Assert.Equal(new ExecutionSnapshotFacts.Rate(amount, expectedCurrency, source), ExecutionSnapshotFacts.RateOf(kind, rate, currency));
    }
}
