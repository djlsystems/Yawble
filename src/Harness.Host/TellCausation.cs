using System.Globalization;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// Which message a `tell` answers when the caller named none.
///
/// A TEAM CONTAINER DISPATCHES INSIDE THE RUN IT IS IN. If a container's missing causation rooted a
/// new workflow, a Manager's dispatch would run under a workflow nobody owned, the real one would
/// sit blocked waiting for a handback that went elsewhere, and the new one could only be declared
/// by the member. So a container's missing causation is its current run's. A person or a Concierge still roots a new workflow by omitting
/// it, which is how work starts.
/// </summary>
public static class TellCausation
{
    public static string? Resolve(string? requested, PrincipalKind? caller, long? callerCurrentCausation) =>
        !string.IsNullOrWhiteSpace(requested) || caller is not PrincipalKind.Container
            ? requested
            : callerCurrentCausation?.ToString(CultureInfo.InvariantCulture);
}
