using Harness.Host.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// AGENTS.md, Authorization and <see cref="PermitMarkers"/>: every endpoint says exactly one of
/// RequirePermit, NoPermitRequired, HumansOnly or HumansOrConcierge. A route with none is ungated for machine
/// principals and nothing complains; a route with two says contradictory things. This walk of the
/// real host's <see cref="EndpointDataSource"/> is the only thing that closes that silence.
/// </summary>
public sealed class RouteMarkerTests(HostFixture host) : IClassFixture<HostFixture>
{
    [Fact]
    public void Every_endpoint_carries_exactly_one_marker()
    {
        var endpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        Assert.NotEmpty(endpoints);

        var wrong = endpoints
            .Select(e => (Name: Describe(e), Count:
                e.Metadata.GetOrderedMetadata<PermitRequirement>().Count
                + e.Metadata.GetOrderedMetadata<PermitExemption>().Count
                + e.Metadata.GetOrderedMetadata<HumansOnlyMarker>().Count
                + e.Metadata.GetOrderedMetadata<HumansOrConciergeMarker>().Count))
            .Where(e => e.Count != 1)
            .Select(e => $"{e.Name}: {e.Count} markers")
            .ToList();

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    [Fact]
    public void Only_the_merge_archive_and_preview_env_routes_open_a_door_to_the_Concierge_each_for_its_own_permit()
    {
        var doors = host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Where(e => e.Metadata.GetMetadata<HumansOrConciergeMarker>() is not null)
            .ToDictionary(Describe, e => e.Metadata.GetMetadata<HumansOrConciergeMarker>()!.Permit);

        Assert.Equal(
            new Dictionary<string, string>
            {
                ["POST /api/teams/{team}/archive"] = Harness.Contracts.Permits.Archive,
                ["POST /api/teams/{team}/preview-env"] = Harness.Contracts.Permits.Preview,
                ["POST /api/teams/{team}/repos/{repo}/bring-current-and-merge"] = Harness.Contracts.Permits.Merge,
                ["POST /api/teams/{team}/repos/{repo}/merge-to-main"] = Harness.Contracts.Permits.Merge,
                ["POST /api/teams/{team}/unarchive"] = Harness.Contracts.Permits.Archive,
            },
            doors);
    }

    /// <summary>
    /// THE SPEND PERMIT OPENS READS ONLY: exactly the Spend page's reads require it - a person passes as
    /// before - and every billing account and expense write stays HumansOnly.
    /// </summary>
    [Fact]
    public void Exactly_the_Actual_Spend_reads_require_the_Spend_permit_and_every_spend_write_stays_humans_only()
    {
        var endpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        var spend = endpoints
            .Where(e => e.Metadata.GetMetadata<PermitRequirement>()?.Permit == Harness.Contracts.Permits.Spend)
            .Select(Describe)
            .Order(StringComparer.Ordinal);

        Assert.Equal(
            [
                "GET /api/billing-accounts",
                "GET /api/billing-accounts/{id}",
                "GET /api/expenses",
                "GET /api/expenses/{id}",
                "GET /api/spend/export/billing-accounts",
                "GET /api/spend/export/estimated-execution-cost",
                "GET /api/spend/export/expenses",
                "GET /api/spend/export/period-summary",
                "GET /api/spend/linked-members",
                "GET /api/spend/overview",
            ],
            spend);

        var writes = endpoints
            .Where(e => Describe(e) is var name && !name.StartsWith("GET ", StringComparison.Ordinal)
                && (name.Contains(" /api/billing-accounts", StringComparison.Ordinal) || name.Contains(" /api/expenses", StringComparison.Ordinal)
                    || name.Contains(" /api/spend", StringComparison.Ordinal) || name == "PUT /api/tenant/settings"))
            .ToList();

        Assert.Equal(8, writes.Count);
        Assert.All(writes, e => Assert.NotNull(e.Metadata.GetMetadata<HumansOnlyMarker>()));
    }

    private static string Describe(Endpoint endpoint)
    {
        var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
        var verb = methods is { Count: > 0 } ? string.Join(",", methods) : "*";

        return endpoint is RouteEndpoint route
            ? $"{verb} {route.RoutePattern.RawText}"
            : $"{verb} {endpoint.DisplayName}";
    }
}
