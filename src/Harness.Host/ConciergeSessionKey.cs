namespace Harness.Host;

/// <summary>
/// Whose Concierge session this is.
///
/// Tenant-wide by design: one running Concierge per signed-in user, regardless of which
/// team surface they attached from. The team is launch-time context and lives outside the identity.
///
/// Ordinal throughout. User ids are opaque generated values, and folding case would invent an
/// equivalence nobody defined.
/// </summary>
public readonly record struct ConciergeSessionKey(string User)
{
    public bool Equals(ConciergeSessionKey other) =>
        string.Equals(User, other.User, StringComparison.Ordinal);

    public override int GetHashCode() => User.GetHashCode(StringComparison.Ordinal);

    /// <summary>For logs and refusals. Not an identifier anything parses back.</summary>
    public override string ToString() => User;
}
