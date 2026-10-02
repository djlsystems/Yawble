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
// A run's secrets - the credential and the redaction set on its start, and the set the worker applied -
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
public abstract record WorkerFrame;

/// <summary>
/// The worker's first frame: who it is, its build version, the session it was started as (a new
/// one each time the worker process starts), its own random nonce, its measured capacity, the runs it
/// still holds, and the last event sequence number it published.
/// </summary>
public sealed record WorkerHello(
    WorkerId Worker, string Version, string Session, string Nonce, WorkerHelloCapacity Capacity,
    IReadOnlyList<RunId> OpenRuns, long LastSeq) : WorkerFrame;

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

/// <summary>
/// What a worker in a process of its own needs from control's settings to run runs as control's
/// own would: the normal limit of one run on it (the heavy allowance never goes below it) and the
/// megabytes of its container the heavy allowance leaves to the worker process.
/// </summary>
public sealed record RunWorkerSettings(MemoryFigure? RunLimit, long ReserveMb)
{
    public static RunWorkerSettings None { get; } = new(null, 0);
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
            case CommandFrame { Message: StartRun start } command when start.Credential is not null || start.Redaction is not null:
                frame = command with
                {
                    Message = start with { Credential = null, Redaction = null },
                    Sealed = Seal(new Secrets(start.Credential, start.Redaction)),
                };
                break;

            case EventFrame { Envelope.Event: RunCredentialApplied { Redaction: not null } applied } @event:
                frame = @event with
                {
                    Envelope = @event.Envelope with { Event = applied with { Redaction = null } },
                    Sealed = Seal(new Secrets(null, applied.Redaction)),
                };
                break;
        }

        var text = JsonSerializer.Serialize(frame, Json);
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes) throw new InvalidDataException(TooLargeText);
        return text;
    }

    /// <summary>The frame <paramref name="text"/> is, its secrets opened. Throws <see cref="InvalidDataException"/> with a sentence when it cannot be.</summary>
    public WorkerFrame Read(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes) throw new InvalidDataException(TooLargeText);

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

        switch (frame)
        {
            case CommandFrame { Sealed: { } blob, Message: StartRun start } command:
                var forStart = Open(blob);
                return command with { Message = start with { Credential = forStart.Credential, Redaction = forStart.Redaction }, Sealed = null };

            case EventFrame { Sealed: { } blob, Envelope.Event: RunCredentialApplied applied } @event:
                var forEvent = Open(blob);
                return @event with { Envelope = @event.Envelope with { Event = applied with { Redaction = forEvent.Redaction } }, Sealed = null };

            case CommandFrame { Sealed: not null } or EventFrame { Sealed: not null }:
                throw new InvalidDataException(UnsealableText);

            default:
                return frame;
        }
    }

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

    /// <summary>A run's secrets, written by hand: neither record may ever be written as JSON by a serializer.</summary>
    private sealed record Secrets(RunCredential? Credential, ValueRedactor? Redaction)
    {
        public byte[] ToJson()
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                if (Credential is { } credential)
                {
                    writer.WriteStartObject("credential");
                    writer.WriteString("source", credential.Source.ToString());
                    writer.WriteStartObject("environment");
                    foreach (var (name, value) in credential.Environment) writer.WriteString(name, value);
                    writer.WriteEndObject();
                    Strings(writer, "displace", credential.Displace);
                    Strings(writer, "otherProviders", credential.OtherProviders);
                    writer.WriteBoolean("perRunHome", credential.PerRunHome);
                    if (credential.Missing is { } missing) writer.WriteString("missing", missing);
                    writer.WriteEndObject();
                }

                if (Redaction is { } redaction) Strings(writer, "redaction", redaction.Forms);
                writer.WriteEndObject();
            }

            return buffer.ToArray();
        }

        public static Secrets FromJson(byte[] json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            RunCredential? credential = null;
            if (root.TryGetProperty("credential", out var c))
            {
                credential = new RunCredential(
                    Enum.Parse<CredentialSource>(c.GetProperty("source").GetString()!),
                    c.GetProperty("environment").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal),
                    [.. c.GetProperty("displace").EnumerateArray().Select(e => e.GetString()!)],
                    [.. c.GetProperty("otherProviders").EnumerateArray().Select(e => e.GetString()!)],
                    c.GetProperty("perRunHome").GetBoolean(),
                    c.TryGetProperty("missing", out var m) ? m.GetString() : null);
            }

            var redaction = root.TryGetProperty("redaction", out var r)
                ? ValueRedactor.FromForms(r.EnumerateArray().Select(e => e.GetString()!))
                : null;

            return new Secrets(credential, redaction);
        }

        private static void Strings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
        {
            writer.WriteStartArray(name);
            foreach (var value in values) writer.WriteStringValue(value);
            writer.WriteEndArray();
        }
    }
}
