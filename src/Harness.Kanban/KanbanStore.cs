using Harness.Contracts;

namespace Harness.Kanban;

/// <summary>
/// THE ONE KANBAN PROJECTION, shared by every reader: the board routes, the <c>kanban</c> and
/// <c>status</c> tools, every run end (the loose ends and the worktrees a declaration settles),
/// Needs you and the stranded-item read.
///
/// <para>
/// INCREMENTAL, UNDER A LOCK. The rows are appended from the log's position the last read reached
/// (<see cref="IMessageLog.HighestSeqAsync"/> says whether anything moved, <see cref="IMessageLog.ReadRangeAsync"/>
/// brings the rest), never replayed from zero on every read; a row that does not move the board
/// keeps only what the open-workflow read needs of it (its seq and workflow), not its payload.
/// The cards are projected once per log position and floors, and a filter is applied to those
/// cards, so two readers asking for two teams at once each get their own team - the shared field a
/// caller's filter was written into gave one reader the other's board.
/// </para>
///
/// <para>
/// FROM EACH TEAM'S FLOOR, NOT FROM ZERO: a team made again under its predecessor's id would
/// otherwise inherit its cards, and a run end deciding which worktrees are settled would act on them.
/// </para>
///
/// <para>
/// A READ THAT FAILS THROWS. The last board is not served in its place: a stale board let a worktree
/// removal treat a live card's tree as free. The next read starts again from the rows held.
/// </para>
///
/// <para>
/// THE LOG'S ONE DELETE IS TOLD HERE: <see cref="Invalidate"/> after a retention pass or a Reset that
/// deleted memory drops the rows held, since nothing in a seq-ordered append says a row went.
/// </para>
/// </summary>
public sealed class KanbanStore
{
    /// <summary>How many rows one catch-up read takes.</summary>
    public const int ReadPage = 2_000;

    private readonly IMessageLog _log;
    private readonly Func<string, long> _floorFor;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Every row held, oldest first, with its team as <see cref="MessageTeam.Of"/> reads it, read once.</summary>
    private readonly List<(Message Row, string? Team)> _rows = [];

    private long _seq;
    private bool _invalidated;
    private Projection? _projected;

    private sealed record Projection(long Seq, string Floors, IReadOnlyList<Message> Rows, IReadOnlyList<KanbanCard> Cards);

    /// <param name="log">The message log.</param>
    /// <param name="floorFor">A team's floor: the seq its current incarnation starts after. Null is 0 for every team.</param>
    public KanbanStore(IMessageLog log, Func<string, long>? floorFor = null)
    {
        _log = log;
        _floorFor = floorFor ?? (_ => 0);
    }

    /// <summary>
    /// The rows the board is projected from: every row above its team's floor, oldest first. A row
    /// that does not move the board carries an empty payload.
    /// </summary>
    public async Task<IReadOnlyList<Message>> RowsAsync(CancellationToken ct = default) =>
        (await ProjectedAsync(ct)).Rows;

    /// <summary>The board, narrowed by <paramref name="filter"/>.</summary>
    public async Task<KanbanBoard> GetBoardAsync(KanbanFilter? filter = null, CancellationToken ct = default)
    {
        filter ??= new KanbanFilter();
        var projected = await ProjectedAsync(ct);
        return new KanbanBoard(KanbanLanes.All, KanbanProjector.Filter(projected.Cards, filter), filter);
    }

    /// <summary>One card by id, from any team, or null.</summary>
    public async Task<KanbanCard?> GetCardAsync(string cardId, CancellationToken ct = default) =>
        (await ProjectedAsync(ct)).Cards.FirstOrDefault(card => string.Equals(card.Id, cardId, StringComparison.Ordinal));

    /// <summary>Forgets the rows held, so the next read starts from the log's first row again.</summary>
    public void Invalidate() => _invalidated = true;

    private async Task<Projection> ProjectedAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_invalidated)
            {
                _invalidated = false;
                _rows.Clear();
                _seq = 0;
                _projected = null;
            }

            var highest = await _log.HighestSeqAsync(ct);
            while (_seq < highest)
            {
                var page = await _log.ReadRangeAsync(_seq, ReadPage, ct);
                if (page.Count == 0) break;
                foreach (var row in page) _rows.Add((Keep(row), MessageTeam.Of(row)));
                _seq = page[^1].Seq;
                if (page.Count < ReadPage) break;
            }

            var floors = Floors();
            if (_projected is { } held && held.Seq == _seq && held.Floors == floors) return held;

            var rows = _rows
                .Where(entry => entry.Team is null || entry.Row.Seq > _floorFor(entry.Team))
                .Select(entry => entry.Row)
                .ToList();
            _projected = new Projection(_seq, floors, rows, KanbanProjector.ProjectAll(rows));
            return _projected;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A row that moves the board whole; any other without its payload.</summary>
    private static Message Keep(Message row) =>
        KanbanProjector.MovesTheBoard(row.Type) || row.Payload.Length == 0 ? row : row with { Payload = string.Empty };

    /// <summary>The floors of every team among the rows, as one key: a floor that moved re-projects.</summary>
    private string Floors()
    {
        var teams = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _rows)
        {
            if (entry.Team is { } team) teams.Add(team);
        }

        return string.Join(";", teams.Select(team => $"{team}={_floorFor(team)}"));
    }
}
