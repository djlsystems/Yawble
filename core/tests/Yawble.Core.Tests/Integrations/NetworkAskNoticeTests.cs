using Microsoft.Data.Sqlite;
using Yawble.Core.Bus;
using Yawble.Core.Integrations;
using Yawble.Core.Policy;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Gateway;
using Yawble.Core.Tests.Policy;
using Xunit;
using MemberRef = Yawble.Core.Bus.MemberRef;

namespace Yawble.Core.Tests.Integrations;

/// <summary>
/// Merged "now allowed" notices (B018 part 3, card 184): allows told to one team's Manager within the notice window
/// (5 s) become one wake listing every host; allows further apart are told apart. Fakes only: a temporary database,
/// a fake clock whose timers fire only when the test moves it, and a recording tell.
/// </summary>
public sealed class NetworkAskNoticeTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempDb _db = new();
    private readonly FakeTimers _time = new(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly PolicyStore _policies;
    private readonly List<(string Team, string Member, string Text, long? Causation)> _told = [];
    private readonly NetworkAskService _asks;
    private readonly long _alpha, _beta, _priya;

    public NetworkAskNoticeTests()
    {
        _policies = new PolicyStore(_db.ConnectionString);
        var layout = PolicyFixtures.Layout(TargetOs.Windows, AgentCli.Claude);
        _asks = new NetworkAskService(_db.ConnectionString,
            new StoreNetworkPolicy(_policies, (_, _) => Task.FromResult(layout), _ => Task.FromResult<RunLayout?>(layout)),
            (team, member, text, causation, _) =>
            {
                lock (_told) _told.Add((team, member, text, causation));
                return Task.CompletedTask;
            },
            _time.Clock) { NoticeDelay = _time.Delay };
        _alpha = Team("Alpha");
        _beta = Team("Beta");
        Member(_alpha, "Manager", "manager");
        _priya = Member(_alpha, "Priya", "member");
        Member(_alpha, "Bo", "member");
        Member(_beta, "Manager", "manager");
        Member(_beta, "Wren", "member");
    }

    public void Dispose() => _db.Dispose();

    private long Team(string name) => Insert("INSERT INTO teams (name, created_at) VALUES ($n, '2026-11-13T00:00:00Z') RETURNING id;", ("$n", name));

    private long Member(long team, string name, string role) =>
        Insert("INSERT INTO members (team_id, name, role, created_at) VALUES ($t, $n, $r, '2026-11-13T00:00:00Z') RETURNING id;", ("$t", team), ("$n", name), ("$r", role));

    private long Insert(string sql, params (string, object)[] args)
    {
        using var c = new SqliteConnection(_db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        return (long)cmd.ExecuteScalar()!;
    }

    private async Task AllowAsync(string member, string host, string choice = NetworkAskChoices.Agent)
    {
        _asks.Refused(member, host, "CONNECT", null);
        var id = _asks.Asks().Single(a => a.Member == member && a.Host == host).Id;
        var result = await _asks.AnswerAsync(id, choice, null, Ct);
        Assert.True(result["ok"]!.GetValue<bool>());
    }

    [Fact]
    public void TheWindowIsFiveSeconds_ByDefault()
    {
        var asks = new NetworkAskService(_db.ConnectionString, new StoreNetworkPolicy(_policies, (_, _) => throw new InvalidOperationException(), _ => throw new InvalidOperationException()),
            (_, _, _, _, _) => Task.CompletedTask);
        Assert.Equal(TimeSpan.FromSeconds(5), asks.NoticeWindow);
    }

    [Fact]
    public async Task ThreeAllowsInTwoSeconds_WakeTheManagerOnce_ListingEveryHost()
    {
        await AllowAsync("Alpha/Priya", "crl3.digicert.com");
        _time.Advance(TimeSpan.FromSeconds(1));
        await AllowAsync("Alpha/Priya", "ocsp.digicert.com");
        _time.Advance(TimeSpan.FromSeconds(1));
        await AllowAsync("Alpha/Priya", "crl4.digicert.com");
        Assert.Empty(_told); // the window is still open

        _time.Advance(TimeSpan.FromSeconds(3)); // 5 s after the first

        var told = Assert.Single(_told);
        Assert.Equal(("Alpha", "Manager"), (told.Team, told.Member));
        Assert.Equal("crl3.digicert.com, ocsp.digicert.com and crl4.digicert.com are now allowed for Priya; retry what was blocked. " +
                     "They apply to runs that start from now on.", told.Text);
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Single(_told);
    }

    [Fact]
    public async Task TwoAllowsTenSecondsApart_AreToldApart()
    {
        await AllowAsync("Alpha/Priya", "api.nuget.org");
        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal("api.nuget.org is now allowed for Priya; retry what was blocked. It applies to runs that start from now on.", Assert.Single(_told).Text);

        await AllowAsync("Alpha/Priya", "crl3.digicert.com");
        Assert.Single(_told);
        _time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(2, _told.Count);
        Assert.Equal("crl3.digicert.com is now allowed for Priya; retry what was blocked. It applies to runs that start from now on.", _told[1].Text);
    }

    [Fact]
    public async Task OneWake_KeepsEachMembersHostsAndScope_InTheOrderTheyCame()
    {
        await AllowAsync("Alpha/Priya", "api.nuget.org");
        await AllowAsync("Alpha/Bo", "registry.npmjs.org");
        await AllowAsync("Alpha/Priya", "crl3.digicert.com");
        await AllowAsync("Alpha/Bo", "pypi.org", NetworkAskChoices.Team);
        _time.Advance(TimeSpan.FromSeconds(5));

        var told = Assert.Single(_told);
        Assert.Equal("api.nuget.org and crl3.digicert.com are now allowed for Priya; retry what was blocked. " +
                     "registry.npmjs.org is now allowed for Bo; retry what was blocked. " +
                     "pypi.org is now allowed for Bo; retry what was blocked. The person allowed it for every member of Alpha. " +
                     "They apply to runs that start from now on.", told.Text);
    }

    [Fact]
    public async Task EachTeamsManager_HasItsOwnWindow()
    {
        await AllowAsync("Alpha/Priya", "api.nuget.org");
        _time.Advance(TimeSpan.FromSeconds(3));
        await AllowAsync("Beta/Wren", "pypi.org");
        _time.Advance(TimeSpan.FromSeconds(2)); // Alpha's window ends; Beta's is open for 3 s more

        var alpha = Assert.Single(_told);
        Assert.Equal(("Alpha", "Manager"), (alpha.Team, alpha.Member));
        Assert.StartsWith("api.nuget.org is now allowed for Priya;", alpha.Text);

        _time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(2, _told.Count);
        Assert.Equal(("Beta", "Manager"), (_told[1].Team, _told[1].Member));
        Assert.StartsWith("pypi.org is now allowed for Wren;", _told[1].Text);
    }

    [Fact]
    public async Task OneEverywhereAllow_ToldToEachTeamsManager_AfterTheWindow()
    {
        _asks.Refused("Beta/Wren", "pypi.org", "CONNECT", null);
        await AllowAsync("Alpha/Priya", "pypi.org", NetworkAskChoices.Everywhere);
        Assert.Empty(_told);

        _time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(2, _told.Count);
        Assert.Contains(_told, t => t is { Team: "Alpha", Member: "Manager" } && t.Text.StartsWith("pypi.org is now allowed for Priya; retry what was blocked. The person allowed it for every agent on this computer.", StringComparison.Ordinal));
        Assert.Contains(_told, t => t is { Team: "Beta", Member: "Manager" } && t.Text.StartsWith("pypi.org is now allowed for Wren;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheMergedWake_JoinsTheFirstAsksWorkflow_ThatHasOne()
    {
        await AllowAsync("Alpha/Priya", "api.nuget.org"); // a refusal: no workflow
        var priya = new MemberRef(_priya, _alpha, "Alpha", "Priya", MemberRole.Member);
        _asks.RequestFromAgent(priya, "crl3.digicert.com", "NuGet's signature revocation check", workflow: 40, causation: 41);
        var id = _asks.Asks().Single(a => a.Host == "crl3.digicert.com").Id;
        await _asks.AnswerAsync(id, NetworkAskChoices.Agent, null, Ct);
        _time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(41, Assert.Single(_told).Causation);
    }

    [Fact]
    public void TheText_ForOneHost_IsTheSingleNotice()
    {
        var text = NetworkAskService.AllowedText([new NetworkAskService.AllowNotice("api.nuget.org", "Priya and Bo", NetworkAskChoices.Team, "Alpha", null)]);
        Assert.Equal("api.nuget.org is now allowed for Priya and Bo; retry what was blocked. The person allowed it for every member of Alpha. " +
                     "It applies to runs that start from now on.", text);
    }
}

/// <summary>A fake clock with delays that complete only when <see cref="Advance"/> moves it past their end, inline.</summary>
internal sealed class FakeTimers(DateTimeOffset start)
{
    private readonly List<(DateTimeOffset Due, TaskCompletionSource Done)> _waiting = [];

    public FakeClock Clock { get; } = new(start);

    public Task Delay(TimeSpan by)
    {
        var done = new TaskCompletionSource();
        lock (_waiting) _waiting.Add((Clock.Now + by, done));
        return done.Task;
    }

    public void Advance(TimeSpan by)
    {
        Clock.Advance(by);
        List<TaskCompletionSource> due;
        lock (_waiting)
        {
            due = _waiting.Where(w => w.Due <= Clock.Now).Select(w => w.Done).ToList();
            _waiting.RemoveAll(w => w.Due <= Clock.Now);
        }
        foreach (var d in due) d.SetResult(); // the flush runs inline: the tell is recorded when this returns
    }
}
