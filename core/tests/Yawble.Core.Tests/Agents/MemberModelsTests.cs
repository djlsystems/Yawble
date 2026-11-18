using Microsoft.Data.Sqlite;
using Xunit;
using Yawble.Core.Agents;
using Yawble.Core.Agents.Profiles;
using Yawble.Core.Bus;
using Yawble.Core.Policy;
using Yawble.Core.Tests.Bus;

namespace Yawble.Core.Tests.Agents;

/// <summary>The member pane's agent pill: which model a member runs, and where the name comes from.</summary>
public sealed class MemberModelsTests : IDisposable
{
    private readonly TempDb _db = new();
    private readonly AgentDefaultsStore _defaults;
    private readonly AgentProfileStore _profiles;
    private readonly MemberModels _models;
    private long _id;

    public MemberModelsTests()
    {
        _defaults = new AgentDefaultsStore(_db.ConnectionString);
        AgentProfileStore? store = null;
        var log = new AgentChangeLog(_db.ConnectionString, null, (a, m) => store!.ResolveModel(a, m));
        _profiles = store = new AgentProfileStore(_db.ConnectionString, log, defaults: _defaults);
        _models = new MemberModels(_db.ConnectionString, _profiles, _defaults);
    }

    public void Dispose() => _db.Dispose();

    private static MemberRow Member(string cli = "claude", string model = "") =>
        new(1, 1, "payments", "DeveloperMarlo", "member", cli, "", model, "default");

    private void Usage(string run, string model, int output, string member = "payments/DeveloperMarlo", string? at = null)
    {
        using var c = new SqliteConnection(_db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO usage_events (at, run_key, member, vhost, path, status, model, output_tokens)
            VALUES ($at, $run, $member, 'anthropic.yawble.internal', '/v1/messages', 200, $model, $out);
            """;
        cmd.Parameters.AddWithValue("$at", at ?? $"2026-11-18T20:{++_id:00}:00.0000000+00:00");
        cmd.Parameters.AddWithValue("$run", run);
        cmd.Parameters.AddWithValue("$member", member);
        cmd.Parameters.AddWithValue("$model", model);
        cmd.Parameters.AddWithValue("$out", output);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void TheMembersOwnModelWins()
    {
        _defaults.Set(AgentCli.Claude, "claude-sonnet-5-5");
        Usage("r1", "claude-haiku-5-5", 900);
        Assert.Equal(new MemberModel("claude-opus-5-5", ModelSources.Member), _models.For(Member(model: "claude-opus-5-5")));
    }

    [Fact]
    public void ThenTheAgentsDefault()
    {
        _defaults.Set(AgentCli.Claude, "claude-sonnet-5-5");
        Assert.Equal(new MemberModel("claude-sonnet-5-5", ModelSources.Agent), _models.For(Member()));
    }

    [Fact]
    public void ACustomAgentBringsItsOwnDefault()
    {
        _profiles.Duplicate(AgentCli.Codex, "codex-fast", "gpt-5.1-codex-mini");
        Assert.Equal(new MemberModel("gpt-5.1-codex-mini", ModelSources.Agent), _models.For(Member(cli: "codex-fast")));
    }

    [Fact]
    public void ThenTheModelTheLastRunUsedMost_NotItsSideModel()
    {
        Usage("r1", "claude-sonnet-5-5", 5000);
        Usage("r2", "claude-opus-5-5", 4000);
        Usage("r2", "claude-haiku-5-5", 30);   // a title or a summary: many small calls
        Usage("r2", "claude-haiku-5-5", 30);
        Usage("r2", "claude-haiku-5-5", 30, at: "2026-11-18T21:30:00.0000000+00:00");
        var m = _models.For(Member());
        Assert.Equal("claude-opus-5-5", m.Model);
        Assert.Equal(MemberModelSources.Observed, m.Source);
        Assert.Equal("2026-11-18T21:30:00.0000000+00:00", m.At);
    }

    [Fact]
    public void AnotherMembersRunsDoNotCount()
    {
        Usage("r9", "claude-opus-5-5", 4000, member: "payments/TesterWren");
        Assert.Equal(new MemberModel("", ModelSources.Cli), _models.For(Member()));
    }

    [Fact]
    public void NeverRun_IsTheClisDefault()
    {
        Assert.Equal(new MemberModel("", ModelSources.Cli), _models.For(Member(cli: "grok")));
        Assert.Equal(new MemberModel("", ModelSources.Cli), _models.For(Member(cli: "no-such-agent")));
    }
}
