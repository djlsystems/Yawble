using System.Text.Json.Nodes;
using Yawble.Core.Scripts.Engine;
using Yawble.Core.Scripts.Language;
using Yawble.Core.Scripts.Running;

namespace Yawble.Core.Scripts.Actions;

/// <summary>
/// The script engine's integration actions by kind ("api" now; SC-3 adds "email" beside it): the catalog and a run's calls go to the provider
/// of the requirement's kind. A kind no provider serves has no actions, and its calls fail the step.
/// </summary>
public sealed class ScriptActionKinds : IScriptActionsProvider
{
    private readonly IReadOnlyDictionary<string, IScriptActionsProvider> _kinds;

    public ScriptActionKinds(IReadOnlyDictionary<string, IScriptActionsProvider> kinds) =>
        _kinds = new Dictionary<string, IScriptActionsProvider>(kinds, StringComparer.Ordinal);

    public IReadOnlyCollection<string> Kinds => _kinds.Keys.ToList();

    /// <summary>Each kind's catalog, read the first time that kind is asked about.</summary>
    public IScriptActionCatalog Catalog => new RoutedCatalog(kind => _kinds.TryGetValue(kind, out var p) ? p.Catalog : null);

    public IScriptActions For(ScriptCaller caller) => new RoutedActions(_kinds, caller);

    /// <summary>The catalog a run is checked with: the run's own actions when they know (a team's), else the provider's.</summary>
    public static IScriptActionCatalog CatalogFor(IScriptActions actions, IScriptActionsProvider provider) =>
        actions as IScriptActionCatalog ?? provider.Catalog;

    private sealed class RoutedCatalog(Func<string, IScriptActionCatalog?> of) : IScriptActionCatalog
    {
        private readonly Dictionary<string, IScriptActionCatalog?> _read = new(StringComparer.Ordinal);

        private IScriptActionCatalog? Of(string kind)
        {
            lock (_read)
            {
                if (!_read.TryGetValue(kind, out var c)) _read[kind] = c = of(kind);
                return c;
            }
        }

        public bool HasAction(string kind, string action) => Of(kind)?.HasAction(kind, action) ?? false;

        public bool IsChanging(string kind, string action) => Of(kind)?.IsChanging(kind, action) ?? true;
    }

    private sealed class RoutedActions : IScriptActions, IScriptActionCatalog
    {
        private readonly IReadOnlyDictionary<string, IScriptActionsProvider> _kinds;
        private readonly Dictionary<string, IScriptActions> _actions = new(StringComparer.Ordinal);
        private readonly ScriptCaller _caller;
        private readonly RoutedCatalog _catalog;

        public RoutedActions(IReadOnlyDictionary<string, IScriptActionsProvider> kinds, ScriptCaller caller)
        {
            _kinds = kinds;
            _caller = caller;
            _catalog = new RoutedCatalog(kind => _kinds.TryGetValue(kind, out var p) ? CatalogFor(Of(kind)!, p) : null);
        }

        private IScriptActions? Of(string kind)
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

        public Task<JsonNode?> InvokeAsync(ScriptActionCall call, CancellationToken ct) =>
            (Of(call.Kind) ?? throw new ScriptActionException($"{call.Use}: no '{call.Kind}' integration is available to scripts")).InvokeAsync(call, ct);
    }
}
