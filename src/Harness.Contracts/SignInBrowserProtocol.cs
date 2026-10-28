using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harness.Contracts;

// A PERSON'S SIGN-IN BROWSER ON A WORKER. A person signs in to a website themselves, in a sandboxed
// headless Chromium a worker runs, and the platform keeps that browser's session as a masked secret
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
/// </summary>
public sealed record SignInBrowserLaunch(string StartUrl, IReadOnlyList<string> Hosts, int Width, int Height);

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
/// A browser's storage state as Playwright's <c>storageState</c> reads it:
/// <c>{ "cookies": [...], "origins": [ { "origin", "localStorage": [ { "name", "value" } ] } ] }</c>.
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
