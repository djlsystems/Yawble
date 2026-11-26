using System.Text.Json.Nodes;
using Yawble.Core.Workflows.Engine;
using Yawble.Core.Workflows.Language;
using Yawble.Core.Workflows.Running;

namespace Yawble.Core.Workflows.Actions;

/// <summary>
/// The workflow engine's integration actions by kind ("api" and "email"): the catalog and a run's calls go to the provider
/// of the requirement's kind. A kind no provider serves has no actions, and its calls fail the step.
/// </summary>
public sealed class WorkflowActionKinds : IWorkflowActionsProvider
{
    private readonly IReadOnlyDictionary<string, IWorkflowActionsProvider> _kinds;

    public WorkflowActionKinds(IReadOnlyDictionary<string, IWorkflowActionsProvider> kinds) =>
        _kinds = new Dictionary<string, IWorkflowActionsProvider>(kinds, StringComparer.Ordinal);

    public IReadOnlyCollection<string> Kinds => _kinds.Keys.ToList();

    /// <summary>Each kind's catalog, read the first time that kind is asked about.</summary>
    public IWorkflowActionCatalog Catalog => new RoutedCatalog(kind => _kinds.TryGetValue(kind, out var p) ? p.Catalog : null);

    public IWorkflowActions For(WorkflowCaller caller) => new RoutedActions(_kinds, caller);

    /// <summary>The catalog a run is checked with: the run's own actions when they know (a team's), else the provider's.</summary>
    public static IWorkflowActionCatalog CatalogFor(IWorkflowActions actions, IWorkflowActionsProvider provider) =>
        actions as IWorkflowActionCatalog ?? provider.Catalog;

    private sealed class RoutedCatalog(Func<string, IWorkflowActionCatalog?> of) : IWorkflowActionCatalog, IWorkflowActionSignatures
    {
        private readonly Dictionary<string, IWorkflowActionCatalog?> _read = new(StringComparer.Ordinal);

        private IWorkflowActionCatalog? Of(string kind)
        {
            lock (_read)
            {
                if (!_read.TryGetValue(kind, out var c)) _read[kind] = c = of(kind);
                return c;
            }
        }

        public bool HasAction(string kind, string action) => Of(kind)?.HasAction(kind, action) ?? false;

        public bool IsChanging(string kind, string action) => Of(kind)?.IsChanging(kind, action) ?? true;

        public WorkflowActionSignature? Signature(string kind, string action) => (Of(kind) as IWorkflowActionSignatures)?.Signature(kind, action);
    }

    private sealed class RoutedActions : IWorkflowActions, IWorkflowActionCatalog, IWorkflowActionSignatures
    {
        private readonly IReadOnlyDictionary<string, IWorkflowActionsProvider> _kinds;
        private readonly Dictionary<string, IWorkflowActions> _actions = new(StringComparer.Ordinal);
        private readonly WorkflowCaller _caller;
        private readonly RoutedCatalog _catalog;

        public RoutedActions(IReadOnlyDictionary<string, IWorkflowActionsProvider> kinds, WorkflowCaller caller)
        {
            _kinds = kinds;
            _caller = caller;
            _catalog = new RoutedCatalog(kind => _kinds.TryGetValue(kind, out var p) ? CatalogFor(Of(kind)!, p) : null);
        }

        private IWorkflowActions? Of(string kind)
        {
            if (!_kinds.TryGetValue(kind, out var provider)) return null;
            lock (_actions)
            {
                if (!_actions.TryGetValue(kind, out var a)) _actions[kind] = a = provider.For(_caller);
                return a;
            }
        }

        public bool HasAction(string kind, string action) => _catalog.HasAction(kind, action);

        public bool IsChanging(string kind, string action) => _catalog.IsChanging(kind, action);

        public WorkflowActionSignature? Signature(string kind, string action) => _catalog.Signature(kind, action);

        public Task<JsonNode?> InvokeAsync(WorkflowActionCall call, CancellationToken ct) =>
            (Of(call.Kind) ?? throw new WorkflowActionException($"{call.Use}: no '{call.Kind}' integration is available to workflows")).InvokeAsync(call, ct);
    }
}
