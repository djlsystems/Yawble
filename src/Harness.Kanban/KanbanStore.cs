using Harness.Contracts;

namespace Harness.Kanban;

/// <summary>
/// Cached snapshot + rebuild from IMessageLog.ReadRangeAsync.
/// Holds the current board state and rebuilds when needed.
/// </summary>
public class KanbanStore
{
    private readonly IMessageLog _log;
    private KanbanBoard _board;
    private long _lastSeq;

    public KanbanStore(IMessageLog log)
    {
        _log = log;
        _lastSeq = 0;
        _board = new KanbanBoard(KanbanLanes.All, [], new KanbanFilter());
    }

    /// <summary>
    /// Get the current board with optional filtering.
    /// </summary>
    public async Task<KanbanBoard> GetBoardAsync(KanbanFilter? filter = null)
    {
        await RebuildIfNeededAsync(filter);
        return _board;
    }

    /// <summary>
    /// Get a specific card by id.
    /// </summary>
    public async Task<KanbanCard?> GetCardAsync(string cardId)
    {
        var board = await GetBoardAsync();
        return board.Cards.FirstOrDefault(c => c.Id == cardId);
    }

    /// <summary>
    /// Rebuild the board from the message log if needed.
    /// </summary>
    private async Task RebuildIfNeededAsync(KanbanFilter? filter = null)
    {
        try
        {
            var messages = await _log.ReadRangeAsync(0, int.MaxValue);

            _board = KanbanProjector.Project(messages, filter);
            _lastSeq = messages.Count > 0 ? messages[^1].Seq : 0;
        }
        catch (Exception)
        {
            // If rebuild fails, keep the last known board
            // In production, log this error
        }
    }
}
