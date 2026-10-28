using System.ComponentModel;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Harness.Contracts;
using Harness.Host.Auth;
using Harness.Identity;

namespace Harness.Host;

/// <summary>
/// THE SIGN-IN PAGE'S ONE SOCKET. A row's Sign in link opens the page, and the page opens this: the
/// platform starts a sandboxed headless Chromium for that member's row on a worker and relays it - its
/// screen as binary JPEG frames, notices as JSON text - while the person's mouse and keys come back as
/// JSON text, with <c>{"type":"done"}</c> to save and close and <c>{"type":"cancel"}</c> to close.
/// </summary>
/// <remarks>
/// <para>
/// ONLY A PERSON WHO MAY EDIT THE MEMBER'S SETTINGS: the route is HumansOnly, as every route that sets
/// a member's secret is, and a person reaches every team (TeamGate). An agent's, a Manager's or the
/// Concierge's key is refused before anything starts.
/// </para>
/// <para>
/// The socket closing stops the browser: there is no reattaching to a sign-in. The storage state is
/// never sent on it - a save answers the hosts kept and the providers among them.
/// </para>
/// </remarks>
public static partial class SignInEndpoints
{
    /// <summary>The longest message the page may send: a paste of <see cref="SignInBrowserInput.MaxText"/>, as JSON.</summary>
    public const int MaxMessage = 8 * SignInBrowserInput.MaxText;

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/teams/{team}/members/{member}/sign-in/{field}/{row}", async (
            [Description(Describe.Team)] string team,
            [Description("The plugin member, as addressed in its route.")] string member,
            [Description("The member's table field the row is in.")] string field,
            [Description("The row's name.")] string row,
            HttpContext context, TeamRegistry teams, PluginCatalog plugins, ISignInRows rows, SignInSessions sessions,
            CancellationToken ct) =>
        {
            if (PrincipalClaims.From(context.User) is not { } caller) return Results.Unauthorized();
            if (caller.Kind != PrincipalKind.User)
            {
                return Results.Content(PermitGate.HumansOnlyBody, "application/json", statusCode: StatusCodes.Status403Forbidden);
            }

            if (!Name().IsMatch(field) || !Name().IsMatch(row))
            {
                return Results.BadRequest(new { error = "A field and a row are named with letters, digits, '-' and '_', at most 64 of them." });
            }

            var (found, _, refusal) = await PluginEndpoints.PluginMemberAsync(teams, plugins, team, member, ct);
            if (refusal is not null) return refusal;

            var id = new ContainerId(found!.Team, found.Name);
            if (await rows.EntryAsync(id, field, row, ct) is not { } entry)
            {
                return Results.NotFound(new { error = $"'{member}' has no active interactive entry '{row}' in '{field}' to sign in to." });
            }

            if (!context.WebSockets.IsWebSocketRequest) return Results.BadRequest(new { error = "This endpoint speaks WebSocket." });

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            SignInSession session;
            try
            {
                session = await sessions.StartAsync(caller.Id, id, field, row, entry, ct, Languages(context), TimeZone(context));
            }
            catch (InvalidOperationException exception)
            {
                await SendAsync(socket, new { kind = "ended", reason = exception.Message }, ct);
                await CloseAsync(socket);
                return Results.Empty;
            }

            await SendAsync(socket, new { kind = "started", width = SignInSessions.Width, height = SignInSessions.Height, site = entry.SiteUrl }, ct);
            await PumpAsync(socket, session, PluginEndpoints.SecretActor(context), ct);
            return Results.Empty;
        })
            // A WebSocket upgrade wearing a GET, described here rather than in the API reference.
            .ExcludeFromDescription()
            .HumansOnly();
    }

    /// <summary>
    /// The person's languages, as the page sends them (<c>?lang=</c>, its <c>navigator.languages</c>
    /// joined by commas): language tags only, at most 10. The worker checks them again.
    /// </summary>
    public static IReadOnlyList<string> Languages(HttpContext context) =>
        [.. (context.Request.Query["lang"].ToString() ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Length <= 35 && LanguageTag().IsMatch(l))
            .Take(10)];

    /// <summary>The person's IANA time zone, as the page sends it (<c>?tz=</c>), or null when it is not one.</summary>
    public static string? TimeZone(HttpContext context) =>
        context.Request.Query["tz"].ToString() is { Length: > 0 and <= 64 } zone && ZoneName().IsMatch(zone) && !zone.Contains("..", StringComparison.Ordinal)
            ? zone
            : null;

    /// <summary>The screen to the page and the page's input to the browser, until either side ends.</summary>
    private static async Task PumpAsync(WebSocket socket, SignInSession session, CredentialActor actor, CancellationToken ct)
    {
        using var gone = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var send = new SemaphoreSlim(1, 1);

        // Done ends the session before the page is told what was kept: the answer still goes first.
        var saving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var screen = Task.Run(async () =>
        {
            try
            {
                await foreach (var chunk in session.Stream.Reader.ReadAllAsync(gone.Token))
                {
                    if (chunk.End) break;

                    await send.WaitAsync(gone.Token);
                    try
                    {
                        if (chunk.Bytes is { Length: > 0 } frame) await socket.SendAsync(frame, WebSocketMessageType.Binary, true, gone.Token);
                        else if (chunk.Text is { } notice) await socket.SendAsync(Encoding.UTF8.GetBytes(notice), WebSocketMessageType.Text, true, gone.Token);
                    }
                    finally
                    {
                        send.Release();
                    }
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or IOException)
            {
            }
        });

        var reading = Task.Run(async () =>
        {
            var buffer = new byte[MaxMessage];
            try
            {
                while (socket.State == WebSocketState.Open && !gone.IsCancellationRequested)
                {
                    var length = 0;
                    WebSocketReceiveResult result;
                    do
                    {
                        // A message longer than any input could be is not one: the socket ends.
                        if (length == buffer.Length) throw new InvalidDataException();
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, length, buffer.Length - length), gone.Token);
                        length += result.Count;
                    }
                    while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close) break;

                    var message = buffer[..length];
                    switch (Command(message))
                    {
                        case "done":
                            saving.TrySetResult();
                            var saved = await session.SaveAsync(actor, gone.Token);
                            await send.WaitAsync(gone.Token);
                            try
                            {
                                await SendAsync(socket, saved.Saved
                                    ? new { kind = "saved", hosts = saved.Hosts, providers = saved.Providers }
                                    : (object)new { kind = "error", error = saved.Error }, gone.Token);
                            }
                            finally
                            {
                                send.Release();
                            }

                            if (saved.Saved) return;
                            break;

                        case "cancel":
                            return;

                        default:
                            await session.InputAsync(message, gone.Token);
                            break;
                    }
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or IOException or InvalidDataException)
            {
            }
        });

        await Task.WhenAny(reading, session.Over);
        if (saving.Task.IsCompleted) await Task.WhenAny(reading, Task.Delay(SignInSessions.SaveBound, CancellationToken.None));
        await session.StopAsync();
        var reason = await session.Over;

        await gone.CancelAsync();
        await Task.WhenAll(screen, reading);
        try
        {
            await SendAsync(socket, new { kind = "ended", reason }, CancellationToken.None);
        }
        catch (Exception exception) when (exception is WebSocketException or IOException or ObjectDisposedException)
        {
        }

        await CloseAsync(socket);
    }

    /// <summary><c>done</c> or <c>cancel</c> when the message is that command; null for an input.</summary>
    private static string? Command(byte[] message)
    {
        try
        {
            using var document = JsonDocument.Parse(message);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("type", out var type)) return null;
            return type.ValueKind == JsonValueKind.String && type.GetString() is "done" or "cancel" ? type.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Task SendAsync(WebSocket socket, object message, CancellationToken ct) =>
        socket.State == WebSocketState.Open
            ? socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, JsonSerializerOptions.Web), WebSocketMessageType.Text, true, ct)
            : Task.CompletedTask;

    private static async Task CloseAsync(WebSocket socket)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "ended", CancellationToken.None);
            }
        }
        catch (Exception exception) when (exception is WebSocketException or IOException or ObjectDisposedException)
        {
            // The page went first: there is nobody left to tell.
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex Name();

    [GeneratedRegex("^[A-Za-z]{2,3}(-[A-Za-z0-9]{1,8})*$")]
    private static partial Regex LanguageTag();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_+-]*(/[A-Za-z0-9_+-]+)*$")]
    private static partial Regex ZoneName();
}
