using System.Text.Json;
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
/// A PLUGIN MEMBER'S TABLE ROWS, as the table setting type keeps them: the row is read from the member's
/// stored settings and its session kept as the row's secret cell in <see cref="PluginMemberSecretStore"/>,
/// the member's own secret store. Nothing else stores a value.
/// </summary>
/// <remarks>
/// A row is a sign-in entry when the table declares the <c>sign-in</c> row action and the row holds its
/// <c>when</c> value (<c>method</c> = <c>interactive</c>); is not switched off in a bool <see cref="Active"/>
/// column; has an http(s) <see cref="SiteUrl"/>; and the table has a masked, read-only secret column to
/// keep the session in (<see cref="Session"/>, or the table's only such column). Extra hosts are a
/// <see cref="HostsColumn"/> cell, separated by commas or spaces.
/// </remarks>
public sealed class PluginSignInRows(
    TeamRegistry teams, PluginCatalog plugins, IPluginMemberSettingsStore settings, PluginMemberSecretStore secrets) : ISignInRows
{
    public const string SiteUrl = "siteUrl";
    public const string HostsColumn = "hosts";
    public const string Active = "active";
    public const string Session = "session";

    /// <summary>The longest storage state kept: Playwright's state for a few hosts is tens of kilobytes.</summary>
    public const int MaxSessionLength = 1024 * 1024;

    public async Task<SignInEntry?> EntryAsync(ContainerId member, string field, string row, CancellationToken ct = default)
    {
        if (await ManifestAsync(member, ct) is not { } found) return null;
        return Entry(found.Manifest, await settings.ForAsync(member, ct), field, row);
    }

    public async Task SaveSessionAsync(
        ContainerId member, string field, string row, string column, SignInStorageState state, CredentialActor actor, CancellationToken ct = default)
    {
        var found = await ManifestAsync(member, ct) ?? throw new InvalidOperationException("The member is no longer a plugin member with its plugin installed.");

        // ONE WRITER OF THE MEMBER'S TABLES AT A TIME: a settings save that removes the row cannot pass this one.
        await using var write = await PluginTableWrites.LockAsync(member, ct);
        var stored = await settings.ForAsync(member, ct);
        if (Entry(found.Manifest, stored, field, row) is not { } entry || entry.SessionColumn != column)
        {
            throw new InvalidOperationException("The row is no longer an active interactive entry.");
        }

        if (PluginMemberSecretStore.CellRefusal(found.Manifest, stored, field, row, column, state.Json, byPerson: false, maxLength: MaxSessionLength) is { } refusal)
        {
            throw new InvalidOperationException(refusal);
        }

        await secrets.SetCellAsync(member, found.Label, found.Manifest.Id, field, row, column, state.Json, actor, ct);
    }

    /// <summary>The sign-in entry <paramref name="row"/> of <paramref name="field"/> in <paramref name="stored"/>, or null.</summary>
    public static SignInEntry? Entry(PluginManifest manifest, PluginMemberSettings stored, string field, string row)
    {
        if (!manifest.Config.TryGetValue(field, out var declared) || declared.Table is not { } table) return null;
        if (table.RowActions.FirstOrDefault(a => a.Action == "sign-in") is not { } action) return null;
        if (table.Row(stored.Config.TryGetValue(field, out var rows) ? rows : null, row) is not { } found) return null;

        if (Text(found, action.Column) != action.EqualTo) return null;

        if (table.Column(Active) is { Type: "bool" } active)
        {
            var on = found.TryGetProperty(Active, out var cell) ? cell : active.Default;
            if (on is { ValueKind: JsonValueKind.False }) return null;
        }

        if (!Uri.TryCreate(Text(found, SiteUrl), UriKind.Absolute, out var site) || site.Scheme is not ("https" or "http")) return null;

        var keep = table.SecretColumns.Where(c => c.ReadOnly && c.Masked).ToList();
        var session = keep.FirstOrDefault(c => c.Name == Session) ?? (keep.Count == 1 ? keep[0] : null);
        if (session is null) return null;

        return new SignInEntry(site.ToString(), Hosts(Text(found, HostsColumn)), session.Name);
    }

    /// <summary>Each host of a hosts cell, by name or as a URL's host, lower case, once each; nothing else.</summary>
    public static IReadOnlyList<string> Hosts(string? cell) =>
        string.IsNullOrWhiteSpace(cell)
            ? []
            : [.. cell.Split([',', ' ', '\t', ';'], StringSplitOptions.RemoveEmptyEntries)
                .Select(h => Uri.TryCreate(h, UriKind.Absolute, out var url) && url.Scheme is "https" or "http" ? url.Host : h)
                .Where(h => Uri.CheckHostName(h) is UriHostNameType.Dns or UriHostNameType.IPv4)
                .Select(h => h.ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)];

    private static string? Text(JsonElement row, string column) =>
        row.TryGetProperty(column, out var cell) && cell.ValueKind == JsonValueKind.String ? cell.GetString() : null;

    private async Task<(PluginManifest Manifest, string Label)?> ManifestAsync(ContainerId member, CancellationToken ct)
    {
        PersistedMember found;
        try
        {
            found = await teams.MemberAsync(member.Team, member.Name, ct);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        return MemberRef.IsPlugin(found.Agent, out var id) && plugins.For(id!) is { } plugin
            ? (plugin.Manifest, found.Label ?? found.Name)
            : null;
    }
}
