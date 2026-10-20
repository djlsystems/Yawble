using Harness.Contracts;

namespace Harness.Kanban;

/// <summary>
/// THE ONE KANBAN PROJECTION, shared by every reader: the board routes, the <c>kanban</c> and
/// <c>status</c> tools, every run end (the loose ends and the worktrees a declaration settles),
/// Needs you and the stranded-item read.
///
/// <para>
/// INCREMENTAL, UNDER A LOCK, AND NOTHING OF THE LOG IS KEPT. The projector's card state is
/// applied row by row from the log's position the last read reached (<see cref="IMessageLog.HighestSeqAsync"/>
/// says whether anything moved, <see cref="IMessageLog.ReadRangeAsync"/> brings the rest); no row
/// is held once applied, only each workflow's latest seq for the open-workflow read. The cards are
/// built once per log position and a filter is applied to those cards, so two readers asking for
/// two teams at once each get their own team - the shared field a caller's filter was written into
/// gave one reader the other's board.
/// </para>
///
/// <para>
/// FROM EACH TEAM'S FLOOR, NOT FROM ZERO: a team made again under its predecessor's id would
/// otherwise inherit its cards, and a run end deciding which worktrees are settled would act on
/// them. A floor is asked once per team per read, never per row; a floor that rose (a team made
/// again, a Reset) rebuilds the projection from the log, which is rare.
/// </para>
///
/// <para>
/// A READ THAT FAILS THROWS. The last board is not served in its place: a stale board let a worktree
/// removal treat a live card's tree as free. The next read starts again from the position held.
/// </para>
///
/// <para>
/// THE LOG'S ONE DELETE IS TOLD HERE: <see cref="Invalidate"/> after a retention pass or a Reset that
/// deleted memory rebuilds from the log, since nothing in a seq-ordered append says a row went.
/// </para>
/// </summary>
public sealed class KanbanStore
{
    /// <summary>How many rows one catch-up read takes.</summary>
    public const int ReadPage = 2_000;

    private readonly IMessageLog _log;
    private readonly Func<string, long> _floorFor;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private KanbanProjector.Projection _projection = new();
    private readonly Dictionary<long, long> _latestByWorkflow = [];
    private readonly HashSet<string> _teams = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, long> _floors = new(StringComparer.OrdinalIgnoreCase);
    private long _seq;
    private volatile bool _invalidated;
    private (long Seq, IReadOnlyList<KanbanCard> Cards)? _cards;

    /// <param name="log">The message log.</param>
    /// <param name="floorFor">A team's floor: the seq its current incarnation starts after. Null is 0 for every team.</param>
    public KanbanStore(IMessageLog log, Func<string, long>? floorFor = null)
    {
        _log = log;
        _floorFor = floorFor ?? (_ => 0);
    }

    /// <summary>The board, narrowed by <paramref name="filter"/>.</summary>
    public async Task<KanbanBoard> GetBoardAsync(KanbanFilter? filter = null, CancellationToken ct = default)
    {
        filter ??= new KanbanFilter();
        var cards = await CardsAsync(ct);
        return new KanbanBoard(KanbanLanes.All, KanbanProjector.Filter(cards, filter), filter);
    }

    /// <summary>One card by id, from any team, or null.</summary>
    public async Task<KanbanCard?> GetCardAsync(string cardId, CancellationToken ct = default) =>
        (await CardsAsync(ct)).FirstOrDefault(card => string.Equals(card.Id, cardId, StringComparison.Ordinal));

    /// <summary>One card with its trail, or null when there is none or <paramref name="filter"/> leaves it out.</summary>
    public async Task<KanbanCardDetail?> GetCardDetailAsync(string cardId, KanbanFilter filter, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await RefreshLocked(ct);
            return _projection.Detail(cardId, filter);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Each workflow's latest seq among the rows projected: what the open-workflow read joins on.</summary>
    public async Task<IReadOnlyDictionary<long, long>> LatestSeqByWorkflowAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await RefreshLocked(ct);
            return new Dictionary<long, long>(_latestByWorkflow);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forgets what was projected, so the next read rebuilds from the log's first row.</summary>
    public void Invalidate() => _invalidated = true;

    private async Task<IReadOnlyList<KanbanCard>> CardsAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await RefreshLocked(ct);
            if (_cards is { } held && held.Seq == _seq) return held.Cards;

            var cards = _projection.Cards();
            _cards = (_seq, cards);
            return cards;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RefreshLocked(CancellationToken ct)
    {
        if (_invalidated)
        {
            _invalidated = false;
            Reset();
        }

        // ONE FLOOR PER TEAM PER READ. A floor that rose since the rows were applied means rows
        // below it are in the projection: start again from the log.
        var floors = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var team in _teams) floors[team] = _floorFor(team);
        if (_seq > 0 && floors.Any(pair => !_floors.TryGetValue(pair.Key, out var was) || was != pair.Value)) Reset();

        var highest = await _log.HighestSeqAsync(ct);
        while (_seq < highest)
        {
            var page = await _log.ReadRangeAsync(_seq, ReadPage, ct);
            if (page.Count == 0) break;

            foreach (var row in page) Apply(row, floors);
            _seq = page[^1].Seq;
            if (page.Count < ReadPage) break;
        }

        _floors = floors;
    }

    private void Apply(Message row, Dictionary<string, long> floors)
    {
        var team = MessageTeam.Of(row);
        if (team is not null)
        {
            if (!floors.TryGetValue(team, out var floor))
            {
                floor = _floorFor(team);
                floors[team] = floor;
            }

            _teams.Add(team);
            if (row.Seq <= floor) return;
        }

        if (!_latestByWorkflow.TryGetValue(row.CorrelationId, out var latest) || row.Seq > latest)
            _latestByWorkflow[row.CorrelationId] = row.Seq;

        _projection.Apply(row);
    }

    private void Reset()
    {
        _projection = new KanbanProjector.Projection();
        _latestByWorkflow.Clear();
        _teams.Clear();
        _seq = 0;
        _cards = null;
    }
}
