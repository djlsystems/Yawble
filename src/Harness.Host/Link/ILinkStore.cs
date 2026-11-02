namespace Harness.Host.Link;

/// <summary>Where the link to a management service stands.</summary>
public enum LinkState
{
    Connecting,
    Connected,
    Retrying,
    Refused,
}

/// <summary>
/// The instance's one link to a management service, as stored. <see cref="CredentialCipher"/> is the
/// credential sealed by <see cref="LinkCredential.Protect"/>; the plain credential is never stored, logged
/// or answered.
/// </summary>
public sealed record StoredLink(
    string ServiceUrl,
    string ServiceName,
    string InstanceName,
    string CredentialCipher,
    DateTimeOffset AttachedAt,
    LinkState State,
    DateTimeOffset StateSince,
    string? Reason,
    DateTimeOffset? LastReportAt);

/// <summary>
/// THE STORED LINK: one row or none (<c>management_link</c>, schema step <c>link-001</c>). An instance that
/// was never attached has no row, and nothing reads as linked.
/// </summary>
public interface ILinkStore
{
    /// <summary>The link, or null when the instance is not attached.</summary>
    Task<StoredLink?> GetAsync(CancellationToken ct = default);

    /// <summary>Replaces the one link, whole.</summary>
    Task SaveAsync(StoredLink link, CancellationToken ct = default);

    /// <summary>Moves the link to <paramref name="state"/> since <paramref name="at"/>, with <paramref name="reason"/>
    /// (null clears the last one). Nothing when there is no link.</summary>
    Task MarkAsync(LinkState state, DateTimeOffset at, string? reason, CancellationToken ct = default);

    /// <summary>Records when the service last took a status report. Nothing when there is no link.</summary>
    Task ReportedAsync(DateTimeOffset at, CancellationToken ct = default);

    /// <summary>Forgets the link and its credential.</summary>
    Task ClearAsync(CancellationToken ct = default);
}
