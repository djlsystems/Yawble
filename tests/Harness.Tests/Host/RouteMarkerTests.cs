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
    public void Only_the_merge_and_archive_routes_open_a_door_to_the_Concierge_each_for_its_own_permit()
    {
        var doors = host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Where(e => e.Metadata.GetMetadata<HumansOrConciergeMarker>() is not null)
            .ToDictionary(Describe, e => e.Metadata.GetMetadata<HumansOrConciergeMarker>()!.Permit);

        Assert.Equal(
            new Dictionary<string, string>
            {
                ["POST /api/teams/{team}/archive"] = Harness.Contracts.Permits.Archive,
                ["POST /api/teams/{team}/repos/{repo}/bring-current-and-merge"] = Harness.Contracts.Permits.Merge,
                ["POST /api/teams/{team}/repos/{repo}/merge-to-main"] = Harness.Contracts.Permits.Merge,
                ["POST /api/teams/{team}/unarchive"] = Harness.Contracts.Permits.Archive,
            },
            doors);
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
