using System.Text.Json.Nodes;
using Yawble.Core.Ipc;
using Yawble.Core.Provenance;

namespace Yawble.Core.Storage;

/// <summary>ops.backups (list), ops.backupNow, ops.restore (stages it; applied at the next start), ops.restoreStatus. Restore is the person's alone: the window, never an agent, the Concierge, a trigger or a Cloud Link viewer.</summary>
public sealed class BackupRpc(BackupService backups)
{
    public static bool Handles(string method) => method is "ops.backups" or "ops.backupNow" or "ops.restore" or "ops.restoreStatus" or "ops.setKeepDaily" or "ops.keepDaily";

    public BackupService Service => backups;

    public static string? RestoreRefusal(Actor? actor) =>
        actor is { Via: Actor.App } or { Via: Actor.External, ApprovedBy: not null } ? null
            : "a backup can only be restored by the person, in the app's Settings on this computer";

    public static string? BackupNowRefusal(Actor? actor) =>
        actor is { Via: Actor.App or Actor.Concierge } or { Via: Actor.External, ApprovedBy: not null } ? null
            : "a backup can only be made from the app's window or by the Concierge on this computer";

    public Task<JsonNode?> DispatchAsync(string method, JsonObject? p, CancellationToken ct)
    {
        switch (method)
        {
            case "ops.backups":
                return Task.FromResult<JsonNode?>(new JsonArray(backups.List().Select(b => (JsonNode?)Item(b)).ToArray()));
            case "ops.restoreStatus":
            {
                // What the window shows after the restart: a restore still staged, and how the last one went. Never "restored"
                // unless the swap really happened at start.
                var last = backups.LastRestore;
                return Task.FromResult<JsonNode?>(new JsonObject
                {
                    ["pending"] = backups.PendingRestore,
                    ["last"] = last is null ? null : new JsonObject
                    {
                        ["backup"] = last.Backup, ["state"] = last.State, ["reason"] = last.Reason, ["at"] = last.At.ToString("O"),
                    },
                });
            }
            case "ops.keepDaily":
                return Task.FromResult<JsonNode?>(new JsonObject { ["keepDaily"] = backups.KeepDaily() });
            case "ops.backupNow":
                if (BackupNowRefusal(Actor.Current) is { } r1) throw new RpcException("forbidden", r1);
                return Task.FromResult<JsonNode?>(Item(backups.BackupNow("manual")));
            case "ops.setKeepDaily":
                if (RestoreRefusal(Actor.Current) is { } r3) throw new RpcException("forbidden", r3);
                backups.SetKeepDaily(p?["count"]?.GetValue<int>() ?? throw new RpcException("bad_params", "count is required"));
                backups.Prune();
                return Task.FromResult<JsonNode?>(new JsonObject { ["keepDaily"] = backups.KeepDaily() });
            case "ops.restore":
                if (RestoreRefusal(Actor.Current) is { } r2) throw new RpcException("forbidden", r2);
                if (p?["confirm"]?.GetValue<bool>() != true) throw new RpcException("bad_params", "restore must be confirmed by the person (confirm: true)");
                var name = p["backup"]?.GetValue<string>() ?? throw new RpcException("bad_params", "backup is required");
                try
                {
                    var staged = backups.Restore(name);
                    return Task.FromResult<JsonNode?>(new JsonObject { ["staged"] = Item(staged), ["restarting"] = true });
                }
                catch (Exception e) when (e is ArgumentException or IOException or InvalidOperationException)
                {
                    throw new RpcException("restore_failed", e.Message);
                }
            default:
                throw new RpcException("unknown_method", $"no method named '{method}'");
        }
    }

    private static JsonObject Item(BackupInfo b) => new()
    {
        ["name"] = b.Name,
        ["size"] = b.Size,
        ["reason"] = b.Reason,
        ["time"] = b.Time.ToString("O"),
    };
}
