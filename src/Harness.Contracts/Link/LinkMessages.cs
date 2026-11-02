using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harness.Contracts.Link;

// ---------------------------------------------------------------------------------------------
// THE MANAGEMENT LINK'S WIRE FORMAT. An instance can be attached to a management service: it spends a
// one-time token for a credential (attach), then holds one outbound WebSocket to the service and
// reports its status over it. These records and docs/management-link.md are the contract a service
// is built against; a field renamed here is a break for every service. Version 1 carries status only:
// the service cannot ask the instance to do anything, and a frame type the instance does not know -
// including every "relay." type, reserved for a later version - parses to null and is ignored.
// ---------------------------------------------------------------------------------------------

/// <summary>The link's fixed names: its routes, its subprotocol, its close codes and its limits.</summary>
public static class LinkProtocol
{
    /// <summary>The attach route, under the service URL: <c>POST {serviceUrl}/api/link/attach</c>.</summary>
    public const string AttachPath = "/api/link/attach";

    /// <summary>The link's WebSocket route, under the service URL: <c>GET {serviceUrl}/api/link</c>.</summary>
    public const string LinkPath = "/api/link";

    /// <summary>The WebSocket subprotocol of version 1.</summary>
    public const string Subprotocol = "yawble-link.1";

    /// <summary>Close code: the credential is unknown or revoked. The instance stops retrying until a new attach.</summary>
    public const int CloseCredentialRefused = 4401;

    /// <summary>Close code: a protocol error. The instance retries with backoff.</summary>
    public const int CloseProtocolError = 4400;

    /// <summary>The prefix of frame types reserved for a later version; a version 1 instance ignores them.</summary>
    public const string ReservedRelayPrefix = "relay.";

    public const int DefaultHeartbeatSeconds = 15;

    public const int MinHeartbeatSeconds = 5;

    public const int MaxHeartbeatSeconds = 300;
}

/// <summary>What an instance sends to attach: the one-time <see cref="Token"/> the person was given, and its own version and commit.</summary>
public sealed record AttachRequest(string Token, string Version, string Commit);

/// <summary>
/// The service's 200 answer to an attach: the link's <see cref="Credential"/> (stored only as ciphertext,
/// never shown or logged), the service's own name and the name it gives this instance.
/// </summary>
public sealed record AttachAnswer(string Credential, string ServiceName, string InstanceName);

/// <summary>The body of any non-2xx answer from the service: one sentence, shown to the person verbatim.</summary>
public sealed record LinkError(string Error);

/// <summary>One frame on the link: one UTF-8 JSON text message, discriminated by <see cref="Type"/>.</summary>
public abstract record LinkFrame([property: JsonPropertyOrder(-1)] string Type);

/// <summary>Instance to service, first on every connection.</summary>
public sealed record Hello(string Version, string Commit, DateTimeOffset StartedAt) : LinkFrame("hello");

/// <summary>Instance to service: the latest <see cref="Summary"/>, numbered by <see cref="Seq"/>, which only grows.</summary>
public sealed record Status(long Seq, DateTimeOffset At, StatusSummary Summary) : LinkFrame("status");

/// <summary>Instance to service, every <see cref="Welcome.HeartbeatSeconds"/> while nothing else is sent.</summary>
public sealed record Heartbeat(DateTimeOffset At) : LinkFrame("heartbeat");

/// <summary>
/// Service to instance, in answer to <see cref="Hello"/>: the names to show, and the heartbeat interval
/// (5 to 300 seconds; 15 when absent).
/// </summary>
public sealed record Welcome(string ServiceName, string InstanceName, int HeartbeatSeconds = LinkProtocol.DefaultHeartbeatSeconds)
    : LinkFrame("welcome");

/// <summary>Service to instance: the link is refused for <see cref="Reason"/>, and the service closes it next.</summary>
public sealed record Refused(string Reason) : LinkFrame("refused");

/// <summary>
/// What a status carries: only what the Teams list already shows. Never documents, transcripts,
/// prompts, file paths, connection values or secrets. <see cref="Health"/> is
/// <see cref="Healthy"/> or <see cref="Unhealthy"/>.
/// </summary>
public sealed record StatusSummary(
    string Health, string Version, IReadOnlyList<WorkerState> Workers, IReadOnlyList<TeamState> Teams, int NeedsYou)
{
    public const string Healthy = "healthy";

    public const string Unhealthy = "unhealthy";
}

/// <summary>One worker, by <see cref="Name"/>; <see cref="State"/> is <see cref="Connected"/> or <see cref="Dropped"/>.</summary>
public sealed record WorkerState(string Name, string State)
{
    public const string Connected = "connected";

    public const string Dropped = "dropped";
}

/// <summary>One team, as its row in the Teams list shows it.</summary>
public sealed record TeamState(string Id, string Name, string Status, int Running, int Idle, int Blocked, int NeedsYou);

/// <summary>How the link's JSON is written and read.</summary>
public static class LinkJson
{
    /// <summary>Web defaults: camelCase names, case-insensitive reading, enums as camelCase strings.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>One frame as its JSON text, <c>type</c> first.</summary>
    public static string Serialize(LinkFrame frame) => JsonSerializer.Serialize(frame, frame.GetType(), Options);

    /// <summary>
    /// One frame from its JSON text, or null when its <c>type</c> is missing or not one this version
    /// knows - a "relay." type included - so the caller ignores it. Text that is not a JSON object is a
    /// protocol error and throws <see cref="JsonException"/>.
    /// </summary>
    public static LinkFrame? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("A link frame is one JSON object.");
        }

        if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var frameType = type.GetString() switch
        {
            "hello" => typeof(Hello),
            "status" => typeof(Status),
            "heartbeat" => typeof(Heartbeat),
            "welcome" => typeof(Welcome),
            "refused" => typeof(Refused),
            _ => null,
        };

        return frameType is null ? null : (LinkFrame?)root.Deserialize(frameType, Options);
    }
}
