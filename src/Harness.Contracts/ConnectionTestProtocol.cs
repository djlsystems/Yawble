using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harness.Contracts;

// ---------------------------------------------------------------------------------------------
// A CONNECTION'S TEST ON A WORKER. A person asks whether a connection works; control reads what the
// connection holds and sends one worker the probe - what to reach and with what - and the worker
// answers ok, refused or failed in one sentence. The probe holds the connection's secrets and the
// answer may hold a rotated refresh token: each refuses to be written as JSON, and crosses a
// connection only sealed. No team and no plugin member is involved, so any connection can be tested.
// ---------------------------------------------------------------------------------------------

/// <summary>Test one connection with <see cref="Probe"/>, answered by <see cref="ConnectionTested"/>.</summary>
public sealed record TestConnection(string Request, ConnectionTestSecret? Probe) : AgentCliCommand(Request);

/// <summary>
/// How a test went, in <see cref="Sentence"/>: <c>ok</c>, <c>refused</c> (the service said no to what
/// the connection holds) or <c>failed</c> (not reached, or not answering as it should).
/// <see cref="RefreshToken"/> is a sign-in's refresh token when the provider rotated it, which control
/// stores; never written as JSON.
/// <see cref="Tools"/> is what an MCP server's check listed: its tools' names, when it was ok.
/// </summary>
public sealed record ConnectionTested(
    string Request, string Outcome, string Sentence, ConnectionTestSecret? RefreshToken = null, IReadOnlyList<string>? Tools = null) : WorkerEvent
{
    public const string Ok = "ok";

    public const string Refused = "refused";

    public const string Failed = "failed";
}

/// <summary>What a test reaches. Each kind is one record; it crosses only sealed, inside a <see cref="ConnectionTestSecret"/>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "probe")]
[JsonDerivedType(typeof(MailboxProbe), "mailbox")]
[JsonDerivedType(typeof(OAuthProbe), "oauth")]
[JsonDerivedType(typeof(HttpProbe), "http")]
[JsonDerivedType(typeof(McpProbe), "mcp")]
public abstract record ConnectionProbe
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>This probe as the secret that crosses.</summary>
    public ConnectionTestSecret Seal() => new(JsonSerializer.Serialize(this, Json));

    /// <summary>The probe a secret holds.</summary>
    public static ConnectionProbe Open(ConnectionTestSecret secret) =>
        JsonSerializer.Deserialize<ConnectionProbe>(secret.Value, Json) ?? throw new InvalidDataException("A connection test carried no probe.");
}

/// <summary>A mail server as a test reaches it: <c>TLS</c> from the first byte, or <c>STARTTLS</c>.</summary>
public sealed record ProbeServer(string Host, int Port, string Security);

/// <summary>A mailbox: an IMAP login and an SMTP authentication with its app password.</summary>
public sealed record MailboxProbe(string Account, string Username, ProbeServer Imap, ProbeServer Smtp, string? Preset, string Password)
    : ConnectionProbe
{
    public override string ToString() => $"MailboxProbe {{ Account = {Account}, Imap = {Imap.Host}, Smtp = {Smtp.Host} }}";
}

/// <summary>
/// A sign-in: the refresh token grant at <see cref="TokenUrl"/> (with <see cref="Scopes"/> when named),
/// then one read of <see cref="ProfileUrl"/> with the access token, when the provider has one.
/// <see cref="Provider"/> is the provider's name, as a sentence says it.
/// </summary>
public sealed record OAuthProbe(
    string Provider, string TokenUrl, string ClientId, string? ClientSecret, string RefreshToken, IReadOnlyList<string>? Scopes, string? ProfileUrl)
    : ConnectionProbe
{
    public override string ToString() => $"OAuthProbe {{ Provider = {Provider}, TokenUrl = {TokenUrl} }}";
}

/// <summary>
/// One harmless GET a preset declares: <see cref="Url"/> with <see cref="Headers"/>. A 401 or 403 is a
/// refusal; so is a body matching <see cref="RefusedWhen"/>, whose first group (in
/// <see cref="ReasonFrom"/>) is the service's reason. <see cref="Secrets"/> are the values no sentence
/// may carry. <see cref="Service"/> names the service and <see cref="What"/> what it is given ("API key",
/// "username and password"); <see cref="Host"/> is the address a sentence may say - never the URL, whose
/// query may hold a key.
/// </summary>
public sealed record HttpProbe(
    string Service, string What, string Url, IReadOnlyDictionary<string, string> Headers, IReadOnlyList<string> Secrets,
    string? RefusedWhen = null, string? ReasonFrom = null)
    : ConnectionProbe
{
    public string Host => Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri.Host : Service;

    public override string ToString() => $"HttpProbe {{ Service = {Service}, Host = {Host} }}";
}

/// <summary>
/// A secret a connection test carries: a probe's JSON, or a rotated refresh token. Never written as
/// JSON by a serializer, and never shown by <see cref="ToString"/>; the worker connection seals it.
/// </summary>
[JsonConverter(typeof(ConnectionTestSecretNeverSerialised))]
public sealed class ConnectionTestSecret(string value)
{
    public string Value { get; } = value;

    public override string ToString() => "[connection secret]";
}

/// <summary>Refuses to read or write a <see cref="ConnectionTestSecret"/> as JSON.</summary>
public sealed class ConnectionTestSecretNeverSerialised : JsonConverter<ConnectionTestSecret>
{
    public override ConnectionTestSecret Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("A connection test's secret is never read from JSON.");

    public override void Write(Utf8JsonWriter writer, ConnectionTestSecret value, JsonSerializerOptions options) =>
        throw new NotSupportedException("A connection test's secret is never written as JSON.");
}

/// <summary>
/// An MCP server at a web address (MCP 1): initialize over streamable HTTP, then list its tools, with
/// <see cref="Headers"/> on every request. A 401 or 403 is a refusal of what the headers carry.
/// <see cref="Name"/> is the entry's name; <see cref="Secrets"/> are the values no sentence may carry,
/// the URL's query among them. <see cref="Host"/> is all a sentence says of the address.
/// </summary>
public sealed record McpProbe(string Name, string Url, IReadOnlyDictionary<string, string> Headers, IReadOnlyList<string> Secrets)
    : ConnectionProbe
{
    public string Host => Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri.Host : Name;

    public override string ToString() => $"McpProbe {{ Name = {Name}, Host = {Host} }}";
}
