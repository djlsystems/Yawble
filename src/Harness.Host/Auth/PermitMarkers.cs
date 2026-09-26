namespace Harness.Host.Auth;

/// <summary>This endpoint requires <paramref name="Permit"/> of a machine principal.</summary>
public sealed record PermitRequirement(string Permit);

/// <summary>A machine principal may call this endpoint holding nothing at all.</summary>
public sealed record PermitExemption;

/// <summary>No machine principal may call this endpoint, whatever it holds.</summary>
public sealed record HumansOnlyMarker;

/// <summary>
/// The three things a route can say about who may call it, and it must say exactly one -
/// <c>RouteMarkerTests</c> fails the build otherwise.
///
/// THREE rather than two, and the third is not a convenience. Most of this product's route table is
/// neither "needs a permit" nor "anyone may": it is user administration, the tenant log, the
/// catalog, somebody's own password. Marking those <see cref="NoPermitRequired"/> would say the
/// opposite of what is meant and would read, to the next person, as a considered exemption rather
/// than as a wall.
///
/// A per-route declaration is exactly the shape <c>TeamGate</c>'s doc comment rejects, on the
/// grounds that "its failure mode is silence, so a new team route without the filter is ungated and
/// nothing complains". That objection is right and cannot be designed around here: there is no
/// structural key for a VERB the way <c>{team}</c> is for a team - no route shape encodes "this is
/// a Tell", and both HTTP method and tag are far too coarse. So the silence is closed from the
/// other end instead, by a test that walks <c>EndpointDataSource</c> and demands one marker per
/// endpoint. That test is the only thing that earns this shape; deleting it reinstates precisely
/// the hazard TeamGate names.
/// </summary>
public static class PermitMarkers
{
    // Generic over IEndpointConventionBuilder rather than written against RouteHandlerBuilder,
    // because not every endpoint in this host is a minimal-API route: MapHub contributes two,
    // /hub/containers and its negotiate.
    //
    // KEEP IT GENERIC, however few such endpoints there are. A marker set that could not reach
    // the non-route endpoints would leave the closed-set test unsatisfiable, and the pressure would
    // be to exempt them from the test - which is exactly how a gate acquires a hole.
    public static TBuilder RequirePermit<TBuilder>(this TBuilder builder, string permit)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpoint => endpoint.Metadata.Add(new PermitRequirement(permit)));
        return builder;
    }

    public static TBuilder NoPermitRequired<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpoint => endpoint.Metadata.Add(new PermitExemption()));
        return builder;
    }

    public static TBuilder HumansOnly<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpoint => endpoint.Metadata.Add(new HumansOnlyMarker()));
        return builder;
    }

    // DECLARE ON ROUTES, NOT ON GROUPS - and note that nothing here enforces that, on purpose.
    //
    // A group default with per-route overrides looks obvious for /api/teams/{team}/documents, whose
    // two reads and three writes differ. It does not work, because the three markers are different
    // TYPES: a group saying RequirePermit(Read) and a route saying HumansOnly leave BOTH readable,
    // and "last wins" only disambiguates within one type. Resolving that needs a precedence rule,
    // and any fixed precedence is wrong in one direction - HumansOnly-beats-Requirement is right
    // for a read group with write overrides and wrong for a humans-only group with one readable
    // route in it.
    //
    // Describe.Documents reached the same conclusion about descriptions on this exact group, for a
    // neighbouring reason: a route's own WithDescription REPLACES the group's, so three documents
    // routes would have silently dropped half their explanation.
    //
    // The type system is not what stops it. RouteMarkerTests is: a group marker plus a route marker
    // puts TWO on the endpoint, and "no endpoint says two contradictory things" fails. So the
    // mistake is available to make and impossible to ship, which is better than an overload that
    // does not exist and a reason nobody can find.
}
