using Harness.Host.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// AGENTS.md, Authorization and <see cref="PermitMarkers"/>: every endpoint says exactly one of
/// RequirePermit, NoPermitRequired or HumansOnly. A route with none is ungated for machine
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
                + e.Metadata.GetOrderedMetadata<HumansOnlyMarker>().Count))
            .Where(e => e.Count != 1)
            .Select(e => $"{e.Name}: {e.Count} markers")
            .ToList();

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
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
