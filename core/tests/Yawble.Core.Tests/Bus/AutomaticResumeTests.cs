using System.Net;
using System.Text.Json;
using Yawble.Core.Bus;
using Yawble.Core.Gateway;
using Yawble.Core.Launch;
using Xunit;

namespace Yawble.Core.Tests.Bus;

/// <summary>Automatic resume after a usage limit (Yawble ResumeSweep): when to come back, and the Manager told, at most 3 times.</summary>
public class AutomaticResumeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 3, 0, 0, TimeSpan.Zero);

    private static HttpResponseMessage TooMany(params (string Name, string Value)[] headers)
    {
        var r = new HttpResponseMessage((HttpStatusCode)429);
        foreach (var (n, v) in headers) r.Headers.TryAddWithoutValidation(n, v);
        return r;
    }

    [Fact]
    public void WhenToComeBack_IsReadFromWhicheverHeaderTheProviderSends()
    {
        Assert.Equal(Now.AddSeconds(120), RunGateway.RetryAt(TooMany(("retry-after", "120")), Now));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794200000), RunGateway.RetryAt(TooMany(("anthropic-ratelimit-unified-reset", "1794200000")), Now));
        Assert.Equal(Now.AddSeconds(90), RunGateway.RetryAt(TooMany(("x-ratelimit-reset-requests", "1m30s")), Now));
        Assert.Equal(Now.AddMinutes(5), RunGateway.RetryAt(TooMany(), Now)); // nothing said: five minutes
    }

    [Fact]
    public void TheVerdictIsRateLimited_UnlessALaterRequestWentThrough()
    {
        GatewayEvent E(string kind, int status, string? detail = null, int s = 0) =>
            new(1, "Alpha/Ada", Now.AddSeconds(s), kind, false, Vhosts.Model, "POST", "/v1/messages", status, detail);
        var until = Now.AddHours(2).ToString("O");
        Assert.Equal(Now.AddHours(2), GatewayVerdicts.From([E(GatewayEventKinds.Forward, 200), E(GatewayEventKinds.RateLimited, 429, until, 1)]).RateLimitedUntil);
        Assert.Null(GatewayVerdicts.From([E(GatewayEventKinds.RateLimited, 429, until), E(GatewayEventKinds.Forward, 200, null, 5)]).RateLimitedUntil);
    }

    [Fact]
    public async Task AfterTheTimePasses_TheManagerIsToldOnce_PerFailure_AndAtMostThreeTimesAWorkflow()
    {
        var retryAt = Now.AddMinutes(30);
        var details = new Dictionary<string, object?> { ["retryAfter"] = retryAt.ToString("O") };
        var adaRuns = 0;
        var runner = new ScriptedRunner().On("Alpha/Ada", (_, _) =>
        {
            adaRuns++;
            return Task.FromResult(new RunResult(false, "usage limit", FailureClasses.Rate, details));
        });
        using var db = new TempDb();
        await using var engine = new BusEngine(db.OpenStore(), runner, new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false });
        await engine.CreateTeamAsync("Alpha", ct: Ct);
        await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
        await engine.StartAsync(Ct);
        var root = await engine.PersonTellAsync("Alpha", "Ada", "build it", ct: Ct);
        await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "Ada's limited run and the Manager's run");

        int Resumes() { using var s = db.OpenStore(); return s.Workflow(root.Seq).Count(m => m.Payload.Contains($"\"{BusEngine.AutomaticResumeMarker}\":true")); }
        await engine.SweepTriggersAsync(Now, Ct);
        Assert.Equal(0, Resumes()); // not yet: the provider said 03:30
        await engine.SweepTriggersAsync(retryAt.AddSeconds(1), Ct);
        Assert.Equal(1, Resumes());
        await engine.SweepTriggersAsync(retryAt.AddSeconds(20), Ct);
        Assert.Equal(1, Resumes()); // the same failure is answered once

        using (var s = db.OpenStore())
        {
            var resume = s.Workflow(root.Seq).Single(m => m.Payload.Contains(BusEngine.AutomaticResumeMarker));
            Assert.Equal(MessageTypes.InstructionPrefix + "Alpha/Manager", resume.Type);
            Assert.Contains("automatic resume 1 of 3", JsonDocument.Parse(resume.Payload).RootElement.GetProperty("text").GetString());

            // Two more limited failures of Ada in the workflow, each answered, then a fourth is not.
            for (var i = 0; i < 3; i++)
            {
                var again = s.Append(new NewMessage(MessageTypes.Failed,
                    JsonSerializer.Serialize(new { failureClass = FailureClasses.Rate, run = new { retryAfter = retryAt.ToString("O") } }), "Alpha/Ada", root.Seq));
                await engine.SweepTriggersAsync(retryAt.AddMinutes(1 + i), Ct);
            }
        }
        Assert.Equal(3, Resumes());
    }
}
