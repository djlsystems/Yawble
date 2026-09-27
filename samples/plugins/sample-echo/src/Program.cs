using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// SAMPLE ECHO: the deterministic proof-of-concept plugin member, protocol harness.member/1.
//
// In:  ONE JSON request on stdin - { protocol, member, causation, work: [{ seq, instruction, ... }],
//      config: { mode }, secrets: { token? }, ... }.
// Out: JSON Lines on stdout - progress, then blocked / handback when asked, then its declared event
//      `done` (plugin.sample-echo.done, with the output's length), then exactly one result.
//
// Each instruction is transformed by `config.mode`: `upper` (the default) or `reverse`. Prefixes:
//   fail:<words>      the run fails with those words (result ok:false, exit 1)
//   block:<words>     that item is blocked with those words; the others still run
//   handback:<words>  the words are handed back, then transformed like any other
//   sleep:<seconds>   waits, to exercise Stop and the idle clock
//   publish:<type>    also publishes an event of that type - for trying what the Host refuses
// The same input always gives the same output.

var request = JsonNode.Parse(Console.In.ReadToEnd()) ?? new JsonObject();
var work = request["work"]?.AsArray() ?? [];
var mode = (string?)request["config"]?["mode"] ?? "upper";

Emit(new JsonObject { ["t"] = "progress", ["status"] = $"transforming {work.Count} message(s) ({mode})" });

var results = new List<string>();

for (var i = 0; i < work.Count; i++)
{
    var text = (string?)work[i]?["instruction"] ?? work[i]?["payload"]?.ToJsonString() ?? "";

    if (Take(ref text, "fail:"))
    {
        Emit(new JsonObject { ["t"] = "result", ["ok"] = false, ["error"] = $"asked to fail: {text}" });
        return 1;
    }

    if (Take(ref text, "block:"))
    {
        Emit(new JsonObject { ["t"] = "blocked", ["item"] = i + 1, ["reason"] = text });
        continue;
    }

    if (Take(ref text, "sleep:"))
    {
        Thread.Sleep(TimeSpan.FromSeconds(double.Parse(text, System.Globalization.CultureInfo.InvariantCulture)));
        text = $"slept {text}s";
    }

    if (Take(ref text, "publish:"))
    {
        Emit(new JsonObject { ["t"] = "publish", ["type"] = text, ["payload"] = new JsonObject { ["length"] = text.Length } });
        text = $"published {text}";
    }

    if (Take(ref text, "handback:"))
    {
        Emit(new JsonObject { ["t"] = "handback", ["delivered"] = text });
    }

    results.Add(mode == "reverse" ? string.Concat(Enumerable.Reverse(text)) : text.ToUpperInvariant());
}

if ((string?)request["secrets"]?["token"] is { } token)
{
    results.Add($"token length {token.Length}");
}

var output = string.Join("\n", results);
Emit(new JsonObject { ["t"] = "publish", ["type"] = "done", ["payload"] = new JsonObject { ["length"] = output.Length } });
Emit(new JsonObject { ["t"] = "result", ["ok"] = true, ["output"] = output });
return 0;

static bool Take(ref string text, string prefix)
{
    if (!text.StartsWith(prefix, StringComparison.Ordinal)) return false;
    text = text[prefix.Length..].Trim();
    return true;
}

static void Emit(JsonObject record)
{
    Console.Out.Write(record.ToJsonString() + "\n");
    Console.Out.Flush();
}
