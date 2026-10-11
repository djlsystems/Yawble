using System.Text.Json;
using Harness.Contracts;
using Harness.Host.Capacity;
using Microsoft.Extensions.Logging;

namespace Harness.Host;

/// <summary>
/// One running Concierge session as people see it: whose, on which worker, whether someone has it
/// open, what it last did, and when the idle window would end it.
/// </summary>
/// <param name="LastViewerAt">Null while a viewer has it open; otherwise when the last one left, or
/// when it started if nobody ever opened it.</param>
/// <param name="LastActivity"><c>started</c>, <c>output</c>, <c>typed</c>, <c>attached</c> or <c>call</c>.</param>
/// <param name="OutputPerMinute">Bytes printed in each of the session's last minutes, oldest first, the current minute last - what a floor is measured from.</param>
/// <param name="WouldEndAt">Null while a viewer has it open; otherwise the later of the last viewer and the last activity, plus the window. It is ended at the first sweep at or after this.</param>
/// <param name="Memory">Its process group on its worker at the last sample; null when not measured.</param>
public sealed record ConciergeSessionView(
    string User,
    string? Email,
    string? Worker,
    DateTimeOffset StartedAt,
    bool Viewer,
    DateTimeOffset? LastViewerAt,
    DateTimeOffset LastActivityAt,
    string LastActivity,
    int CallsInFlight,
    OutputFloor OutputFloor,
    IReadOnlyList<long> OutputPerMinute,
    DateTimeOffset? WouldEndAt,
    TerminalMemory? Memory);

/// <summary>A terminal's process group as its worker last measured it.</summary>
public sealed record TerminalMemory(long ResidentBytes, int Processes, DateTimeOffset SampledAt);

/// <summary>The session list <c>GET /api/concierge</c> and the doctor read.</summary>
public static class ConciergeSessionsView
{
    /// <summary>Every running session, oldest first, judged against <paramref name="window"/> at the store's clock.</summary>
    public static async Task<IReadOnlyList<ConciergeSessionView>> ReadAsync(
        ConciergeSessionStore store,
        TimeSpan window,
        Func<string, TerminalMeasured?> measured,
        Func<string, CancellationToken, Task<string?>> emailOf,
        CancellationToken ct)
    {
        var now = store.Clock.GetUtcNow();
        var views = new List<ConciergeSessionView>();

        foreach (var session in store.Sessions().OrderBy(s => s.Activity.StartedAt))
        {
            var idleSince = session.Attachment.IdleSince;
            var (lastAt, lastKind) = session.Activity.Last(now);

            views.Add(new ConciergeSessionView(
                session.Key.User,
                await emailOf(session.Key.User, ct),
                session.Worker,
                session.Activity.StartedAt,
                idleSince is null,
                idleSince,
                lastAt,
                lastKind,
                session.Activity.CallsInFlight,
                session.Activity.Floor,
                session.Activity.PerMinute(now),
                idleSince is { } since ? (since > lastAt ? since : lastAt) + window : null,
                MemoryOf(session, measured)));
        }

        return views;
    }

    /// <summary>The Concierge terminals on <paramref name="worker"/>, each with its memory when measured.</summary>
    public static IReadOnlyList<TerminalHold> TerminalsOn(
        ConciergeSessionStore store, WorkerId worker, Func<string, TerminalMeasured?> measured) =>
        [.. store.Sessions()
            .Where(session => session.Worker == worker.Value)
            .OrderBy(session => session.Activity.StartedAt)
            .Select(session => MemoryOf(session, measured) is { } memory
                ? new TerminalHold(session.Key.User, session.Activity.StartedAt, memory.ResidentBytes, memory.Processes, memory.SampledAt)
                : new TerminalHold(session.Key.User, session.Activity.StartedAt, null, null, null))];

    private static TerminalMemory? MemoryOf(ConciergeSession session, Func<string, TerminalMeasured?> measured) =>
        session.TerminalId is { } id && measured(id) is { } figures
            ? new TerminalMemory(figures.ResidentBytes, figures.Processes, figures.At)
            : null;
}

/// <summary>One session as <c>concierge-sessions.json</c> names it for the doctor.</summary>
public sealed record ConciergeSessionRecordItem(
    string User,
    string? Email,
    string? Worker,
    DateTimeOffset StartedAt,
    bool Viewer,
    DateTimeOffset? LastViewerAt,
    DateTimeOffset LastActivityAt,
    string LastActivity,
    DateTimeOffset? WouldEndAt,
    long? ResidentBytes);

/// <summary>
/// CONTROL'S RECORD OF THE RUNNING CONCIERGE SESSIONS, in <c>&lt;dataRoot&gt;/concierge-sessions.json</c>,
/// for <c>--doctor</c>, which is another process and cannot ask the store: written when a session
/// starts or ends and at every reaper sweep. The window it was judged against is in it, so "would end
/// at" reads as it did when written.
/// </summary>
public sealed record ConciergeSessionsRecord(DateTimeOffset RecordedAt, string Window, IReadOnlyList<ConciergeSessionRecordItem> Sessions)
{
    public const string FileName = "concierge-sessions.json";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static ConciergeSessionsRecord Of(DateTimeOffset at, TimeSpan window, IReadOnlyList<ConciergeSessionView> sessions) =>
        new(
            at,
            window.ToString("c", System.Globalization.CultureInfo.InvariantCulture),
            [.. sessions.Select(s => new ConciergeSessionRecordItem(
                s.User, s.Email, s.Worker, s.StartedAt, s.Viewer, s.LastViewerAt, s.LastActivityAt, s.LastActivity,
                s.WouldEndAt, s.Memory?.ResidentBytes))]);

    /// <summary>Never throws: a control that cannot record it still serves its sessions, and logs why.</summary>
    public void Write(string dataRoot, ILogger? log = null)
    {
        var path = Path.Combine(dataRoot, FileName);
        try
        {
            var temp = path + "." + Environment.CurrentManagedThreadId + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log?.LogWarning("Concierge: could not record the sessions in {Path}: {Error}", path, exception.Message);
        }
    }

    /// <summary>The last record, or null when there is none or it cannot be read.</summary>
    public static ConciergeSessionsRecord? Read(string dataRoot)
    {
        try
        {
            var path = Path.Combine(dataRoot, FileName);
            return File.Exists(path) ? JsonSerializer.Deserialize<ConciergeSessionsRecord>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
