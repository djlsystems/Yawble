using System.Data.Common;
using Harness.Contracts;

namespace Harness.Tests.Contracts;

/// <summary>
/// <see cref="IUserStore"/>, as each provider must keep it. The appearance round trip is
/// <c>FreshVolumeTests.The_appearance_step_applies_to_an_empty_and_an_existing_volume</c>'s, with its
/// values; the current team surviving a rotation is <c>UserCurrentTeamTests</c>'; the rest are the
/// interface's own remarks (one answer for an unknown email and a wrong password, a taken address is
/// an answer and not a fault) and the review's case and unicode rules.
/// </summary>
public abstract class UserStoreContract : IdentityContract
{
    private const string Password = "correct horse battery";

    protected async Task<IUserStore> CreateAsync() => (await DatabaseAsync()).Users();

    [Fact]
    public async Task A_created_user_is_found_by_any_case_of_its_email_and_by_its_id()
    {
        var users = await CreateAsync();
        Assert.False(await users.AnyAsync(Ct));

        var created = await users.CreateAsync("  Alice@Example.TEST ", Password, Ct);

        Assert.True(await users.AnyAsync(Ct));
        Assert.Equal("alice@example.test", created.Email);
        Assert.Equal(created, await users.FindAsync("ALICE@example.test", Ct));
        Assert.Equal(created, await users.FindByIdAsync(created.Id, Ct));
        Assert.Null(await users.FindAsync("bob@example.test", Ct));
        Assert.Null(await users.FindByIdAsync(created.Id.ToUpperInvariant(), Ct));
    }

    [Fact]
    public async Task A_password_verifies_and_a_wrong_one_or_an_unknown_email_is_the_same_null()
    {
        var users = await CreateAsync();
        var created = await users.CreateAsync("alice@example.test", Password, Ct);

        Assert.Equal(created, await users.VerifyAsync("Alice@Example.test", Password, Ct));
        Assert.Null(await users.VerifyAsync("alice@example.test", "wrong", Ct));
        Assert.Null(await users.VerifyAsync("nobody@example.test", Password, Ct));
    }

    [Fact]
    public async Task A_changed_password_verifies_and_a_changed_email_keeps_it()
    {
        var users = await CreateAsync();
        var created = await users.CreateAsync("alice@example.test", Password, Ct);

        await users.UpdatePasswordAsync(created.Id, "a new one", Ct);
        Assert.Null(await users.VerifyAsync("alice@example.test", Password, Ct));
        Assert.Equal(created, await users.VerifyAsync("alice@example.test", "a new one", Ct));

        Assert.True(await users.TryUpdateEmailAsync(created.Id, " Alice2@Example.test", Ct));
        Assert.Equal(created with { Email = "alice2@example.test" }, await users.VerifyAsync("alice2@example.test", "a new one", Ct));
        Assert.Null(await users.FindAsync("alice@example.test", Ct));
    }

    /// <summary>
    /// The unique index decides "taken", whatever case the address was typed in: a second account by
    /// try-create is null, a move onto a taken address is false and leaves the row, and the plain
    /// create throws.
    /// </summary>
    [Fact]
    public async Task A_taken_email_in_any_case_is_refused_by_each_write()
    {
        var users = await CreateAsync();
        var alice = await users.CreateAsync("alice@example.test", Password, Ct);
        var bob = await users.CreateAsync("bob@example.test", Password, Ct);

        Assert.Null(await users.TryCreateAsync("ALICE@example.test", Password, Ct));
        Assert.False(await users.TryUpdateEmailAsync(bob.Id, "Alice@Example.Test", Ct));
        Assert.Equal(bob, await users.FindByIdAsync(bob.Id, Ct));
        await Assert.ThrowsAnyAsync<DbException>(() => users.CreateAsync("alice@EXAMPLE.test", Password, Ct));

        Assert.Equal([alice, bob], await users.ListAsync(Ct));
    }

    /// <summary>
    /// THE EMAIL COLUMN COMPARES EXACTLY, AS SQLITE'S BINARY ONE DOES: case is folded by the store
    /// before the row is written or asked for, never by the database. A row whose address is not
    /// lower-case (none the store writes) is not found by the lower-case spelling.
    /// </summary>
    [Fact]
    public async Task The_email_column_compares_exactly_and_only_the_store_folds_case()
    {
        var database = await DatabaseAsync();
        var users = database.Users();
        var created = await users.CreateAsync("mixed@example.test", Password, Ct);

        await database.ExecuteAsync("UPDATE users SET email = 'MIXED@example.test'");

        Assert.Null(await users.FindAsync("mixed@example.test", Ct));
        Assert.Equal(1, await database.LongAsync("SELECT COUNT(*) FROM users WHERE email = 'MIXED@example.test'"));
        Assert.Equal(0, await database.LongAsync("SELECT COUNT(*) FROM users WHERE email = 'mixed@example.test'"));
        Assert.Equal("MIXED@example.test", (await users.FindByIdAsync(created.Id, Ct))!.Email);
    }

    [Fact]
    public async Task Users_are_listed_by_email()
    {
        var users = await CreateAsync();
        var carol = await users.CreateAsync("carol@example.test", Password, Ct);
        var alice = await users.CreateAsync("alice@example.test", Password, Ct);
        var bob = await users.CreateAsync("bob@example.test", Password, Ct);

        Assert.Equal([alice, bob, carol], await users.ListAsync(Ct));
    }

    /// <summary>A person's keys go with the person: the cascade from <c>principals</c> to <c>users</c>.</summary>
    [Fact]
    public async Task Deleting_a_user_revokes_their_api_keys_and_nothing_else()
    {
        var database = await DatabaseAsync();
        var users = database.Users();
        var principals = database.Principals();
        var alice = await users.CreateAsync("alice@example.test", Password, Ct);
        var bob = await users.CreateAsync("bob@example.test", Password, Ct);
        var aliceKey = await principals.MintAsync("key-a", PrincipalKind.ApiKey, null, Permits.All, ownerUserId: alice.Id, label: "A", ct: Ct);
        var bobKey = await principals.MintAsync("key-b", PrincipalKind.ApiKey, null, Permits.All, ownerUserId: bob.Id, label: "B", ct: Ct);

        await users.DeleteAsync(alice.Id, Ct);

        Assert.Null(await users.FindByIdAsync(alice.Id, Ct));
        Assert.Null(await principals.ResolveAsync(aliceKey, Ct));
        Assert.NotNull(await principals.ResolveAsync(bobKey, Ct));
        Assert.Equal([bob], await users.ListAsync(Ct));
    }

    /// <summary>
    /// <c>UserCurrentTeamTests.The_current_team_survives_a_credential_rotation</c>: the current team is
    /// on the person, so re-minting their credential leaves it; null is a real state, set and cleared.
    /// </summary>
    [Fact]
    public async Task The_current_team_is_null_until_set_survives_a_credential_rotation_and_clears()
    {
        var database = await DatabaseAsync();
        var users = database.Users();
        var principals = database.Principals();
        var alice = await users.CreateAsync("alice@example.test", Password, Ct);

        Assert.Null(await users.CurrentTeamForAsync(alice.Id, Ct));
        Assert.Null(await users.CurrentTeamForAsync("nobody", Ct));

        await principals.MintAsync("console:alice", PrincipalKind.Concierge, "Alpha", Permits.All, ownerUserId: alice.Id, ct: Ct);
        await users.SetCurrentTeamAsync(alice.Id, "Alpha", Ct);
        await principals.MintAsync("console:alice", PrincipalKind.Concierge, "Alpha", Permits.All, ownerUserId: alice.Id, ct: Ct);

        Assert.Equal("Alpha", await users.CurrentTeamForAsync(alice.Id, Ct));

        await users.SetCurrentTeamAsync(alice.Id, null, Ct);
        Assert.Null(await users.CurrentTeamForAsync(alice.Id, Ct));
    }

    /// <summary><c>FreshVolumeTests.The_appearance_step_applies_to_an_empty_and_an_existing_volume</c>'s round trip, same values.</summary>
    [Fact]
    public async Task A_persons_appearance_is_null_until_chosen_and_round_trips()
    {
        var users = await CreateAsync();
        var person = await users.CreateAsync("fresh@example.test", Password, Ct);

        Assert.Null(await users.AppearanceForAsync(person.Id, Ct));
        Assert.Null(await users.AppearanceForAsync("nobody", Ct));

        await users.SetAppearanceAsync(person.Id, """{"textSize":130}""", Ct);
        Assert.Equal("""{"textSize":130}""", await users.AppearanceForAsync(person.Id, Ct));

        await users.SetAppearanceAsync(person.Id, """{"selected":"dark"}""", Ct);
        Assert.Equal("""{"selected":"dark"}""", await users.AppearanceForAsync(person.Id, Ct));
    }

    /// <summary>
    /// REVIEW FOCUS 4: a person's own text comes back byte for byte - the appearance record (the one
    /// free-text column a person has; there is no display-name column) over 200 KB with emoji, accents,
    /// CJK and a <c>'</c>, and an address holding a <c>'</c> and an emoji.
    /// </summary>
    [Fact]
    public async Task Long_and_unicode_text_round_trips()
    {
        var users = await CreateAsync();
        var appearance = LongUnicode("""{"name":"Zoë""") + "\"}";
        var person = await users.CreateAsync("o'brien+🚀@example.test", Password, Ct);

        await users.SetAppearanceAsync(person.Id, appearance, Ct);

        Assert.True(appearance.Length > 200_000);
        Assert.Equal(appearance, await users.AppearanceForAsync(person.Id, Ct));
        Assert.Equal("o'brien+🚀@example.test", (await users.FindAsync("O'Brien+🚀@example.test", Ct))!.Email);
        Assert.Equal(person, await users.VerifyAsync("o'brien+🚀@example.test", Password, Ct));
    }

    /// <summary>
    /// CREDENTIALS ARE STORED AS TODAY: the password hasher's own text (version 3, its iteration count
    /// inside it), never the password.
    /// </summary>
    [Fact]
    public async Task The_stored_hash_is_the_password_hashers_text_and_never_the_password()
    {
        var database = await DatabaseAsync();
        var person = await database.Users(hashIterations: 1000).CreateAsync("alice@example.test", Password, Ct);

        var stored = (await database.TextAsync($"SELECT password_hash FROM users WHERE id = '{person.Id}'"))!;
        var bytes = Convert.FromBase64String(stored);

        Assert.Equal(0x01, bytes[0]);
        Assert.Equal(1000, (bytes[5] << 24) | (bytes[6] << 16) | (bytes[7] << 8) | bytes[8]);
        Assert.DoesNotContain(Password, stored, StringComparison.Ordinal);

        // A store configured differently still verifies it: the count is read from the hash.
        Assert.Equal(person, await database.Users(hashIterations: null).VerifyAsync("alice@example.test", Password, Ct));
    }

    /// <summary>
    /// REVIEW FOCUS 2: eight sign-ups for one address at once, in different cases. One account is made
    /// and the rest are told it is taken; none fails.
    /// </summary>
    [Fact]
    public async Task Concurrent_creates_of_one_email_make_one_user()
    {
        var users = await CreateAsync();

        for (var round = 0; round < 3; round++)
        {
            var email = $"raced{round}@example.test";
            var made = await RaceAsync(8, i => users.TryCreateAsync(i % 2 == 0 ? email : email.ToUpperInvariant(), Password, Ct));

            Assert.Single(made, user => user is not null);
            Assert.Equal(7, made.Count(user => user is null));
        }

        Assert.Equal(3, (await users.ListAsync(Ct)).Count);
    }
}

public sealed class SqliteUserStoreTests : UserStoreContract
{
    protected override Task<IdentityDatabase> OpenAsync() => Task.FromResult<IdentityDatabase>(new SqliteIdentityDatabase());
}

public sealed class SqlServerUserStoreTests : UserStoreContract
{
    protected override async Task<IdentityDatabase> OpenAsync() => await SqlServerIdentityDatabase.CreateAsync();
}
