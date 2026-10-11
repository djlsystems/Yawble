using System.Net.Http.Headers;
using System.Text;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Harness.Pty;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// A PLATFORM CALL UNDER A CONCIERGE SESSION'S OWN CREDENTIAL IS THAT SESSION'S ACTIVITY while it
/// runs, on every route and <c>/mcp</c>, and its count ends however the call ends. Over the
/// middleware alone, and over the real Host to show the reaper reads what a real call stamped.
/// </summary>
public sealed class ConciergeCallClockTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);
    private static readonly TimeSpan Tick = TimeSpan.FromTicks(1);

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"concierge-calls-{Guid.NewGuid():N}");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_request_under_the_sessions_own_credential_stamps_its_call_and_its_end()
    {
        var (clock, store) = await OpenAsync("user-1");
        var gate = new TaskCompletionSource();
        var context = Context(SessionCredential("user-1"));

        clock.Advance(TimeSpan.FromMinutes(5));
        var call = ConciergeCallClock.InvokeAsync(context, () => gate.Task, store);
        var began = clock.GetUtcNow();

        var activity = store.Sessions().Single().Activity;
        Assert.Equal(1, activity.CallsInFlight);
        Assert.Equal(ConciergeActivity.Call, activity.Last(began).Kind);

        clock.Advance(TimeSpan.FromMinutes(20));
        gate.SetResult();
        await call;

        var ended = clock.GetUtcNow();
        Assert.Equal(0, activity.CallsInFlight);
        Assert.Equal((ended, ConciergeActivity.Call), activity.Last(ended + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task A_call_under_another_principal_stamps_nothing()
    {
        var (clock, store) = await OpenAsync("user-1");
        var activity = store.Sessions().Single().Activity;
        clock.Advance(TimeSpan.FromMinutes(5));

        Principal[] others =
        [
            // The person themselves, in a browser.
            new("user-1", PrincipalKind.User, new HashSet<string>(), null),
            // A member's credential.
            new("alpha/Worker", PrincipalKind.Container, new HashSet<string>(), null),
            // A credential of that kind under another id, acting as the person.
            new("concierge-someone-else", PrincipalKind.TenantConcierge, new HashSet<string>(), "user-1"),
            // Another person's session.
            new(ConciergeLaunchFactory.PrincipalId("user-2"), PrincipalKind.TenantConcierge, new HashSet<string>(), "user-2"),
        ];

        foreach (var other in others)
        {
            await ConciergeCallClock.InvokeAsync(Context(other), () => Task.CompletedTask, store);
        }

        // An anonymous request too.
        await ConciergeCallClock.InvokeAsync(new DefaultHttpContext(), () => Task.CompletedTask, store);

        Assert.Equal((Start, ConciergeActivity.Started), activity.Last(clock.GetUtcNow()));
    }

    [Fact]
    public async Task A_call_that_throws_or_is_aborted_still_ends_its_in_flight_count()
    {
        var (clock, store) = await OpenAsync("user-1");
        var activity = store.Sessions().Single().Activity;

        var failing = new TaskCompletionSource();
        var thrown = ConciergeCallClock.InvokeAsync(Context(SessionCredential("user-1")), () => failing.Task, store);
        var aborting = new TaskCompletionSource();
        var aborted = ConciergeCallClock.InvokeAsync(Context(SessionCredential("user-1")), () => aborting.Task, store);
        Assert.Equal(2, activity.CallsInFlight);

        failing.SetException(new InvalidOperationException("the handler failed"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => thrown);
        Assert.Equal(1, activity.CallsInFlight);

        aborting.SetCanceled(Ct);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => aborted);
        Assert.Equal(0, activity.CallsInFlight);

        // And nothing keeps the session past a window from then.
        var last = clock.GetUtcNow();
        clock.Advance(Window);
        var reaped = Assert.Single(await store.ReapIdleAsync(Window, clock.GetUtcNow()));
        Assert.Equal(last, reaped.LastActivityAt);
    }

    [Fact]
    public async Task A_call_under_a_sessions_own_credential_keeps_that_session_from_being_reaped()
    {
        var clock = new ManualTime(Start);
        await using var host = Host(clock);
        var (user, key) = await PersonWithSessionAsync(host);
        var store = host.Services.GetRequiredService<ConciergeSessionStore>();

        clock.Advance(TimeSpan.FromMinutes(30));
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, key);
        (await client.GetAsync("/api/teams", Ct)).EnsureSuccessStatusCode();
        var callEnd = clock.GetUtcNow();

        Assert.Empty(await store.ReapIdleAsync(Window, callEnd + Window - Tick));
        Assert.True(store.Has(new ConciergeSessionKey(user)));

        var reaped = Assert.Single(await store.ReapIdleAsync(Window, callEnd + Window));
        Assert.Equal(callEnd, reaped.LastActivityAt);
        Assert.Equal(ConciergeActivity.Call, reaped.LastActivity);
    }

    [Fact]
    public async Task An_mcp_tool_call_stamps_the_session()
    {
        var clock = new ManualTime(Start);
        await using var host = Host(clock);
        var (_, key) = await PersonWithSessionAsync(host);
        var activity = host.Services.GetRequiredService<ConciergeSessionStore>().Sessions().Single().Activity;

        clock.Advance(TimeSpan.FromMinutes(10));
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(ApiKeyAuthenticationHandler.Header, key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        (await client.SendAsync(request, Ct)).EnsureSuccessStatusCode();

        Assert.Equal((Start.AddMinutes(10), ConciergeActivity.Call), activity.Last(clock.GetUtcNow()));
    }

    private async Task<(ManualTime Clock, ConciergeSessionStore Store)> OpenAsync(string user)
    {
        var clock = new ManualTime(Start);
        var store = new ConciergeSessionStore(
            new FakePtyEngine(),
            (_, _, _, _) => Task.FromResult(new PtySpec("fixture-cli", Path.GetTempPath())),
            (_, _) => Task.CompletedTask,
            clock);
        await store.AttachAsync(new ConciergeSessionKey(user), "", 80, 24, Ct);
        return (clock, store);
    }

    private static Principal SessionCredential(string user) =>
        new(ConciergeLaunchFactory.PrincipalId(user), PrincipalKind.TenantConcierge, ConciergeLaunchFactory.ConciergePermits, user);

    private static DefaultHttpContext Context(Principal principal) =>
        new() { User = PrincipalClaims.ToClaimsPrincipal(principal, "ApiKey") };

    /// <summary>The real Host, its Concierge sessions on fake terminals and the test's clock.</summary>
    private WebApplicationFactory<Program> Host(ManualTime clock)
    {
        Directory.CreateDirectory(_dataRoot);
        return new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton(new ConciergeSessionStore(
                new FakePtyEngine(),
                (_, _, _, _) => Task.FromResult(new PtySpec("fixture-cli", _dataRoot)),
                (_, _) => Task.CompletedTask,
                clock))));
    }

    /// <summary>A person with a session open, and the session's own credential, minted as the launch mints it.</summary>
    private static async Task<(string User, string Key)> PersonWithSessionAsync(WebApplicationFactory<Program> host)
    {
        var person = await host.Services.GetRequiredService<IUserStore>().CreateAsync("person@example.test", "correct horse battery", Ct);
        var key = await host.Services.GetRequiredService<IPrincipalStore>().MintAsync(
            ConciergeLaunchFactory.PrincipalId(person.Id), PrincipalKind.TenantConcierge, null,
            ConciergeLaunchFactory.ConciergePermits, ownerUserId: person.Id, ct: Ct);
        await host.Services.GetRequiredService<ConciergeSessionStore>().AttachAsync(new ConciergeSessionKey(person.Id), "", 80, 24, Ct);
        return (person.Id, key);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
