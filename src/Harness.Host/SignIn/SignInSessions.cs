using System.Collections.Concurrent;
using Harness.Contracts;
using Microsoft.Extensions.Logging;

namespace Harness.Host;

/// <summary>
/// THE LIVE SIGN-IN BROWSERS, CONTROL'S SIDE: who owns each, its idle clock, its save. A browser runs
/// on a worker (<c>WorkerSignInBrowsers</c>); its screen arrives here as a stream, the person's input
/// goes back outside the command queue, and Done asks the worker for the storage state, keeps it
/// through <see cref="ISignInRows"/> and stops the browser.
/// </summary>
/// <remarks>
/// <para>
/// ONE LIVE SESSION PER PERSON: a person who starts another REPLACES the first, which ends with
/// <see cref="ReplacedText"/> - the usual reason is the same person opening Sign in again in a new tab,
/// and a refusal would leave them locked out until the first one idled away.
/// </para>
/// <para>
/// IDLE: <see cref="IdleTimeout"/> after the last thing the person did (or the start) the browser is
/// stopped, with <see cref="IdleText"/>.
/// </para>
/// <para>
/// THE STATE IS NEVER ANSWERED OR LOGGED: a save answers the hosts it kept and the providers among
/// them, nothing else.
/// </para>
/// </remarks>
public sealed class SignInSessions(
    Func<IRunWorker> pick, WorkerStreams streams, ISignInRows rows, TimeProvider? clock = null, ILogger<SignInSessions>? log = null)
{
    /// <summary>How long a browser nobody uses stays open.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);

    /// <summary>How long a save waits for its worker's answer.</summary>
    public static readonly TimeSpan SaveBound = TimeSpan.FromSeconds(45);

    /// <summary>The viewport every sign-in browser has, in CSS pixels.</summary>
    public const int Width = 1280, Height = 800;

    public const string IdleText = "Nothing happened for 10 minutes, so the browser was closed. Nothing was saved.";
    public const string ReplacedText = "You opened Sign in somewhere else, so this browser was closed. Nothing was saved here.";
    public const string SavedText = "Signed in: the session was saved and the browser closed.";
    public const string CancelledText = "The browser was closed. Nothing was saved.";
    public const string NoWorkerText = "No worker is connected, so no browser can start. Try again once one is.";
    public const string RowInUseText = "Someone else is signing in to this entry right now. Try again once they are done.";

    private readonly ConcurrentDictionary<string, SignInSession> _byPerson = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<SignInBrowserSaved>> _saves = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>The live session of <paramref name="person"/>, if any.</summary>
    public SignInSession? Of(string person) => _byPerson.GetValueOrDefault(person);

    /// <summary>
    /// Starts a browser for <paramref name="person"/> on <paramref name="member"/>'s row, in the row's
    /// own profile and with the person's <paramref name="languages"/> and <paramref name="timeZone"/>,
    /// replacing any session that person has open. Throws <see cref="InvalidOperationException"/> with
    /// a sentence when no worker can take it, or another person is signing in to the same row: the
    /// row has one profile, and one browser at a time.
    /// </summary>
    public async Task<SignInSession> StartAsync(
        string person, ContainerId member, string field, string row, SignInEntry entry, CancellationToken ct = default,
        IReadOnlyList<string>? languages = null, string? timeZone = null)
    {
        var profile = SignInProfileKey.Row(member, field, row);
        if (_byPerson.Values.Any(s => s.Person != person && s.Profile == profile)) throw new InvalidOperationException(RowInUseText);

        IRunWorker worker;
        try
        {
            worker = pick();
        }
        catch (InvalidOperationException)
        {
            throw new InvalidOperationException(NoWorkerText);
        }

        var id = "sign-in:" + Guid.NewGuid().ToString("N");
        var stream = streams.Open(id, worker, whole: false);
        var session = new SignInSession(this, id, person, member, field, row, entry, worker, stream) { Profile = profile };

        if (_byPerson.TryGetValue(person, out var previous)) await previous.StopAsync(ReplacedText);
        _byPerson[person] = session;

        try
        {
            await worker.SendAsync(
                new StartSignInBrowser(id, new SignInBrowserLaunch(entry.SiteUrl, entry.Hosts, Width, Height, profile, languages, timeZone)), ct);
        }
        catch
        {
            _byPerson.TryRemove(new KeyValuePair<string, SignInSession>(person, session));
            stream.Dispose();
            throw;
        }

        session.Arm();
        log?.LogInformation("Sign-in browser {Session} started for {Member} row {Row} on worker {Worker}.", id, member, row, worker.Id);
        return session;
    }

    /// <summary>A save's answer from a worker: whoever waits for its request has it.</summary>
    public Task HandleAsync(WorkerEnvelope envelope, CancellationToken ct = default)
    {
        if (envelope.Event is SignInBrowserSaved saved && _saves.TryRemove(saved.Request, out var waiting)) waiting.TrySetResult(saved);
        return Task.CompletedTask;
    }

    internal void Forget(SignInSession session) =>
        _byPerson.TryRemove(new KeyValuePair<string, SignInSession>(session.Person, session));

    internal async Task<SignInBrowserSaved?> AskSaveAsync(SignInSession session, CancellationToken ct)
    {
        var request = Guid.NewGuid().ToString("N");
        var answer = new TaskCompletionSource<SignInBrowserSaved>(TaskCreationOptions.RunContinuationsAsynchronously);
        _saves[request] = answer;
        try
        {
            await session.Worker.SendAsync(new SaveSignInBrowser(session.Id, request), ct);
            var done = await Task.WhenAny(answer.Task, session.Worker.Closed, Task.Delay(SaveBound, _clock, ct));
            return done == answer.Task ? await answer.Task : null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _saves.TryRemove(request, out _);
        }
    }

    internal ISignInRows Rows => rows;

    internal TimeProvider Clock => _clock;

    internal ILogger? Log => log;
}

/// <summary>What a save did: kept (with the hosts kept and the providers among them), or why not.</summary>
public sealed record SignInSaveResult(bool Saved, IReadOnlyList<string> Hosts, IReadOnlyList<string> Providers, string? Error);

/// <summary>One person's live sign-in browser: its screen, its input, its idle clock, its save and its end.</summary>
public sealed class SignInSession
{
    private readonly SignInSessions _owner;
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource<string> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _over;
    private ITimer? _idle;
    private int _stopping;

    internal SignInSession(
        SignInSessions owner, string id, string person, ContainerId member, string field, string row, SignInEntry entry,
        IRunWorker worker, WorkerStream stream)
    {
        _owner = owner;
        Id = id;
        Person = person;
        Member = member;
        Field = field;
        Row = row;
        Entry = entry;
        Worker = worker;
        Stream = stream;
        _ = stream.Ended.ContinueWith(
            t => Ended(t.Result is SignInBrowserEnded ended ? ended.Reason : stream.Lost ?? "The browser closed."),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public string Id { get; }

    public string Person { get; }

    public ContainerId Member { get; }

    public string Field { get; }

    public string Row { get; }

    public SignInEntry Entry { get; }

    /// <summary>The row's browser profile on the workers (<see cref="SignInProfileKey"/>).</summary>
    public string Profile { get; init; } = "";

    public IRunWorker Worker { get; }

    /// <summary>The browser's screen (JPEG frames as bytes) and notices (JSON as text), in order; the last chunk is its end.</summary>
    public WorkerStream Stream { get; }

    /// <summary>Completes with the sentence the person reads when the session is over, however it ended.</summary>
    public Task<string> Over => _ended.Task;

    internal void Arm()
    {
        lock (_gate)
        {
            _idle ??= _owner.Clock.CreateTimer(_ => _ = StopAsync(SignInSessions.IdleText), null, SignInSessions.IdleTimeout, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>The person did something: the idle clock starts again.</summary>
    public void Touch()
    {
        lock (_gate) _idle?.Change(SignInSessions.IdleTimeout, Timeout.InfiniteTimeSpan);
    }

    /// <summary>One input of the person's, as JSON of <see cref="SignInBrowserInput"/>; it also resets the idle clock.</summary>
    public async ValueTask InputAsync(byte[] input, CancellationToken ct = default)
    {
        if (_ended.Task.IsCompleted || input.Length > 4 * SignInBrowserInput.MaxText) return;

        Touch();
        if (Worker is IRunWorkerInput relay) await relay.InputAsync(new StreamInput(Id, input), ct);
    }

    /// <summary>
    /// Done: reads the storage state from the worker, keeps it as the row's masked session secret, and
    /// stops the browser. The state itself is never part of the answer.
    /// </summary>
    public async Task<SignInSaveResult> SaveAsync(CredentialActor actor, CancellationToken ct = default)
    {
        if (_ended.Task.IsCompleted) return new SignInSaveResult(false, [], [], "The browser has already closed.");

        Touch();
        var saved = await _owner.AskSaveAsync(this, ct);
        if (saved is null) return new SignInSaveResult(false, [], [], "The worker did not answer the save. Nothing was saved.");
        if (saved.State is not { } state) return new SignInSaveResult(false, [], [], saved.Error ?? "Nothing could be read from the browser.");

        try
        {
            await _owner.Rows.SaveSessionAsync(Member, Field, Row, Entry.SessionColumn, state, actor, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The store's sentence, never the state.
            _owner.Log?.LogWarning("Sign-in session for {Member} row {Row} was not kept: {Message}", Member, Row, exception.Message);
            return new SignInSaveResult(false, [], [], "The session could not be kept: " + exception.Message);
        }

        _owner.Log?.LogInformation(
            "Sign-in session kept for {Member} row {Row} ({Hosts} hosts, providers {Providers}).", Member, Row, saved.Hosts.Count, string.Join(", ", saved.Providers));
        await StopAsync(SignInSessions.SavedText);
        return new SignInSaveResult(true, saved.Hosts, saved.Providers, null);
    }

    /// <summary>Stops the browser; <see cref="Over"/> completes with <paramref name="reason"/>.</summary>
    public async Task StopAsync(string reason = SignInSessions.CancelledText)
    {
        if (Interlocked.Exchange(ref _stopping, 1) == 1) return;

        Ended(reason);
        try
        {
            await Worker.SendAsync(new StopSignInBrowser(Id));
        }
        catch (InvalidOperationException)
        {
            // A worker that is gone has no browser left to stop.
        }

        Stream.Dispose();
    }

    private void Ended(string reason)
    {
        if (Interlocked.Exchange(ref _over, 1) == 1) return;

        // Gone from the person before Over completes, so whoever awaits it can start the next one.
        lock (_gate) _idle?.Dispose();
        _owner.Forget(this);
        _ended.TrySetResult(reason);
    }
}
