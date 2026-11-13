using System.Text.Json.Nodes;
using Yawble.Core.Ipc;
using Yawble.Core.Provenance;

namespace Yawble.Core.Updates;

/// <summary>
/// updates.status (also for cloud viewers), and what changes or installs: updates.check, updates.download,
/// updates.setAuto and updates.installer (the verified installer the window quits and runs), for the person in the app's
/// window on this computer only.
/// </summary>
public sealed class UpdateRpc(UpdateService updates)
{
    public static bool Handles(string method) => method.StartsWith("updates.", StringComparison.Ordinal);

    public const string LocalOnly = "the app is updated by the person, in its window on this computer";

    public async Task<JsonNode?> DispatchAsync(string method, JsonObject? p, CancellationToken ct)
    {
        if (method != "updates.status" && Actor.Current is { Via: not Actor.App }) throw new RpcException("forbidden", LocalOnly);
        try
        {
            return method switch
            {
                "updates.status" => updates.Status(),
                "updates.check" => await updates.CheckAsync(download: updates.Auto(), ct).ConfigureAwait(false),
                "updates.download" => await updates.DownloadAsync(ct).ConfigureAwait(false),
                "updates.setAuto" => SetAuto(p),
                "updates.installer" => new JsonObject { ["path"] = updates.InstallerToRun() },
                _ => throw new RpcException("unknown_method", $"no method named '{method}'"),
            };
        }
        catch (InvalidOperationException e)
        {
            throw new RpcException("unavailable", e.Message);
        }
    }

    private JsonNode SetAuto(JsonObject? p)
    {
        updates.SetAuto(p?["on"]?.GetValue<bool>() ?? throw new RpcException("bad_params", "on is required"));
        return updates.Status();
    }
}
