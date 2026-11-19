using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Yawble.Core.Credentials;
using Yawble.Core.Gateway;
using Yawble.Core.Launch;
using Yawble.Core.Tests.Gateway;
using Xunit;
using ManualClock = Yawble.Core.Tests.Board.TimelineCalculatorTests.ManualClock;

namespace Yawble.Core.Tests.Launch;

/// <summary>
/// Stalled (B086) means the agent showed no sign of work for 10 minutes. A model request going out or streaming back
/// is a sign of work: a run that talks to the model without calling the bus is working, not stalled. Seen 2026-11-18:
/// DeveloperMarlo's card 446 showed 15 minutes Stalled while his run made model requests every few seconds. The idle
/// watchdog still waits for a bus call.
/// </summary>
public class RunActivityStallTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 18, 20, 36, 0, TimeSpan.Zero);

    [Fact]
    public void ModelRequestsEveryFewMinutes_AreNotAStall()
    {
        var clock = new ManualClock(T0);
        var activity = new RunActivity(clock);
        activity.Touch(); // a progress call
        for (var i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(3));
            activity.Saw(); // a model request
        }
        clock.Advance(TimeSpan.FromMinutes(1));
        activity.Touch(); // the next progress call, 16 minutes after the first
        Assert.Empty(activity.Spans(clock.GetUtcNow()).Stalls);
    }

    [Fact]
    public void TenQuietMinutes_StillAStall_EvenAfterAModelRequest()
    {
        var clock = new ManualClock(T0);
        var activity = new RunActivity(clock);
        activity.Saw();
        clock.Advance(TimeSpan.FromMinutes(12));
        activity.Touch();
        var stall = Assert.Single(activity.Spans(clock.GetUtcNow()).Stalls);
        Assert.Equal(T0, stall.Start);
        Assert.Equal(T0.AddMinutes(12), stall.End);

        // An open stall is measured from the last model request too.
        clock.Advance(TimeSpan.FromMinutes(2));
        activity.Saw();
        clock.Advance(TimeSpan.FromMinutes(11));
        var open = activity.Spans(clock.GetUtcNow()).Stalls;
        Assert.Equal(2, open.Count);
        Assert.Equal(T0.AddMinutes(14), open[1].Start);
    }

    [Fact]
    public void AModelRequest_DoesNotResetTheIdleWatchdog()
    {
        var clock = new ManualClock(T0);
        var activity = new RunActivity(clock);
        activity.Touch();
        clock.Advance(TimeSpan.FromMinutes(5));
        activity.Saw();
        Assert.Equal(T0, activity.Last);
    }

    [Fact]
    public async Task AForwardedModelRequest_ReportsActivity_WhenItGoesOut_AndAsItStreamsBack()
    {
        await using var f = await GatewayFixture.StartAsync();
        f.Credentials.Set(CredentialSlot.ClaudeSetupToken, FakeSecrets.SetupToken);
        var seen = 0;
        await using var gateway = f.Host.Start(new RunGatewaySpec(71, "Alpha/m71", GatewayFixture.AllModelVhosts, [], TimeSpan.FromHours(1))
        {
            RunKey = "run-71",
            ModelActivity = () => Interlocked.Increment(ref seen),
        }, new NoBus());
        using var http = ProxyClient.For(gateway);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://{Vhosts.Model}/v1/messages?beta=true")
        {
            Content = new StringContent("{\"model\":\"fake\",\"stream\":true}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gateway.Token.Value);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var atHeaders = Volatile.Read(ref seen);
        Assert.True(atHeaders >= 1, "told when the request went out");

        await using var stream = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        using var reader = new StreamReader(stream);
        await reader.ReadLineAsync(TestContext.Current.CancellationToken);
        f.Upstream.ReleaseStream();
        await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.True(Volatile.Read(ref seen) > atHeaders, "told again as the answer streamed back");
    }
}
