using Microsoft.Data.Sqlite;
using Xunit;
using Yawble.Core.Bus;
using Yawble.Core.Credentials;
using Yawble.Core.Launch;
using Yawble.Core.Storage;
using Yawble.Core.Teams;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Connections;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// A team's environment values are encrypted in the database, under a key kept in the computer's credential store.
/// The names stay readable. Values stored unencrypted are encrypted at the next start. On a computer without the key
/// the names are listed, the values are marked to be entered again, and no run gets them. The credential store is a
/// fake in memory.
/// </summary>
public class TeamEnvVaultTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Token = "tok-4f9a-registry-secret";

    private sealed class Rig : IAsyncDisposable
    {
        public TempDb Db { get; } = new();
        public List<string> Log { get; } = [];
        public BusEngine Engine { get; private set; } = null!;

        public static async Task<Rig> StartAsync(ICredentialStore store)
        {
            var rig = new Rig();
            await rig.OpenAsync(store);
            await rig.Engine.CreateTeamAsync("Alpha", ct: Ct);
            await rig.Engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
            return rig;
        }

        public TeamEnvVault Vault(ICredentialStore store) => new(store, Log.Add);

        /// <summary>The bus as a start of the app on a computer with this credential store makes it (the one before is closed).</summary>
        public async Task OpenAsync(ICredentialStore store)
        {
            if (Engine is not null) await Engine.DisposeAsync();
            Engine = new BusEngine(Db.OpenStore(), new ScriptedRunner(), new BusOptions { IdleOffer = false, EnvVault = Vault(store) });
        }

        /// <summary>What a run of Ada gets, read as the launcher reads it.</summary>
        public IReadOnlyDictionary<string, string> RunEnv(ICredentialStore store) =>
            new SqliteMemberDirectory(Db.ConnectionString, envVault: Vault(store)).Find("Alpha", "Ada")!.TeamEnv!;

        public string StoredText()
        {
            using var c = Yawble.Core.Storage.Db.Open(Db.ConnectionString);
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT env FROM teams WHERE name = 'Alpha';";
            return (string)cmd.ExecuteScalar()!;
        }

        public void StoreText(string json)
        {
            using var c = Yawble.Core.Storage.Db.Open(Db.ConnectionString);
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE teams SET env = $e WHERE name = 'Alpha';";
            cmd.Parameters.AddWithValue("$e", json);
            cmd.ExecuteNonQuery();
        }

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Db.Dispose();
        }
    }

    /// <summary>A credential store that refuses every read, as an OS store that does not answer.</summary>
    private sealed class DeafStore : ICredentialStore
    {
        public string Kind => "memory";
        public string? Get(string key) => throw new CredentialStoreException("the store did not answer");
        public void Set(string key, string value) => throw new CredentialStoreException("the store did not answer");
        public bool Delete(string key) => false;
    }

    [Fact]
    public async Task ASavedValue_IsNotReadableInTheDatabase_AndIsReadBackAndGivenToRuns()
    {
        var store = new MemoryCredentialStore();
        await using var rig = await Rig.StartAsync(store);

        var saved = await rig.Engine.SetTeamEnvAsync("Alpha", [new("REGISTRY_KEY", Token), new("API_BASE", "http://localhost:5000")], Ct);

        Assert.Equal(Token, saved["REGISTRY_KEY"]);
        var text = rig.StoredText();
        Assert.Contains("REGISTRY_KEY", text);
        Assert.Contains("API_BASE", text);
        Assert.DoesNotContain(Token, text);
        Assert.DoesNotContain("localhost", text);
        Assert.All(TeamEnv.Parse(text).Values, v => Assert.True(TeamEnvVault.IsSealed(v)));
        // the key is in the credential store, and nowhere in the database text
        Assert.Equal([TeamEnvVault.KeyName], store.Keys);
        Assert.DoesNotContain(store.Get(TeamEnvVault.KeyName)!, text);

        // a later start on this computer reads them, and a run gets them
        await rig.OpenAsync(store);
        var env = await rig.Engine.TeamEnvAsync("Alpha", Ct);
        Assert.Equal((Token, "http://localhost:5000"), (env["REGISTRY_KEY"], env["API_BASE"]));
        Assert.Equal(["REGISTRY_KEY", "API_BASE"], env.Keys);
        Assert.Equal(Token, rig.RunEnv(store)["REGISTRY_KEY"]);
    }

    [Fact]
    public async Task TheSameValueSavedTwice_IsStoredAsDifferentText()
    {
        var store = new MemoryCredentialStore();
        await using var rig = await Rig.StartAsync(store);
        await rig.Engine.SetTeamEnvAsync("Alpha", [new("A", Token)], Ct);
        var first = rig.StoredText();
        await rig.Engine.SetTeamEnvAsync("Alpha", [new("A", Token)], Ct);
        Assert.NotEqual(first, rig.StoredText());
    }

    [Fact]
    public async Task ValuesStoredUnencrypted_AreEncryptedAtTheNextStart_AndKeepTheirValues()
    {
        var store = new MemoryCredentialStore();
        await using var rig = await Rig.StartAsync(store);
        rig.StoreText($$"""{"REGISTRY_KEY":"{{Token}}","EMPTY":"","API_BASE":"http://localhost:5000"}""");

        var state = rig.Vault(store).SealStored(rig.Db.ConnectionString);

        Assert.Equal(1, state.TeamsSealed);
        Assert.Empty(state.UnreadableByTeam);
        var text = rig.StoredText();
        Assert.DoesNotContain(Token, text);
        Assert.DoesNotContain("localhost", text);
        Assert.Contains(rig.Log, l => l.Contains("encrypted the values of 1 team"));
        var env = await rig.Engine.TeamEnvAsync("Alpha", Ct);
        Assert.Equal((Token, "", "http://localhost:5000"), (env["REGISTRY_KEY"], env["EMPTY"], env["API_BASE"]));
        Assert.Equal(Token, rig.RunEnv(store)["REGISTRY_KEY"]);

        // the start after that finds nothing to do and changes nothing
        Assert.Equal(0, rig.Vault(store).SealStored(rig.Db.ConnectionString).TeamsSealed);
        Assert.Equal(text, rig.StoredText());
    }

    [Fact]
    public async Task ATeamWithNoVariables_NeedsNoKey()
    {
        var store = new MemoryCredentialStore();
        await using var rig = await Rig.StartAsync(store);

        Assert.Equal(0, rig.Vault(store).SealStored(rig.Db.ConnectionString).TeamsSealed);
        Assert.Empty(await rig.Engine.TeamEnvAsync("Alpha", Ct));
        Assert.Empty(rig.RunEnv(store));

        Assert.Empty(store.Keys);
    }

    [Fact]
    public async Task OnAComputerWithoutTheKey_TheNamesAreListed_TheValuesAreToBeEnteredAgain_AndNoRunGetsThem()
    {
        var here = new MemoryCredentialStore();
        await using var rig = await Rig.StartAsync(here);
        await rig.Engine.SetTeamEnvAsync("Alpha", [new("REGISTRY_KEY", Token), new("API_BASE", "http://localhost:5000")], Ct);
        var text = rig.StoredText();

        // the database is restored on another computer: its credential store has no key
        var other = new MemoryCredentialStore();
        var state = rig.Vault(other).SealStored(rig.Db.ConnectionString);
        await rig.OpenAsync(other);

        Assert.Equal(0, state.TeamsSealed);
        Assert.Equal(2, state.UnreadableByTeam["Alpha"]);
        Assert.Contains(rig.Log, l => l.Contains("2 value(s) of team 'Alpha' cannot be read on this computer") && l.Contains("Enter them again"));
        var env = await rig.Engine.TeamEnvAsync("Alpha", Ct);
        Assert.Equal(["REGISTRY_KEY", "API_BASE"], env.Keys);
        Assert.All(env.Values, v => Assert.Equal(TeamEnv.UnreadableValue, v));
        Assert.Empty(rig.RunEnv(other));
        // reading made no key and changed nothing
        Assert.Empty(other.Keys);
        Assert.Equal(text, rig.StoredText());

        // the person enters one again and leaves the other: the new one works, the other still waits
        var saved = await rig.Engine.SetTeamEnvAsync("Alpha", [new("REGISTRY_KEY", "new-token"), new("API_BASE", TeamEnv.UnreadableValue)], Ct);
        Assert.Equal(("new-token", TeamEnv.UnreadableValue), (saved["REGISTRY_KEY"], saved["API_BASE"]));
        Assert.Equal("new-token", Assert.Single(rig.RunEnv(other)).Value);
        Assert.DoesNotContain(TeamEnv.UnreadableValue, rig.StoredText());
        Assert.Equal(TeamEnv.Parse(text)["API_BASE"], TeamEnv.Parse(rig.StoredText())["API_BASE"]);

        // a variable that has no stored value cannot be saved with the marker as its value
        var refused = await Assert.ThrowsAsync<BusRefusal>(() => rig.Engine.SetTeamEnvAsync("Alpha", [new("NEW_ONE", TeamEnv.UnreadableValue)], Ct));
        Assert.Contains("'NEW_ONE' has no value yet", refused.Message);
    }

    [Fact]
    public async Task ACloudViewer_SeesThatAValueIsToBeEnteredAgain_AndASaveFromThereLeavesIt()
    {
        var here = new MemoryCredentialStore();
        await using var rig = await Rig.StartAsync(here);
        await rig.Engine.SetTeamEnvAsync("Alpha", [new("LOST", Token)], Ct);
        var other = new MemoryCredentialStore();
        await rig.OpenAsync(other);
        await rig.Engine.SetTeamEnvAsync("Alpha", [new("LOST", TeamEnv.UnreadableValue), new("KEPT", "v")], Ct);

        var hidden = TeamEnv.Hidden(await rig.Engine.TeamEnvAsync("Alpha", Ct));
        Assert.Equal((TeamEnv.UnreadableValue, TeamEnv.HiddenValue), (hidden["LOST"], hidden["KEPT"]));

        var saved = await rig.Engine.SetTeamEnvAsync("Alpha", [new("LOST", TeamEnv.UnreadableValue), new("KEPT", TeamEnv.HiddenValue)], Ct, hiddenValues: true);
        Assert.Equal((TeamEnv.UnreadableValue, "v"), (saved["LOST"], saved["KEPT"]));
    }

    [Fact]
    public async Task AValueMovedUnderAnotherName_OrDamaged_DoesNotOpen()
    {
        var store = new MemoryCredentialStore();
        await using var rig = await Rig.StartAsync(store);
        await rig.Engine.SetTeamEnvAsync("Alpha", [new("REGISTRY_KEY", Token), new("OTHER", "x")], Ct);
        var stored = TeamEnv.Parse(rig.StoredText());
        rig.StoreText(TeamEnv.Serialize([new("OTHER", stored["REGISTRY_KEY"]), new("BROKEN", TeamEnvVault.SealedPrefix + "not base64!"), new("SHORT", TeamEnvVault.SealedPrefix + "AAAA")]));

        var read = TeamEnvVault.Read(rig.Vault(store), rig.StoredText());

        Assert.Empty(read.Values);
        Assert.Equal(["OTHER", "BROKEN", "SHORT"], read.Unreadable);
    }

    [Fact]
    public async Task WhenTheCredentialStoreRefusesTheKey_NothingIsSaved_AndNothingIsStoredUnencrypted()
    {
        var store = new MemoryCredentialStore { FailWrites = true };
        await using var rig = await Rig.StartAsync(store);

        var refused = await Assert.ThrowsAsync<BusRefusal>(() => rig.Engine.SetTeamEnvAsync("Alpha", [new("REGISTRY_KEY", Token)], Ct));

        Assert.Contains("nothing was saved", refused.Message);
        Assert.Contains("credential store", refused.Message);
        Assert.Equal("{}", rig.StoredText());

        // values a version of the app stored unencrypted stay as they are, the log says why, and they still work
        rig.StoreText($$"""{"REGISTRY_KEY":"{{Token}}"}""");
        Assert.Equal(0, rig.Vault(store).SealStored(rig.Db.ConnectionString).TeamsSealed);
        Assert.Contains(rig.Log, l => l.Contains("not encrypted yet"));
        Assert.Equal(Token, rig.RunEnv(store)["REGISTRY_KEY"]);
    }

    [Fact]
    public async Task WhenTheCredentialStoreDoesNotAnswer_ReadingDoesNotFail_AndTheValuesAreUnread()
    {
        var store = new MemoryCredentialStore();
        await using var rig = await Rig.StartAsync(store);
        await rig.Engine.SetTeamEnvAsync("Alpha", [new("REGISTRY_KEY", Token)], Ct);
        var deaf = new DeafStore();

        Assert.Equal(1, rig.Vault(deaf).SealStored(rig.Db.ConnectionString).UnreadableByTeam["Alpha"]);
        await rig.OpenAsync(deaf);
        Assert.Equal(TeamEnv.UnreadableValue, (await rig.Engine.TeamEnvAsync("Alpha", Ct))["REGISTRY_KEY"]);
        Assert.Empty(rig.RunEnv(deaf));

        // the store answers again: the value is there
        await rig.OpenAsync(store);
        Assert.Equal(Token, (await rig.Engine.TeamEnvAsync("Alpha", Ct))["REGISTRY_KEY"]);
    }

    [Fact]
    public void TheStepThatMarksEncryptedValues_IsAMigration_SoAnOlderVersionDoesNotOpenTheDatabase() =>
        Assert.Contains("0063_team_env_encrypted", SchemaMigrator.Available);
}
