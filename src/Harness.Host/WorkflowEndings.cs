using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// WHICH WORKFLOWS HAVE ENDED, PER TEAM, FOR THE QUIET-TEAM SWEEP. Read from the log once and then
/// only from the rows written since the last read, so a sweep that runs every few minutes does not
/// read every ending row of the log's history each time. One reader at a time: the sweep. A purge
/// or a reset that may have removed an ending row calls <see cref="Invalidate"/>, and the next read
/// starts from the beginning.
///
/// TWO SETS, BECAUSE THE SWEEP ASKS TWO DIFFERENT QUESTIONS OF ONE PAIR OF MESSAGE TYPES, and
/// collapsing them into one is how a `workflow.closed` reaches the reader it must not.
/// <see cref="OfTeam.EndedWorkflowCorrelations"/> answers IS THIS WORKFLOW OVER - either terminal
/// type - and is what the quiet-team finding reads: asking `workflow.completed` alone would make
/// that finding permanent for any workflow a person had closed. <see cref="OfTeam.DeclaredWorkflowCorrelations"/>
/// answers DID SOMEBODY CLAIM A DELIVERY - `workflow.completed` only - and is what the unpushed-clone
/// finding reads: a person closing a workflow makes no claim, and pointing a filesystem-touching
/// detector at abandoned work would report an unkept promise nobody made.
/// </summary>
public sealed class WorkflowEndings(IMessageLog messages)
{
    public sealed record OfTeam(HashSet<long> EndedWorkflowCorrelations, HashSet<long> DeclaredWorkflowCorrelations);

    private const int Page = 500;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, OfTeam> _state = new(StringComparer.OrdinalIgnoreCase);
    private long _after;
    private bool _reset;

    /// <summary>The next read starts from the beginning of the log.</summary>
    public void Invalidate()
    {
        lock (_state) _reset = true;
    }

    public async Task<IReadOnlyDictionary<string, OfTeam>> ReadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            lock (_state)
            {
                if (_reset)
                {
                    _state.Clear();
                    _after = 0;
                    _reset = false;
                }
            }

            while (true)
            {
                var page = await messages.ReadAfterAsync(
                    _after, [MessageTypes.WorkflowCompleted, MessageTypes.WorkflowClosed], Page, ct);
                if (page.Count == 0) break;

                foreach (var row in page)
                {
                    // A `workflow.closed` ROW'S TEAM COMES FROM ITS PAYLOAD, not its source: it is
                    // published by a person, whose id carries no `/`. `MessageTeam.Of` knows that.
                    var team = MessageTeam.Of(row);
                    if (team is null) continue;
                    if (!_state.TryGetValue(team, out var ofTeam))
                    {
                        ofTeam = new OfTeam([], []);
                        _state[team] = ofTeam;
                    }

                    ofTeam.EndedWorkflowCorrelations.Add(row.CorrelationId);

                    if (string.Equals(row.Type, MessageTypes.WorkflowCompleted, StringComparison.Ordinal))
                    {
                        ofTeam.DeclaredWorkflowCorrelations.Add(row.CorrelationId);
                    }
                }

                _after = page[^1].Seq;
            }

            return _state;
        }
        finally
        {
            _gate.Release();
        }
    }
}
