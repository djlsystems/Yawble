using System.Text.Json;

namespace Yawble.GuardCheck;

/// <summary>A read attempt. Mode: "open" (open + close, no bytes read), "list" (count entries) or "canary" (read a file the core planted).</summary>
public sealed record ReadProbe(string Id, string Category, string Path, string Mode, string? Canary);

/// <summary>A write attempt: create the file (never removed by the probe; the core looks for it on the host).</summary>
/// <summary>A write attempt. Mode: null (create a new file holding the nonce) or "open" (open an EXISTING file for
/// writing and close it again, writing nothing: the CLI's own program).</summary>
public sealed record WriteProbe(string Id, string Category, string Path, string? Mode = null);

public sealed record TcpProbe(string Id, string Category, string Host, int Port);

public sealed record DnsProbe(string Id, string Category, string Name);

/// <summary>The gateway attempts: a host the gateway must refuse, an allowlisted host (optional), the vendor hosts.</summary>
public sealed record GatewayProbe(string DeniedHost, string? AllowedHost, IReadOnlyList<string> VendorHosts);

/// <summary>
/// What the core asks the probe to try. Wire format (JSON, base64url on the command line), version 1:
/// { "v":1, "nonce", "reads":[{id,category,path,mode,canary?}], "writes":[{id,category,path}],
///   "tcp":[{id,category,host,port}], "dns":[{id,category,name}], "gateway":{deniedHost,allowedHost?,vendorHosts[]}|null,
///   "hostPid":n|null, "scanDirs":[path], "keychainServices":[name] }
/// </summary>
public sealed record ProbePlan(
    string Nonce,
    IReadOnlyList<ReadProbe> Reads,
    IReadOnlyList<WriteProbe> Writes,
    IReadOnlyList<TcpProbe> Tcp,
    IReadOnlyList<DnsProbe> Dns,
    GatewayProbe? Gateway,
    int? HostPid,
    IReadOnlyList<string> ScanDirs,
    IReadOnlyList<string> KeychainServices)
{
    public const int Version = 1;

    public static ProbePlan Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.GetProperty("v").GetInt32() != Version) throw new InvalidOperationException($"plan version {r.GetProperty("v")} is not {Version}");
        static string S(JsonElement e, string name) => e.GetProperty(name).GetString() ?? throw new InvalidOperationException($"{name} is null");
        static string? O(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static IEnumerable<JsonElement> A(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

        GatewayProbe? gateway = null;
        if (r.TryGetProperty("gateway", out var g) && g.ValueKind == JsonValueKind.Object)
            gateway = new GatewayProbe(S(g, "deniedHost"), O(g, "allowedHost"), A(g, "vendorHosts").Select(v => v.GetString()!).ToList());
        return new ProbePlan(
            S(r, "nonce"),
            A(r, "reads").Select(e => new ReadProbe(S(e, "id"), S(e, "category"), S(e, "path"), S(e, "mode"), O(e, "canary"))).ToList(),
            A(r, "writes").Select(e => new WriteProbe(S(e, "id"), S(e, "category"), S(e, "path"), O(e, "mode"))).ToList(),
            A(r, "tcp").Select(e => new TcpProbe(S(e, "id"), S(e, "category"), S(e, "host"), e.GetProperty("port").GetInt32())).ToList(),
            A(r, "dns").Select(e => new DnsProbe(S(e, "id"), S(e, "category"), S(e, "name"))).ToList(),
            gateway,
            r.TryGetProperty("hostPid", out var pid) && pid.ValueKind == JsonValueKind.Number ? pid.GetInt32() : null,
            A(r, "scanDirs").Select(v => v.GetString()!).ToList(),
            A(r, "keychainServices").Select(v => v.GetString()!).ToList());
    }
}

public static class Base64Url
{
    public static byte[] Decode(string s)
    {
        var b = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b.PadRight(b.Length + (4 - b.Length % 4) % 4, '='));
    }
}

/// <summary>One JSON line per attempt; written and flushed one at a time so a killed probe still leaves what it found.</summary>
public sealed class ProbeOutput(Stream stream)
{
    private readonly Lock _lock = new();

    public void Result(string id, string category, string outcome, string evidence, Action<Utf8JsonWriter>? extra = null)
    {
        lock (_lock)
        {
            using (var w = new Utf8JsonWriter(stream))
            {
                w.WriteStartObject();
                w.WriteString("id", id);
                w.WriteString("category", category);
                w.WriteString("outcome", outcome);
                w.WriteString("evidence", evidence);
                extra?.Invoke(w);
                w.WriteEndObject();
            }
            stream.WriteByte((byte)'\n');
            stream.Flush();
        }
    }
}
