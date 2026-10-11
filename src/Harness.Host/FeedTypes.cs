using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// THE TYPES THE ACTIVITY FEED CAN RENDER — now a PROJECTION of <see cref="EventCatalog"/> rather
/// than a hand-written set.
///
/// <para>
/// The closed set itself lives a layer up, in the catalog and `EventCatalogCoverageTests`, which
/// asks every type for a publisher and a summary as well - so a type the feed silently cannot render
/// fails a test rather than shipping.
/// </para>
///
/// <para>
/// KEPT AS A NAMED PROPERTY rather than inlining `EventCatalog.Types` at the call site, because a
/// set built inline in a lambda is a set no test can see.
/// </para>
/// </summary>
public static class FeedTypes
{
    public static IReadOnlySet<string> Explicit => EventCatalog.Types;
}
