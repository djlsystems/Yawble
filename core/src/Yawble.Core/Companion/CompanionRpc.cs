using System.Text.Json.Nodes;
using Yawble.Core.Ipc;
using Yawble.Core.Provenance;

namespace Yawble.Core.Companion;

/// <summary>
/// The window's companion.* methods: the paired clients, the pairings and action requests waiting for the person, their
/// decisions, revocation, and where the control endpoint listens. The person at this computer's window only (Actor Via App):
/// a Cloud Link viewer (RemoteAccess also refuses these by name), the Concierge, a trigger, an agent and the companion
/// itself are all refused. Nothing here ever carries a key, a key hash or a confirmation token.
/// </summary>
public sealed class CompanionRpc(CompanionService service, Func<JsonObject> status)
{
    public const string WindowOnly = "yawble-companion is managed in the app's window on this computer";

    public static bool Handles(string m) => m.StartsWith("companion.", StringComparison.Ordinal);

    public async Task<JsonNode?> DispatchAsync(string method, JsonObject? p, CancellationToken ct)
    {
        if (Actor.Current?.Via != Actor.App) throw new RpcException("forbidden", WindowOnly);
        switch (method)
        {
            case "companion.list":
                return service.ListForWindow();
            case "companion.status":
                return status();
            case "companion.decidePairing":
            {
                CompanionLevel? level = null;
                if (p?["level"] is { } l)
                {
                    if (!CompanionLevels.TryParse(Text(l), out var parsed)) throw new RpcException("bad_params", "the level is read, act or destructive");
                    level = parsed;
                }
                service.DecidePairing(Id(p), Approve(p), level);
                return service.ListForWindow();
            }
            case "companion.decideAction":
            {
                return StatusJson(await service.DecideActionAsync(Id(p), Approve(p), ct).ConfigureAwait(false));
            }
            case "companion.actionStatus":
            {
                // A read only: decides nothing (the window's way to learn how an approved run ended).
                var s = service.StatusForWindow(Id(p)) ?? throw new RpcException("not_found", $"there is no request '{Id(p)}'");
                return StatusJson(s);
            }
            case "companion.revoke":
            {
                var name = Text(p?["name"]) is { Length: > 0 } n ? n : throw new RpcException("bad_params", "name is required");
                return new JsonObject { ["revoked"] = service.Revoke(name) };
            }
            default:
                throw new RpcException("unknown_method", $"no method named '{method}'");
        }
    }

    private static JsonObject StatusJson(ActionStatus s) => new()
    {
        ["id"] = s.Id,
        ["state"] = s.State.ToString().ToLowerInvariant(), // pending, running, done, failed, denied, expired, changed
        ["plan"] = s.Plan,
        ["result"] = s.Result,
        ["nextId"] = s.NextId,
    };

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string Id(JsonObject? p) => Text(p?["id"]) is { Length: > 0 } id ? id : throw new RpcException("bad_params", "id is required");

    private static bool Approve(JsonObject? p) =>
        p?["approve"] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : throw new RpcException("bad_params", "approve (true or false) is required");
}
