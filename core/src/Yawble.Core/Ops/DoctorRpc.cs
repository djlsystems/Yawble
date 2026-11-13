using System.Text.Json.Nodes;
using Yawble.Core.Ipc;

namespace Yawble.Core.Ops;

/// <summary>
/// ops.doctor: the list of checks. ops.doctorFix {fix, confirm?, expect?}: runs one fix; the ones that remove or archive
/// answer needsConfirm (with the targets) until called again with confirm true. With expect (the targets the person saw),
/// a run whose targets moved does nothing and answers changed. Neither is in the Cloud Link's allowed list.
/// </summary>
public sealed class DoctorRpc(DoctorService doctor)
{
    public static bool Handles(string method) => method is "ops.doctor" or "ops.doctorFix";

    public async Task<JsonNode?> DispatchAsync(string method, JsonObject? p, CancellationToken ct)
    {
        if (method == "ops.doctor")
        {
            var checks = await doctor.RunAsync(ct).ConfigureAwait(false);
            return new JsonObject
            {
                ["checks"] = new JsonArray(checks.Select(c => (JsonNode?)new JsonObject
                {
                    ["id"] = c.Id,
                    ["title"] = c.Title,
                    ["status"] = c.Status,
                    ["detail"] = c.Detail,
                    ["fix"] = Fix(c.Fix),
                    ["fixes"] = new JsonArray((c.Fixes ?? []).Select(Fix).ToArray()),
                }).ToArray()),
            };
        }
        var fix = p?["fix"]?.GetValue<string>() is { Length: > 0 } f ? f : throw new RpcException("bad_params", "fix is required");
        var confirm = p?["confirm"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        var expect = p?["expect"] is JsonArray e ? e.Select(n => n?.GetValue<string>() ?? "").ToList() : null;
        var result = await doctor.RunFixAsync(fix, confirm, ct, expect).ConfigureAwait(false);
        var o = new JsonObject { ["done"] = result.Done, ["needsConfirm"] = result.NeedsConfirm, ["message"] = result.Message };
        if (result.Targets is { } t) o["targets"] = new JsonArray(t.Select(x => (JsonNode?)x).ToArray());
        if (result.Changed) o["changed"] = true;
        return o;
    }

    private static JsonNode? Fix(DoctorFix? f) => f is null ? null : new JsonObject
    {
        ["id"] = f.Id, ["label"] = f.Label, ["kind"] = f.Kind, ["confirm"] = f.Confirm,
    };
}
