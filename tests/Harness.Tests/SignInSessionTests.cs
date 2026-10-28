using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// A PERSON'S SIGN-IN SESSION, CONTROL'S SIDE, over a worker that answers as a real one does. Done
/// keeps the storage state through the row seam as the row's session secret and answers only the
/// hosts; the browser closes after 10 minutes with nothing done; a person's second session replaces
/// the first; a save crosses a worker's connection only sealed.
/// </summary>
public sealed class SignInSessionTests
{
    private const string Person = "user-1";
    private const string Cookie = "session-cookie-value-7f3a";
    private static readonly ContainerId Member = new("team-a", "WebCredentials");
    private static readonly SignInEntry Entry = new("https://site.example", ["www.site.example"], "session");
    private static readonly string State = $$"""{"cookies":[{"name":"sid","value":"{{Cookie}}","domain":"site.example"}],"origins":[]}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Bed
    {
        public ManualTime Clock { get; } = new(DateTimeOffset.Parse("2026-10-28T12:00:00Z"));

        public WorkerStreams Streams { get; } = new();

        public Rows Rows { get; } = new();

        public FakeWorker Worker { get; }

        public SignInSessions Sessions { get; }

        public Bed()
        {
            Worker = new FakeWorker(this);
            Sessions = new SignInSessions(() => Worker, Streams, Rows, Clock);
        }
    }

    /// <summary>The row seam's test double: what was kept, by member, field, row and column.</summary>
    private sealed class Rows : ISignInRows
    {
        public ConcurrentDictionary<(ContainerId, string, string, string), string> Kept { get; } = new();

        public Task<SignInEntry?> EntryAsync(ContainerId member, string field, string row, CancellationToken ct = default) =>
            Task.FromResult<SignInEntry?>(Entry);

        public Task SaveSessionAsync(
            ContainerId member, string field, string row, string column, SignInStorageState state, CredentialActor actor, CancellationToken ct = default)
        {
            Kept[(member, field, row, column)] = state.Json;
            return Task.CompletedTask;
        }
    }

    /// <summary>A worker that runs browsers in name only: it answers a save with <see cref="State"/> and a stop with the end.</summary>
    private sealed class FakeWorker(Bed bed) : IRunWorker, IRunWorkerInput
    {
        private long _seq;

        public WorkerId Id { get; } = new("w1");

        public Task Closed { get; } = new TaskCompletionSource().Task;

        public ConcurrentQueue<ControlMessage> Sent { get; } = new();

        public ConcurrentQueue<StreamInput> Inputs { get; } = new();

        public async Task SendAsync(ControlMessage message, CancellationToken ct = default)
        {
            Sent.Enqueue(message);
            switch (message)
            {
                case SaveSignInBrowser save:
                    _ = Task.Run(() => bed.Sessions.HandleAsync(Envelope(new SignInBrowserSaved(
                        save.Session, save.Request, new SignInStorageState(State), ["site.example", "www.site.example", "accounts.idp.example"], ["accounts.idp.example"], null))));
                    break;

                case StopSignInBrowser stop:
                    await bed.Streams.HandleAsync(Envelope(new SignInBrowserEnded(stop.Session, "The browser closed.")));
                    break;
            }
        }

        public ValueTask InputAsync(StreamInput input, CancellationToken ct = default)
        {
            Inputs.Enqueue(input);
            return ValueTask.CompletedTask;
        }

        private WorkerEnvelope Envelope(WorkerEvent @event) => new(Id, Interlocked.Increment(ref _seq), @event);
    }

    [Fact]
    public async Task Done_keeps_the_state_as_the_rows_masked_session_and_answers_only_the_hosts()
    {
        var bed = new Bed();
        var session = await bed.Sessions.StartAsync(Person, Member, "entries", "site-admin", Entry, Ct);

        var start = Assert.IsType<StartSignInBrowser>(Assert.Single(bed.Worker.Sent));
        Assert.Equal("https://site.example", start.Launch.StartUrl);
        Assert.Equal(["www.site.example"], start.Launch.Hosts);

        var saved = await session.SaveAsync(new CredentialActor(Person, "person@example.test"), Ct);

        Assert.True(saved.Saved);
        Assert.Equal(["accounts.idp.example"], saved.Providers);
        Assert.Equal(State, bed.Rows.Kept[(Member, "entries", "site-admin", "session")]);
        // The answer the page gets holds the hosts, never the state.
        Assert.DoesNotContain(Cookie, JsonSerializer.Serialize(saved), StringComparison.Ordinal);
        Assert.DoesNotContain(Cookie, saved.ToString(), StringComparison.Ordinal);

        Assert.Equal(SignInSessions.SavedText, await session.Over);
        Assert.Contains(bed.Worker.Sent, m => m is StopSignInBrowser stop && stop.Session == session.Id);
        Assert.Null(bed.Sessions.Of(Person));
    }

    [Fact]
    public async Task Ten_minutes_with_nothing_done_closes_the_browser_and_input_starts_the_clock_again()
    {
        var bed = new Bed();
        var session = await bed.Sessions.StartAsync(Person, Member, "entries", "site-admin", Entry, Ct);

        bed.Clock.Advance(TimeSpan.FromMinutes(9));
        await session.InputAsync(Encoding.UTF8.GetBytes("""{"type":"mouse","event":"mouseMoved","x":1,"y":2}"""), Ct);
        bed.Clock.Advance(TimeSpan.FromMinutes(9));
        Assert.False(session.Over.IsCompleted);
        Assert.Single(bed.Worker.Inputs, i => i.Stream == session.Id);

        bed.Clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));

        Assert.Equal(SignInSessions.IdleText, await session.Over.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        await WaitFor(() => bed.Worker.Sent.Any(m => m is StopSignInBrowser stop && stop.Session == session.Id));
        Assert.Empty(bed.Rows.Kept);
        Assert.Null(bed.Sessions.Of(Person));

        // A save after the end keeps nothing.
        Assert.False((await session.SaveAsync(new CredentialActor(Person, "person@example.test"), Ct)).Saved);
        Assert.Empty(bed.Rows.Kept);
    }

    [Fact]
    public async Task A_persons_second_session_replaces_the_first_and_another_persons_is_left_alone()
    {
        var bed = new Bed();
        var first = await bed.Sessions.StartAsync(Person, Member, "entries", "site-admin", Entry, Ct);
        var someoneElse = await bed.Sessions.StartAsync("user-2", Member, "entries", "third-row", Entry, Ct);

        var second = await bed.Sessions.StartAsync(Person, Member, "entries", "other-row", Entry, Ct);

        Assert.Equal(SignInSessions.ReplacedText, await first.Over);
        Assert.Contains(bed.Worker.Sent, m => m is StopSignInBrowser stop && stop.Session == first.Id);
        Assert.Same(second, bed.Sessions.Of(Person));
        Assert.False(second.Over.IsCompleted);
        Assert.Same(someoneElse, bed.Sessions.Of("user-2"));
        Assert.False(someoneElse.Over.IsCompleted);
    }

    [Fact]
    public async Task A_row_has_one_browser_so_another_person_is_refused_while_the_same_person_replaces_their_own()
    {
        var bed = new Bed();
        var first = await bed.Sessions.StartAsync(Person, Member, "entries", "site-admin", Entry, Ct);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => bed.Sessions.StartAsync("user-2", Member, "entries", "SITE-ADMIN", Entry, Ct));
        Assert.Equal(SignInSessions.RowInUseText, refused.Message);
        Assert.False(first.Over.IsCompleted);

        var again = await bed.Sessions.StartAsync(Person, Member, "entries", "site-admin", Entry, Ct);
        Assert.Equal(SignInSessions.ReplacedText, await first.Over);
        Assert.Same(again, bed.Sessions.Of(Person));
    }

    [Fact]
    public async Task The_browser_starts_in_the_rows_own_profile_with_the_persons_languages_and_time_zone()
    {
        var bed = new Bed();
        await bed.Sessions.StartAsync(Person, Member, "entries", "site-admin", Entry, Ct, ["de-DE", "de"], "Europe/Berlin");
        await bed.Sessions.StartAsync(Person, Member, "entries", "Site-Admin", Entry, Ct);

        var launches = bed.Worker.Sent.OfType<StartSignInBrowser>().Select(s => s.Launch).ToList();
        Assert.Equal(["de-DE", "de"], launches[0].Languages);
        Assert.Equal("Europe/Berlin", launches[0].TimeZone);
        Assert.Equal(SignInProfileKey.Row(Member, "entries", "site-admin"), launches[0].Profile);
        Assert.Matches("^[0-9a-f]{32}/[0-9a-f]{32}$", launches[0].Profile!);

        // The same row, however its name is cased, is the same profile; another row or member is not.
        Assert.Equal(launches[0].Profile, launches[1].Profile);
        Assert.NotEqual(launches[0].Profile, SignInProfileKey.Row(Member, "entries", "other-row"));
        Assert.NotEqual(launches[0].Profile, SignInProfileKey.Row(new ContainerId(Member.Team, "Other"), "entries", "site-admin"));
        Assert.StartsWith(SignInProfileKey.Member(Member) + "/", launches[0].Profile);
    }

    [Fact]
    public async Task The_browser_ending_on_its_worker_ends_the_session_with_its_reason()
    {
        var bed = new Bed();
        var session = await bed.Sessions.StartAsync(Person, Member, "entries", "site-admin", Entry, Ct);

        await bed.Streams.HandleAsync(new WorkerEnvelope(bed.Worker.Id, 99, new SignInBrowserEnded(session.Id, WorkerSignInBrowsers.NoChromiumText)), Ct);

        Assert.Equal(WorkerSignInBrowsers.NoChromiumText, await session.Over.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.Null(bed.Sessions.Of(Person));
    }

    [Fact]
    public void A_saved_state_crosses_a_workers_connection_only_sealed_and_is_never_serialised_bare()
    {
        var key = WorkerFrameCodec.Key("the-worker-key", "control-nonce", "worker-nonce");
        var saved = new SignInBrowserSaved("sign-in:1", "r1", new SignInStorageState(State), ["site.example"], [], null);
        var frame = new EventFrame(new WorkerEnvelope(new WorkerId("w1"), 7, saved));

        var text = new WorkerFrameCodec(key).Write(frame);
        Assert.DoesNotContain(Cookie, text, StringComparison.Ordinal);

        var back = (SignInBrowserSaved)((EventFrame)new WorkerFrameCodec(key).Read(text)).Envelope.Event;
        Assert.Equal(State, back.State!.Json);

        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(saved));
        Assert.DoesNotContain(Cookie, saved.ToString(), StringComparison.Ordinal);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Timed out.");
            await Task.Delay(20, Ct);
        }
    }
}
