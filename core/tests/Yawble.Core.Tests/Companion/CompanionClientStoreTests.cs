using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Yawble.Core.Companion;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Companion;

public class CompanionClientStoreTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void KeyIsStoredOnlyAsItsHash()
    {
        using var db = new TempDb();
        var store = new CompanionClientStore(db.ConnectionString);
        store.Add("claude-code", CompanionLevel.Act, "pat", out var plainKey);
        Assert.StartsWith("ywb_", plainKey);

        using var c = new SqliteConnection(db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM companion_clients;";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plainKey)));
        string? hash = null;
        for (var i = 0; i < r.FieldCount; i++)
        {
            var text = Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture) ?? "";
            Assert.DoesNotContain(plainKey, text);
            if (r.GetName(i) == "key_hash") hash = text;
        }
        Assert.Equal(expected, hash);
    }

    [Fact]
    public void AuthenticateAcceptsTheKey_RefusesAWrongKeyARevokedClientAndAnUnknownName()
    {
        using var db = new TempDb();
        var store = new CompanionClientStore(db.ConnectionString);
        store.Add("claude-code", CompanionLevel.Read, "pat", out var key);

        var ok = store.Authenticate("claude-code", key);
        Assert.NotNull(ok);
        Assert.Equal(CompanionLevel.Read, ok.Level);
        Assert.Null(store.Authenticate("claude-code", key + "x"));
        Assert.Null(store.Authenticate("claude-code", ""));
        Assert.Null(store.Authenticate("nobody", key));

        Assert.True(store.Revoke("claude-code"));
        Assert.Null(store.Authenticate("claude-code", key));
        Assert.False(store.Revoke("claude-code"));
        Assert.False(store.Revoke("nobody"));
    }

    [Fact]
    public void LastUsedIsTouchedAtMostOnceAMinute()
    {
        using var db = new TempDb();
        var clock = new Clock();
        var store = new CompanionClientStore(db.ConnectionString, clock);
        store.Add("claude-code", CompanionLevel.Read, "pat", out var key);
        Assert.Null(store.List().Single().LastUsedAt);

        var t0 = clock.Now;
        store.Authenticate("claude-code", key);
        Assert.Equal(t0, store.List().Single().LastUsedAt);
        clock.Now = t0.AddSeconds(30);
        store.Authenticate("claude-code", key);
        Assert.Equal(t0, store.List().Single().LastUsedAt);
        clock.Now = t0.AddSeconds(61);
        store.Authenticate("claude-code", key);
        Assert.Equal(t0.AddSeconds(61), store.List().Single().LastUsedAt);
    }

    [Theory]
    [InlineData("claude-code", true)]
    [InlineData("a", true)]
    [InlineData("Claude Code", false)]
    [InlineData("../x", false)]
    [InlineData("", false)]
    [InlineData("-a", false)]
    public void NamesAreValidated(string name, bool valid) => Assert.Equal(valid, CompanionClientStore.ValidName(name));

    [Fact]
    public void NamesAreValidated_Length()
    {
        Assert.True(CompanionClientStore.ValidName(new string('a', 32)));
        Assert.False(CompanionClientStore.ValidName(new string('a', 33)));
        using var db = new TempDb();
        var store = new CompanionClientStore(db.ConnectionString);
        Assert.Throws<ArgumentException>(() => store.Add("Claude Code", CompanionLevel.Read, "pat", out _));
    }

    [Fact]
    public void AnActiveNameCannotBePairedTwice_ARevokedNameCan()
    {
        using var db = new TempDb();
        var store = new CompanionClientStore(db.ConnectionString);
        store.Add("claude-code", CompanionLevel.Read, "pat", out var first);
        Assert.Throws<InvalidOperationException>(() => store.Add("claude-code", CompanionLevel.Act, "pat", out _));

        Assert.True(store.Revoke("claude-code"));
        var again = store.Add("claude-code", CompanionLevel.Destructive, "pat", out var second);
        Assert.NotEqual(first, second);
        Assert.Equal(CompanionLevel.Destructive, again.Level);
        Assert.Null(again.RevokedAt);
        Assert.Single(store.List());
        Assert.Null(store.Authenticate("claude-code", first));
        Assert.NotNull(store.Authenticate("claude-code", second));
    }

    [Fact]
    public void LevelsParseAndDescribe()
    {
        Assert.True(CompanionLevels.TryParse("act", out var l));
        Assert.Equal(CompanionLevel.Act, l);
        Assert.False(CompanionLevels.TryParse("root", out _));
        Assert.False(CompanionLevels.TryParse(null, out _));
        Assert.Equal("destructive", CompanionLevels.Name(CompanionLevel.Destructive));
        Assert.False(string.IsNullOrWhiteSpace(CompanionLevels.Describe(CompanionLevel.Read)));
    }
}
