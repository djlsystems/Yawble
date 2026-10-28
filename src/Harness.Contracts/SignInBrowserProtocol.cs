using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harness.Contracts;

// A PERSON'S SIGN-IN BROWSER ON A WORKER. A person signs in to a website themselves, in a sandboxed
// Google Chrome a worker runs headed on a virtual screen, and the platform keeps that browser's session as a masked secret
// of one plugin member's table row. Control keeps the person's side - the page's socket, who owns the
// session, its idle clock and the save - and asks a worker to run the browser. The worker owns
// everything Chromium says: the host limits, the screencast, the input and the capture, so a cookie
// crosses the connection only inside the one sealed answer to a save.
//
// The browser's screen is a stream (StreamChunk) under the session's id: Bytes is one JPEG frame, Text
// one notice as JSON (SignInBrowserNotice). The person's mouse and keys go back as StreamInput, each
// one SignInBrowserInput as UTF-8 JSON. The end is a sequenced event, after the last frame.

/// <summary>A message about a worker's sign-in browsers. A worker applies each without holding its queue.</summary>
public abstract record SignInBrowserCommand : ControlMessage;

/// <summary>
/// What a sign-in browser starts with: the entry's Site URL it opens first, and the entry's own hosts
/// (the Site URL's host and any other host the entry lists), which every top-level navigation may
/// reach. The viewport is <see cref="Width"/> by <see cref="Height"/> CSS pixels.
/// <see cref="Profile"/> names the row's own browser profile, <c>&lt;member key&gt;/&lt;row key&gt;</c>
/// (32 hex digits each, <c>SignInProfileKey</c>), kept on the worker's volume and reused on every
/// sign-in for that row; null is a fresh profile, removed when the browser closes.
/// <see cref="Languages"/> (the person's <c>navigator.languages</c>) and <see cref="TimeZone"/> (their
/// IANA time zone) are the browser's, so the site sees the person's own locale.
/// </summary>
public sealed record SignInBrowserLaunch(
    string StartUrl,
    IReadOnlyList<string> Hosts,
    int Width,
    int Height,
    string? Profile = null,
    IReadOnlyList<string>? Languages = null,
    string? TimeZone = null);

/// <summary>Start a sign-in browser under <see cref="Session"/>. Its screen is the stream of that id.</summary>
public sealed record StartSignInBrowser(string Session, SignInBrowserLaunch Launch) : SignInBrowserCommand;

/// <summary>
/// Capture the browser's storage state (cookies and local storage), pruned to the entry's hosts and
/// the sign-in providers the person passed through, and answer <see cref="SignInBrowserSaved"/> under
/// <see cref="Request"/>. The browser stays open: control stops it once the save is kept.
/// </summary>
public sealed record SaveSignInBrowser(string Session, string Request) : SignInBrowserCommand;

/// <summary>End the browser and remove its profile. <see cref="SignInBrowserEnded"/> follows.</summary>
public sealed record StopSignInBrowser(string Session) : SignInBrowserCommand;

/// <summary>
/// Remove the browser profiles under <see cref="Profile"/>: one row's (<c>&lt;member key&gt;/&lt;row key&gt;</c>)
/// when the row is deleted, or every row of a member's (<c>&lt;member key&gt;</c>) when the member is.
/// A browser open on one is stopped first. Nothing answers.
/// </summary>
public sealed record DeleteSignInProfiles(string Profile) : SignInBrowserCommand;

/// <summary>
/// What a save captured: the storage state, or <see cref="Error"/> saying why there is none, with the
/// hosts the state was pruned to and the providers among them. <see cref="State"/> holds the
/// session's cookies: it refuses to be written as JSON and crosses a connection only sealed.
/// </summary>
public sealed record SignInBrowserSaved(
    string Session,
    string Request,
    SignInStorageState? State,
    IReadOnlyList<string> Hosts,
    IReadOnlyList<string> Providers,
    string? Error) : WorkerEvent;

/// <summary>
/// The browser is over, after its last frame: it was stopped, or Chromium exited or could not start.
/// <see cref="Reason"/> is a sentence for the person, never page content.
/// </summary>
public sealed record SignInBrowserEnded(string Session, string Reason) : WorkerEvent;

/// <summary>
/// A saved sign-in session, the row's session secret: the envelope
/// <c>{ "v": 2, "storageState": {...}, "identity": {...} }</c>, where <c>storageState</c> is Playwright's
/// <c>storageState</c> (<c>{ "cookies": [...], "origins": [ { "origin", "localStorage": [ { "name", "value" } ] } ] }</c>)
/// and <c>identity</c> is the <see cref="SignInBrowserIdentity"/> of the browser that signed in
/// (<see cref="SignInSessionValue"/> reads both, and a bare storage state saved before the envelope).
/// A secret: never written as JSON by a serializer, and never shown by <see cref="ToString"/>.
/// </summary>
[JsonConverter(typeof(SignInStorageStateNeverSerialised))]
public sealed class SignInStorageState(string json)
{
    /// <summary>The state's JSON text.</summary>
    public string Json { get; } = json;

    public override string ToString() => "[storage state]";
}

/// <summary>Refuses to read or write a <see cref="SignInStorageState"/> as JSON: it holds a session.</summary>
public sealed class SignInStorageStateNeverSerialised : JsonConverter<SignInStorageState>
{
    public override SignInStorageState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("A sign-in browser's storage state is never read from JSON.");

    public override void Write(Utf8JsonWriter writer, SignInStorageState value, JsonSerializerOptions options) =>
        throw new NotSupportedException("A sign-in browser's storage state is never written as JSON: it holds a session.");
}

/// <summary>
/// One thing the person did, as the page sends it. <see cref="Type"/> is <c>mouse</c> (with
/// <see cref="Event"/> <c>mousePressed</c>, <c>mouseReleased</c> or <c>mouseMoved</c>, <see cref="X"/>,
/// <see cref="Y"/>, <see cref="Button"/> and <see cref="ClickCount"/>), <c>wheel</c> (with
/// <see cref="X"/>, <see cref="Y"/>, <see cref="DeltaX"/> and <see cref="DeltaY"/>), <c>key</c> (with
/// <see cref="Event"/> <c>keyDown</c> or <c>keyUp</c>, <see cref="Key"/>, <see cref="Code"/>,
/// <see cref="KeyCode"/> and, for a key that types, <see cref="Text"/>), <c>text</c> (a paste: <see cref="Text"/>),
/// <c>back</c> or <c>reload</c>. <see cref="Modifiers"/> is CDP's bit set (Alt 1, Ctrl 2, Meta 4, Shift 8).
/// Anything else is dropped; nothing here is passed to Chromium as it came.
/// </summary>
public sealed record SignInBrowserInput(
    string Type,
    string? Event = null,
    double X = 0,
    double Y = 0,
    string? Button = null,
    int ClickCount = 0,
    double DeltaX = 0,
    double DeltaY = 0,
    string? Key = null,
    string? Code = null,
    int KeyCode = 0,
    string? Text = null,
    int Modifiers = 0)
{
    /// <summary>The longest text one input may type.</summary>
    public const int MaxText = 8192;
}

/// <summary>
/// A notice about the browser, sent as a Text chunk of its stream: <c>page</c> (the top-level page's
/// <see cref="Url"/> and <see cref="Title"/> now), <c>refused</c> (a navigation to <see cref="Host"/>
/// was not allowed) or <c>provider</c> (<see cref="Host"/> was admitted as a sign-in provider).
/// </summary>
public sealed record SignInBrowserNotice(string Kind, string? Url = null, string? Title = null, string? Host = null);

/// <summary>
/// THE BROWSER A SITE SAW WHEN THE PERSON SIGNED IN, saved beside the session so a plugin's own
/// sign-ins present the same browser: Chrome's build and user agent, its client hints
/// (<see cref="UserAgentMetadata"/>, as CDP's <c>Emulation.setUserAgentOverride</c> and Playwright take
/// them), the person's locale, languages and time zone, and the screen and viewport sizes.
/// <see cref="Browser"/> is <c>chrome</c> (Google Chrome) or <c>chromium</c>.
/// </summary>
public sealed record SignInBrowserIdentity(
    string Browser,
    string Version,
    string UserAgent,
    SignInUserAgentMetadata UserAgentMetadata,
    string Locale,
    IReadOnlyList<string> Languages,
    string TimezoneId,
    SignInBrowserSize Screen,
    SignInBrowserSize Viewport,
    double DeviceScaleFactor);

/// <summary>A browser's client hints: <c>navigator.userAgentData</c> with its high-entropy values.</summary>
public sealed record SignInUserAgentMetadata(
    IReadOnlyList<SignInBrowserBrand> Brands,
    IReadOnlyList<SignInBrowserBrand> FullVersionList,
    string Platform,
    string PlatformVersion,
    string Architecture,
    string Model,
    bool Mobile,
    string Bitness);

/// <summary>One brand of a browser's client hints and its version.</summary>
public sealed record SignInBrowserBrand(string Brand, string Version);

/// <summary>A size in CSS pixels.</summary>
public sealed record SignInBrowserSize(int Width, int Height);

/// <summary>
/// READS A SAVED SESSION: the v2 envelope (<c>{"v":2,"storageState":...,"identity":...}</c>), or a
/// bare storage state saved before it (v1, no identity).
/// </summary>
public static class SignInSessionValue
{
    /// <summary>The envelope's version this platform writes.</summary>
    public const int Version = 2;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The storage state's JSON and, from a v2 value, the identity; throws <see cref="JsonException"/> on anything else.</summary>
    public static (string StorageState, SignInBrowserIdentity? Identity) Read(string value)
    {
        using var document = JsonDocument.Parse(value);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("A saved session is a JSON object.");

        if (!root.TryGetProperty("v", out var v))
        {
            if (!root.TryGetProperty("cookies", out _)) throw new JsonException("A saved session has no cookies.");
            return (root.GetRawText(), null);
        }

        if (v.ValueKind != JsonValueKind.Number || v.GetInt32() != Version || !root.TryGetProperty("storageState", out var state))
        {
            throw new JsonException("A saved session of an unknown version.");
        }

        var identity = root.TryGetProperty("identity", out var who) && who.ValueKind == JsonValueKind.Object
            ? who.Deserialize<SignInBrowserIdentity>(Json)
            : null;
        return (state.GetRawText(), identity);
    }

    /// <summary>The v2 envelope of <paramref name="storageState"/> (JSON) and <paramref name="identity"/>.</summary>
    public static string Write(string storageState, SignInBrowserIdentity identity)
    {
        using var state = JsonDocument.Parse(storageState);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", Version);
            writer.WritePropertyName("storageState");
            state.RootElement.WriteTo(writer);
            writer.WritePropertyName("identity");
            JsonSerializer.Serialize(writer, identity, Json);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}
