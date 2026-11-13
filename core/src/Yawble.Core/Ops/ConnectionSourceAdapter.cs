using Yawble.Core.Connections;

namespace Yawble.Core.Ops;

/// <summary>The doctor's view of the Connections: <see cref="ConnectionService.Index"/>, which reads no secret.</summary>
public sealed class ConnectionSourceAdapter(ConnectionService connections) : IConnectionSource
{
    public IReadOnlyList<ConnectionInfo> List() =>
        connections.Index().Select(c => new ConnectionInfo(c.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), c.Name, c.Kind, c.Status, c.StoreKey)).ToList();
}
