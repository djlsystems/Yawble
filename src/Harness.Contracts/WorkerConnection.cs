using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Harness.Host;

namespace Harness.Contracts;

// THE WORKER CONNECTION: how the run protocol travels between control and a worker in a process of
// its own. One WebSocket per worker, opened by the worker to control; one UTF-8 JSON text message per
// frame, in order both ways. The frames wrap the protocol's own records and add what a connection
// needs: who the worker is and whether control takes it, which command was applied, which events
// control has handled, and a keep-alive.
//
// A run's secrets - the redaction set on its start, the set the worker applied, a terminal's environment,
// and a sign-in browser's saved storage state -
// are never in a frame in the clear. They travel SEALED: AES-256-GCM under a key made for this one
// connection from the worker key and both sides' nonces (WorkerFrameCodec.Key), so only a holder of
// the worker key on this connection can open them.

/// <summary>One message on a worker's connection.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "frame")]
[JsonDerivedType(typeof(WorkerHello), "hello")]
[JsonDerivedType(typeof(WorkerWelcome), "welcome")]
[JsonDerivedType(typeof(WorkerRefused), "refused")]
[JsonDerivedType(typeof(CommandFrame), "command")]
[JsonDerivedType(typeof(AppliedFrame), "applied")]
[JsonDerivedType(typeof(EventFrame), "event")]
[JsonDerivedType(typeof(HandledFrame), "handled")]
[JsonDerivedType(typeof(PingFrame), "ping")]
[JsonDerivedType(typeof(PongFrame), "pong")]
[JsonDerivedType(typeof(StreamFrame), "stream")]
[JsonDerivedType(typeof(StreamInputFrame), "input")]
[JsonDerivedType(typeof(WorkerDraining), "draining")]
[JsonDerivedType(typeof(WorkerSettingsFrame), "settings")]
public abstract record WorkerFrame;

/// <summary>
/// The worker's first frame: who it is, its build version, the session it was started as (a new
/// one each time the worker process starts), its own random nonce, its measured capacity, the runs it
/// still holds, the last event sequence number it published, and whether it is draining (takes no new
/// run), so a drain survives a reconnect.
/// </summary>
public sealed record WorkerHello(
    WorkerId Worker, string Version, string Session, string Nonce, WorkerHelloCapacity Capacity,
    IReadOnlyList<RunId> OpenRuns, long LastSeq, bool Draining = false) : WorkerFrame;

/// <summary>A worker's CPUs and container memory limit as it measured them; null is not measured.</summary>
public sealed record WorkerHelloCapacity(double? Cpus, long? MemoryLimitBytes);

/// <summary>
/// Control took the worker: control's nonce, the last event of this worker's session control has
/// handled - the worker sends again everything after it - and the figures the worker runs by.
/// </summary>
public sealed record WorkerWelcome(string Nonce, long HandledSeq, RunWorkerSettings Settings) : WorkerFrame;

/// <summary>Control refused the worker, and why, in a sentence. The connection closes after it.</summary>
public sealed record WorkerRefused(string Sentence) : WorkerFrame;

/// <summary>
/// One protocol message from control, numbered so its <see cref="AppliedFrame"/> can answer it.
/// <see cref="Sealed"/> holds the message's secrets when it carries any.
/// </summary>
public sealed record CommandFrame(long Id, ControlMessage Message, string? Sealed = null) : WorkerFrame;

/// <summary>The worker applied command <see cref="Id"/>; <see cref="Error"/> is set when applying it threw.</summary>
public sealed record AppliedFrame(long Id, string? Error) : WorkerFrame;

/// <summary>One event, in sequence order. <see cref="Sealed"/> holds the event's secrets when it carries any.</summary>
public sealed record EventFrame(WorkerEnvelope Envelope, string? Sealed = null) : WorkerFrame;

/// <summary>Control has handled every event of the worker's session up to and including <see cref="Seq"/>.</summary>
public sealed record HandledFrame(long Seq) : WorkerFrame;

/// <summary>Control asks whether the worker is there.</summary>
public sealed record PingFrame(long N) : WorkerFrame;

/// <summary>The worker is there.</summary>
public sealed record PongFrame(long N) : WorkerFrame;

/// <summary>One stream chunk, worker to control: not numbered among the events, not kept, not acknowledged.</summary>
public sealed record StreamFrame(StreamChunk Chunk) : WorkerFrame;

/// <summary>A person's keystrokes, control to worker: outside the command queue, never kept, never answered.</summary>
public sealed record StreamInputFrame(StreamInput Input) : WorkerFrame;

/// <summary>
/// Worker to control: this worker takes no new run while <see cref="Draining"/> (its operator is about
/// to stop it); the runs it has go on. Said on a change, and again in every hello.
/// </summary>
public sealed record WorkerDraining(bool Draining) : WorkerFrame;

/// <summary>
/// Control to worker: the figures it runs by changed (a person changed a memory or admission setting).
/// The worker takes them as it takes a welcome's; a frame lost to a drop is made good by the next welcome.
/// </summary>
public sealed record WorkerSettingsFrame(RunWorkerSettings Settings) : WorkerFrame;

/// <summary>
/// What a worker in a process of its own needs from control's settings to run runs as control's
/// own would: the normal limit of one run on it (the heavy allowance never goes below it) and the
/// megabytes of its container the heavy allowance leaves to the worker process.
/// </summary>
public sealed record RunWorkerSettings(MemoryFigure? RunLimit, long ReserveMb)
{
    public static RunWorkerSettings None { get; } = new(null, 0);
}

/// <summary>The sentences a worker connection is refused with: control answers them, and a worker logs them.</summary>
public static class WorkerSentences
{
    /// <summary>No worker key, or not this instance's.</summary>
    public const string WrongKey =
        "This route takes this instance's worker key in X-Api-Key, and none was given or the one given is not it.";

    /// <summary>A person, or a credential that is not the worker key.</summary>
    public const string NotAWorker = "Only a worker connects here, with the worker key.";

    /// <summary>The worker key, but not on a WebSocket.</summary>
    public const string NotAWebSocket = "A worker connects here with a WebSocket.";

    /// <summary>A worker that said nothing in time.</summary>
    public const string NoHello = "No hello within 10 s.";

    /// <summary>A Host that runs its runs itself takes no worker.</summary>
    public const string NotControl =
        "This Host runs its runs itself (--Role all) and takes no worker; start it with --Role control for workers to connect.";

    /// <summary>A worker of another build.</summary>
    public static string OtherVersion(string worker, string control) =>
        $"This worker is version {worker} and control is version {control}; a worker must run control's version.";

    /// <summary>A second worker under a name that is connected.</summary>
    public static string NameTaken(WorkerId worker, DateTimeOffset since) =>
        $"A worker named {worker} is already connected, since {since.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC; give each worker its own HARNESS_WORKER_ID.";
}

/// <summary>
/// Writes and reads one connection's frames: the JSON every frame is written in, and the sealing of
/// a run's secrets under the connection's key. Made once a connection's nonces are known.
/// </summary>
public sealed class WorkerFrameCodec
{
    /// <summary>The largest frame either side reads; a run's whole output fits well inside it.</summary>
    public const int MaxBytes = 16 * 1024 * 1024;

    /// <summary>What a frame over <see cref="MaxBytes"/> is refused with.</summary>
    public const string TooLargeText = "A worker message over 16 MiB was refused.";

    /// <summary>
    /// The largest frame carrying a binary result to be written as the member (<see cref="WriteAsAgent"/>): its
    /// 20 MB, as base64, and the rest of the frame. Every other frame stays within <see cref="MaxBytes"/>.
    /// </summary>
    public const int MaxWriteBytes = 32 * 1024 * 1024;

    /// <summary>What a frame over <see cref="MaxWriteBytes"/> is refused with.</summary>
    public const string WriteTooLargeText = "A worker message over 32 MiB was refused.";

    /// <summary>What a sealed secret that does not open is refused with.</summary>
    public const string UnsealableText = "A sealed run secret did not open with this worker's key.";

    /// <summary>The options every frame is written with, at both ends.</summary>
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);

    private readonly byte[]? _key;

    /// <param name="key">The connection's key (<see cref="Key"/>); null before the nonces are known, when no secret may cross.</param>
    public WorkerFrameCodec(byte[]? key) => _key = key;

    /// <summary>
    /// The key one connection seals with: HKDF-SHA256 over the worker key, salted with control's nonce
    /// and the worker's, for run secrets. A new key for each connection.
    /// </summary>
    public static byte[] Key(string workerKey, string controlNonce, string workerNonce) =>
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(workerKey), 32,
            Encoding.UTF8.GetBytes(controlNonce + "\n" + workerNonce), Encoding.UTF8.GetBytes("run secrets"));

    /// <summary>A fresh random nonce, as text.</summary>
    public static string NewNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

    /// <summary><paramref name="frame"/> as the text sent, its secrets sealed.</summary>
    public string Write(WorkerFrame frame)
    {
        switch (frame)
        {
            case CommandFrame { Message: StartTerminal terminal } command when terminal.Launch.Environment.Count > 0:
                frame = command with
                {
                    Message = terminal with { Launch = terminal.Launch with { Environment = new Dictionary<string, string>() } },
                    Sealed = Seal(new Secrets(null, terminal.Launch.Environment)),
                };
                break;

            case CommandFrame { Message: ReadAgentFile { Redaction: not null } read } command:
                frame = command with
                {
                    Message = read with { Redaction = null },
                    Sealed = Seal(new Secrets(read.Redaction)),
                };
                break;

            case CommandFrame { Message: StartRun { Redaction: not null } start } command:
                frame = command with
                {
                    Message = start with { Redaction = null },
                    Sealed = Seal(new Secrets(start.Redaction)),
                };
                break;

            case CommandFrame { Message: CheckLaunch { Redaction: not null } check } command:
                frame = command with
                {
                    Message = check with { Redaction = null },
                    Sealed = Seal(new Secrets(check.Redaction)),
                };
                break;

            case EventFrame { Envelope.Event: RunCredentialApplied { Redaction: not null } applied } @event:
                frame = @event with
                {
                    Envelope = @event.Envelope with { Event = applied with { Redaction = null } },
                    Sealed = Seal(new Secrets(applied.Redaction)),
                };
                break;

            case CommandFrame { Message: TestConnection { Probe: not null } test } command:
                frame = command with
                {
                    Message = test with { Probe = null },
                    Sealed = Seal(new Secrets(null, ConnectionSecret: test.Probe.Value)),
                };
                break;

            case EventFrame { Envelope.Event: ConnectionTested { RefreshToken: not null } tested } @event:
                frame = @event with
                {
                    Envelope = @event.Envelope with { Event = tested with { RefreshToken = null } },
                    Sealed = Seal(new Secrets(null, ConnectionSecret: tested.RefreshToken.Value)),
                };
                break;

            case EventFrame { Envelope.Event: SignInBrowserSaved { State: not null } saved } @event:
                frame = @event with
                {
                    Envelope = @event.Envelope with { Event = saved with { State = null } },
                    Sealed = Seal(new Secrets(null, StorageState: saved.State.Json)),
                };
                break;
        }

        var text = JsonSerializer.Serialize(frame, Json);
        Bound(Encoding.UTF8.GetByteCount(text), frame);
        return text;
    }

    /// <summary>Refuses a frame of <paramref name="length"/> bytes past its cap: <see cref="MaxWriteBytes"/> for a binary result written as the member, <see cref="MaxBytes"/> for any other.</summary>
    private static void Bound(int length, WorkerFrame frame)
    {
        if (frame is CommandFrame { Message: WriteAsAgent })
        {
            if (length > MaxWriteBytes) throw new InvalidDataException(WriteTooLargeText);
        }
        else if (length > MaxBytes)
        {
            throw new InvalidDataException(TooLargeText);
        }
    }

    /// <summary>The frame <paramref name="text"/> is, its secrets opened. Throws <see cref="InvalidDataException"/> with a sentence when it cannot be.</summary>
    public WorkerFrame Read(string text)
    {
        var length = Encoding.UTF8.GetByteCount(text);
        if (length > MaxWriteBytes) throw new InvalidDataException(WriteTooLargeText);
        if (length > MaxBytes && !text.Contains("\"writeAsAgent\"", StringComparison.Ordinal)) throw new InvalidDataException(TooLargeText);

        WorkerFrame frame;
        try
        {
            frame = JsonSerializer.Deserialize<WorkerFrame>(text, Json)
                ?? throw new InvalidDataException("A worker message was empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"A worker message was not a frame: {exception.Message}");
        }

        Bound(length, frame);

        switch (frame)
        {
            case CommandFrame { Sealed: { } blob, Message: StartTerminal terminal } command:
                var forTerminal = Open(blob);
                return command with
                {
                    Message = terminal with { Launch = terminal.Launch with { Environment = forTerminal.Environment ?? new Dictionary<string, string>() } },
                    Sealed = null,
                };

            case CommandFrame { Sealed: { } blob, Message: ReadAgentFile read } command:
                var forRead = Open(blob);
                return command with { Message = read with { Redaction = forRead.Redaction }, Sealed = null };

            case CommandFrame { Sealed: { } blob, Message: StartRun start } command:
                var forStart = Open(blob);
                return command with { Message = start with { Redaction = forStart.Redaction }, Sealed = null };

            case CommandFrame { Sealed: { } blob, Message: CheckLaunch check } command:
                var forCheck = Open(blob);
                return command with { Message = check with { Redaction = forCheck.Redaction }, Sealed = null };

            case EventFrame { Sealed: { } blob, Envelope.Event: RunCredentialApplied applied } @event:
                var forEvent = Open(blob);
                return @event with { Envelope = @event.Envelope with { Event = applied with { Redaction = forEvent.Redaction } }, Sealed = null };

            case EventFrame { Sealed: { } blob, Envelope.Event: SignInBrowserSaved saved } @event:
                var forSave = Open(blob);
                return @event with
                {
                    Envelope = @event.Envelope with
                    {
                        Event = saved with { State = forSave.StorageState is { } state ? new SignInStorageState(state) : null },
                    },
                    Sealed = null,
                };

            case CommandFrame { Sealed: { } blob, Message: TestConnection test } command:
                return command with { Message = test with { Probe = Secret(Open(blob)) }, Sealed = null };

            case EventFrame { Sealed: { } blob, Envelope.Event: ConnectionTested tested } @event:
                return @event with { Envelope = @event.Envelope with { Event = tested with { RefreshToken = Secret(Open(blob)) } }, Sealed = null };

            case CommandFrame { Sealed: not null } or EventFrame { Sealed: not null }:
                throw new InvalidDataException(UnsealableText);

            default:
                return frame;
        }
    }

    private static ConnectionTestSecret? Secret(Secrets secrets) =>
        secrets.ConnectionSecret is { } value ? new ConnectionTestSecret(value) : null;

    private string Seal(Secrets secrets)
    {
        if (_key is null) throw new InvalidOperationException("No run secret crosses a connection before its key is made.");

        var plain = secrets.ToJson();
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        using (var aes = new AesGcm(_key, tag.Length)) aes.Encrypt(nonce, plain, cipher, tag);
        CryptographicOperations.ZeroMemory(plain);

        return Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    private Secrets Open(string blob)
    {
        if (_key is null) throw new InvalidDataException(UnsealableText);

        try
        {
            var bytes = Convert.FromBase64String(blob);
            var nonceSize = AesGcm.NonceByteSizes.MaxSize;
            var tagSize = AesGcm.TagByteSizes.MaxSize;
            if (bytes.Length < nonceSize + tagSize) throw new InvalidDataException(UnsealableText);

            var plain = new byte[bytes.Length - nonceSize - tagSize];
            using (var aes = new AesGcm(_key, tagSize))
            {
                aes.Decrypt(bytes.AsSpan(0, nonceSize), bytes.AsSpan(nonceSize + tagSize), bytes.AsSpan(nonceSize, tagSize), plain);
            }

            try
            {
                return Secrets.FromJson(plain);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or JsonException or KeyNotFoundException)
        {
            throw new InvalidDataException(UnsealableText);
        }
    }

    /// <summary>
    /// A run's secrets, written by hand: a redaction set may never be written as JSON by a serializer.
    /// <see cref="Environment"/> is a terminal's whole environment, which holds its session's key;
    /// <see cref="StorageState"/> a sign-in browser's saved session; <see cref="ConnectionSecret"/> a
    /// connection test's probe or rotated refresh token.
    /// </summary>
    private sealed record Secrets(
        ValueRedactor? Redaction, IReadOnlyDictionary<string, string>? Environment = null, string? StorageState = null,
        string? ConnectionSecret = null)
    {
        public byte[] ToJson()
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                if (Redaction is { } redaction) Strings(writer, "redaction", redaction.Forms);
                if (Environment is { } environment)
                {
                    writer.WriteStartObject("terminalEnvironment");
                    foreach (var (name, value) in environment) writer.WriteString(name, value);
                    writer.WriteEndObject();
                }

                if (StorageState is { } state) writer.WriteString("storageState", state);
                if (ConnectionSecret is { } secret) writer.WriteString("connectionSecret", secret);

                writer.WriteEndObject();
            }

            return buffer.ToArray();
        }

        public static Secrets FromJson(byte[] json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var redaction = root.TryGetProperty("redaction", out var r)
                ? ValueRedactor.FromForms(r.EnumerateArray().Select(e => e.GetString()!))
                : null;

            var environment = root.TryGetProperty("terminalEnvironment", out var t)
                ? t.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal)
                : null;

            var state = root.TryGetProperty("storageState", out var s) ? s.GetString() : null;
            var secret = root.TryGetProperty("connectionSecret", out var c) ? c.GetString() : null;

            return new Secrets(redaction, environment, state, secret);
        }

        private static void Strings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
        {
            writer.WriteStartArray(name);
            foreach (var value in values) writer.WriteStringValue(value);
            writer.WriteEndArray();
        }
    }
}
