using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Yawble.Core.Provenance;

/// <summary>What one model request used, as the provider reported it (0015_provenance.sql usage_events).</summary>
public sealed record UsageReading(string? Model, long? InputTokens, long? OutputTokens, long? CacheReadTokens, long? CacheWriteTokens, string? Usage);

/// <summary>
/// Reads a model response's own usage figures as the gateway relays it, without holding the response up: the bytes are
/// copied aside (up to a cap) and read once the response has ended. Understands what the agent CLIs' providers send:
/// <list type="bullet">
/// <item>Anthropic Messages (Claude): a JSON body, or SSE where message_start carries the input and cache counts and
///   message_delta the running output count.</item>
/// <item>OpenAI-style Responses and Chat Completions (Codex, Grok, Copilot): a JSON body, or SSE whose last usage object
///   (response.completed, or the final chunk) carries the totals.</item>
/// </list>
/// Each figure is the largest seen across the response's events: running counts only grow, and a figure reported once
/// is kept. A response it cannot read (too large, compressed in a way it does not know, no usage) gives no figures but
/// is still recorded, so a gap is visible rather than silent.
/// </summary>
public sealed class UsageMeter(string? contentEncoding, int cap = 4 * 1024 * 1024)
{
    private readonly MemoryStream _body = new();
    private bool _overflow;

    public void Add(ReadOnlySpan<byte> chunk)
    {
        if (_overflow) return;
        if (_body.Length + chunk.Length > cap)
        {
            _overflow = true;
            _body.SetLength(0);
            return;
        }
        _body.Write(chunk);
    }

    public UsageReading Read()
    {
        if (_overflow) return new UsageReading(null, null, null, null, null, "\"not read: the response was larger than the meter keeps\"");
        string text;
        try
        {
            text = Decode(_body.ToArray(), contentEncoding);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or NotSupportedException)
        {
            return new UsageReading(null, null, null, null, null, $"\"not read: {e.GetType().Name}\"");
        }
        return Parse(text);
    }

    private static string Decode(byte[] bytes, string? encoding)
    {
        Stream? decoder = encoding?.Trim().ToLowerInvariant() switch
        {
            null or "" or "identity" => null,
            "gzip" => new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress),
            "br" => new BrotliStream(new MemoryStream(bytes), CompressionMode.Decompress),
            "deflate" => new ZLibStream(new MemoryStream(bytes), CompressionMode.Decompress),
            var other => throw new NotSupportedException(other),
        };
        if (decoder is null) return Encoding.UTF8.GetString(bytes);
        using (decoder)
        using (var reader = new StreamReader(decoder, Encoding.UTF8))
            return reader.ReadToEnd();
    }

    /// <summary>The usage in a response body: one JSON document, or the data lines of an SSE stream.</summary>
    public static UsageReading Parse(string text)
    {
        var acc = new Accumulator();
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            acc.Visit(TryParse(trimmed));
        }
        else
        {
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = line[5..].Trim();
                if (data.Length == 0 || data == "[DONE]") continue;
                acc.Visit(TryParse(data));
            }
        }
        return acc.Reading();
    }

    private static JsonObject? TryParse(string json)
    {
        try { return JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private sealed class Accumulator
    {
        private string? _model;
        private long? _in, _out, _cacheRead, _cacheWrite;
        private readonly JsonArray _seen = [];

        public void Visit(JsonObject? doc)
        {
            if (doc is null) return;
            // Where the providers put the model and the usage: the root (JSON bodies, chat chunks), "message"
            // (Anthropic message_start), "response" (OpenAI Responses events).
            foreach (var holder in new[] { doc, doc["message"] as JsonObject, doc["response"] as JsonObject })
            {
                if (holder is null) continue;
                if (_model is null && holder["model"] is JsonValue m && m.TryGetValue<string>(out var model) && model.Length > 0) _model = model;
                if (holder["usage"] is JsonObject usage) Take(usage);
            }
        }

        private void Take(JsonObject u)
        {
            if (_seen.Count < 8) _seen.Add(u.DeepClone());
            Max(ref _in, Num(u, "input_tokens") ?? Num(u, "prompt_tokens"));
            Max(ref _out, Num(u, "output_tokens") ?? Num(u, "completion_tokens"));
            Max(ref _cacheRead, Num(u, "cache_read_input_tokens")
                ?? Num(u["input_tokens_details"] as JsonObject, "cached_tokens") ?? Num(u["prompt_tokens_details"] as JsonObject, "cached_tokens"));
            Max(ref _cacheWrite, Num(u, "cache_creation_input_tokens"));
        }

        private static void Max(ref long? into, long? value)
        {
            if (value is { } v && (into is null || v > into)) into = v;
        }

        private static long? Num(JsonObject? o, string name) =>
            o?[name] is JsonValue v && v.TryGetValue<long>(out var n) ? n : null;

        public UsageReading Reading() => new(_model, _in, _out, _cacheRead, _cacheWrite,
            _seen.Count == 0 ? null : (_seen.Count == 1 ? _seen[0]!.ToJsonString() : _seen.ToJsonString()));
    }
}
