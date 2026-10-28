using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// The row a Sign in link names, as the sign-in page needs it: the entry's Site URL, which the
/// browser opens first, the entry's other hosts, and the secret column its session is kept in.
/// </summary>
public sealed record SignInEntry(string SiteUrl, IReadOnlyList<string> Hosts, string SessionColumn);

/// <summary>
/// THE SEAM BETWEEN THE SIGN-IN PAGE AND A PLUGIN MEMBER'S TABLE ROWS. The table setting type, its
/// row-scoped secret store and its screen are the table's own; the sign-in page only reads one row and
/// keeps one secret cell, through this. Nothing here holds a value: a secret cell is written once, by
/// <see cref="SaveSessionAsync"/>, and never read back.
/// </summary>
public interface ISignInRows
{
    /// <summary>
    /// The interactive entry <paramref name="row"/> of <paramref name="member"/>'s table field
    /// <paramref name="field"/>, or null when there is no such row, the field is not a table, the row
    /// is inactive, or its method is not <c>interactive</c>.
    /// </summary>
    Task<SignInEntry?> EntryAsync(ContainerId member, string field, string row, CancellationToken ct = default);

    /// <summary>
    /// Keeps <paramref name="state"/> as the masked secret cell <paramref name="column"/> of
    /// <paramref name="row"/> in <paramref name="member"/>'s table field <paramref name="field"/>,
    /// replacing what was there, recorded as set by <paramref name="actor"/>. Throws when it cannot.
    /// </summary>
    Task SaveSessionAsync(
        ContainerId member, string field, string row, string column, SignInStorageState state, CredentialActor actor, CancellationToken ct = default);
}

/// <summary>
/// No table rows yet: every row reads as absent, so every Sign in link answers that the row has no
/// sign-in, and nothing is ever kept. Replaced by the table setting type's own rows when it is wired.
/// </summary>
public sealed class NoSignInRows : ISignInRows
{
    public Task<SignInEntry?> EntryAsync(ContainerId member, string field, string row, CancellationToken ct = default) =>
        Task.FromResult<SignInEntry?>(null);

    public Task SaveSessionAsync(
        ContainerId member, string field, string row, string column, SignInStorageState state, CredentialActor actor, CancellationToken ct = default) =>
        throw new InvalidOperationException("This platform has no table rows to keep a session in.");
}
