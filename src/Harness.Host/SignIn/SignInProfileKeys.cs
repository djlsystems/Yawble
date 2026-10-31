using System.Security.Cryptography;
using System.Text;
using Harness.Contracts;
using Microsoft.Extensions.Logging;

namespace Harness.Host;

/// <summary>
/// THE NAME OF A ROW'S SIGN-IN BROWSER PROFILE ON A WORKER: <c>&lt;member key&gt;/&lt;row key&gt;</c>, each the
/// first 32 hex digits of a SHA-256, so the volume names no member, field or row. The member's key is
/// of its team and name, the row's of the field and the row's name (case folded, as the table's
/// secret cells are), so the same row always finds the same profile.
/// </summary>
public static class SignInProfileKey
{
    public static string Member(ContainerId member) =>
        Hash("member\n" + member.Team.ToLowerInvariant() + "\n" + member.Name.ToLowerInvariant());

    public static string Row(ContainerId member, string field, string row) =>
        Member(member) + "/" + Hash("row\n" + field + "\n" + row.ToLowerInvariant());

    /// <summary>
    /// A WEBSITE CONNECTION'S PROFILE (CON 6): <c>&lt;connections key&gt;/&lt;connection key&gt;</c>, of the
    /// connection's id, so the same connection - whatever it is renamed to - always finds the one profile
    /// its Sign in and its runs share, and the volume names no connection.
    /// </summary>
    public static string Connection(string connectionId) => Hash("connections") + "/" + Hash("connection\n" + connectionId);

    private static string Hash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
}

/// <summary>
/// REMOVES A ROW'S, A MEMBER'S OR A WEBSITE CONNECTION'S SIGN-IN PROFILES with the row, the member or the connection: every connected worker
/// is told (<see cref="DeleteSignInProfiles"/>), as the profiles are on the volume they share. Best
/// effort: a worker that is gone has nothing open, and a failure never stops the deletion it follows.
/// </summary>
public sealed class SignInProfileCleanup(Func<IReadOnlyList<IRunWorker>> workers, ILogger<SignInProfileCleanup>? log = null)
{
    public Task ForgetRowAsync(ContainerId member, string field, string row, CancellationToken ct = default) =>
        TellAsync(SignInProfileKey.Row(member, field, row), ct);

    public Task ForgetMemberAsync(ContainerId member, CancellationToken ct = default) =>
        TellAsync(SignInProfileKey.Member(member), ct);

    /// <summary>A website connection's profile, removed with the connection (CON 6).</summary>
    public Task ForgetConnectionAsync(string connectionId, CancellationToken ct = default) =>
        TellAsync(SignInProfileKey.Connection(connectionId), ct);

    private async Task TellAsync(string key, CancellationToken ct)
    {
        IReadOnlyList<IRunWorker> connected;
        try
        {
            connected = workers();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        foreach (var worker in connected)
        {
            try
            {
                await worker.SendAsync(new DeleteSignInProfiles(key), ct);
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException)
            {
                log?.LogInformation("Worker {Worker} was not told to remove a sign-in profile: {Message}", worker.Id, exception.Message);
            }
        }
    }
}
