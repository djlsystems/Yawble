using Harness.Host.Link;

namespace Harness.Tests.Contracts;

/// <summary>
/// <see cref="ILinkStore"/>, as each provider must keep it: one row or none, a save replacing it whole,
/// a mark moving its state and saying since when and why, a report time, a clear that forgets the row
/// and its credential, and the credential only ever as Data Protection ciphertext.
/// </summary>
public abstract class LinkStoreContract : HostStoreContract
{
    private static readonly DateTimeOffset At = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    private const string Plain = "link-credential-plain-7f3a";

    private async Task<ILinkStore> StoreAsync() => (await DatabaseAsync()).Links();

    private StoredLink Link(string credential = Plain, string instance = "Workshop") =>
        new("https://manage.example.com", "Example management", instance, LinkCredential.Protect(Protection, credential),
            At, LinkState.Connecting, At, null, null);

    [Fact]
    public async Task A_fresh_store_has_no_link()
    {
        var store = await StoreAsync();

        Assert.Null(await store.GetAsync(Ct));
    }

    [Fact]
    public async Task Save_replaces_the_one_link()
    {
        var database = await DatabaseAsync();
        var store = await StoreAsync();
        var first = Link();
        var second = Link("second-credential", "Lab") with
        {
            ServiceUrl = "https://manage.example.com/other",
            AttachedAt = At.AddHours(1),
            State = LinkState.Connected,
            StateSince = At.AddHours(1),
            LastReportAt = At.AddHours(2),
        };

        await store.SaveAsync(first, Ct);
        Assert.Equal(first, await store.GetAsync(Ct));

        await store.SaveAsync(second, Ct);
        Assert.Equal(second, await store.GetAsync(Ct));
        Assert.Equal("second-credential", LinkCredential.Unprotect(Protection, (await store.GetAsync(Ct))!.CredentialCipher));
        Assert.Equal(1, await database.LongAsync("SELECT COUNT(*) FROM management_link"));
    }

    [Fact]
    public async Task Mark_records_state_since_and_reason()
    {
        var store = await StoreAsync();
        var saved = Link();
        await store.SaveAsync(saved, Ct);

        await store.MarkAsync(LinkState.Retrying, At.AddMinutes(5), "could not reach https://manage.example.com", Ct);
        Assert.Equal(
            saved with { State = LinkState.Retrying, StateSince = At.AddMinutes(5), Reason = "could not reach https://manage.example.com" },
            await store.GetAsync(Ct));

        // A mark with no reason clears the last one; a report moves only the report time.
        await store.MarkAsync(LinkState.Connected, At.AddMinutes(6), null, Ct);
        await store.ReportedAsync(At.AddMinutes(7), Ct);
        Assert.Equal(
            saved with { State = LinkState.Connected, StateSince = At.AddMinutes(6), Reason = null, LastReportAt = At.AddMinutes(7) },
            await store.GetAsync(Ct));
    }

    [Fact]
    public async Task Clear_forgets_the_link_and_its_credential()
    {
        var database = await DatabaseAsync();
        var store = await StoreAsync();
        await store.SaveAsync(Link(), Ct);

        await store.ClearAsync(Ct);

        Assert.Null(await store.GetAsync(Ct));
        Assert.Equal(0, await database.LongAsync("SELECT COUNT(*) FROM management_link"));

        // A mark or a report with no link writes nothing.
        await store.MarkAsync(LinkState.Refused, At, "refused", Ct);
        await store.ReportedAsync(At, Ct);
        Assert.Null(await store.GetAsync(Ct));
    }

    [Fact]
    public async Task The_credential_is_never_stored_in_plain()
    {
        var database = await DatabaseAsync();
        var store = await StoreAsync();
        await store.SaveAsync(Link(), Ct);

        var rows = await database.RowsAsync("SELECT * FROM management_link");

        Assert.Single(rows);
        Assert.All(rows[0], value => Assert.DoesNotContain(Plain, value as string ?? "", StringComparison.Ordinal));
        Assert.Equal(Plain, LinkCredential.Unprotect(Protection, (await store.GetAsync(Ct))!.CredentialCipher));
    }
}

public sealed class SqliteLinkStoreTests : LinkStoreContract
{
    protected override Task<IdentityDatabase> OpenAsync() => Task.FromResult<IdentityDatabase>(new SqliteIdentityDatabase());
}

public sealed class SqlServerLinkStoreTests : LinkStoreContract
{
    protected override async Task<IdentityDatabase> OpenAsync() => await SqlServerIdentityDatabase.CreateAsync();
}
