using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Harness.Pty;

/// <summary>Moves bytes between one WebSocket and one PTY session.</summary>
public static class PtyWebSocket
{
    private sealed record Control(string? Type, int? Cols, int? Rows);

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Sent as the close reason when this connection lost the terminal to a newer one, so the client
    /// can say what happened instead of showing the blank screen of a dropped socket.
    /// </summary>
    public const string EvictedReason = "attached in another tab or device";

    /// <summary>How long an evicted client is given to answer the close frame before its socket is
    /// torn down anyway.</summary>
    private static readonly TimeSpan EvictionGrace = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Resizes go through <paramref name="attachment"/> rather than straight to the session. It is a
    /// required argument, not an optional one: a connection that skipped it would keep resizing a
    /// child another device is watching, which is the mangled-screen defect <see cref="PtyAttachment"/>
    /// exists to stop, and nothing would fail.
    /// </summary>
    public static async Task PumpAsync(
        WebSocket socket,
        PtyRecord record,
        IPtySession session,
        PtyAttachment attachment,
        int cols,
        int rows,
        CancellationToken ct)
    {
        // Separate from `stop` below so that being evicted is distinguishable afterwards from every
        // other reason this connection ends -- `stop` is always cancelled on the way out, including
        // by this method itself, so it cannot answer the question.
        using var evicted = new CancellationTokenSource();

        // A token private to this one connection, linked to the caller's ct (the whole HTTP
        // request's lifetime) but also independently cancellable by this method itself. Handed to
        // both pump loops below INSTEAD of the raw ct so that once either one finishes, the other
        // can be told to stop right now rather than either (a) being awaited with no bound against
        // a socket/channel that may never produce anything else -- the client-disconnected case,
        // where outbound would otherwise wait forever for a chunk or exit event that is not
        // coming, since the underlying session is deliberately meant to keep running unattended --
        // or (b) being abandoned outright while still doing I/O against a socket the caller's
        // `using var socket` disposes moments later, which would turn an already-caught-looking
        // exception into an unobserved task fault.
        //
        // Deliberately NOT linked to `evicted`. Cancelling a pending WebSocket operation aborts the
        // whole WebSocket, not just that call - so wiring eviction in here would tear the socket down
        // before the close frame explaining the eviction could reach the client, which is exactly the
        // bug where a takeover read as "console ended". Eviction is delivered to the OUTBOUND pump
        // alone, which owns every send; the inbound pump unwinds afterwards, on the client's own
        // close frame.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // AFTER the tokens exist, because attaching is what evicts the previous client and that
        // client's pump must have somewhere to land the moment it happens.
        var viewer = attachment.Attach(evicted, cols, rows);

        // AFTER attaching, never before. Attaching is what resizes the child to this client's
        // geometry, and a resize is what makes the previous device's recorded output unreplayable.
        // Subscribing first would snapshot a transcript laid out for somebody else's screen.
        var subscription = record.Subscribe();

        // Subscribe, the initial replay write, and the two pump loops all sit inside the SAME
        // try, mirroring SseEndpoint/TerminalSseEndpoint: a client that aborts during the replay
        // write (before either pump loop has even started) must still hit the finally below,
        // exactly like one that aborts mid-loop. Without this, a disconnect racing the very first
        // SendAsync would throw past the Unsubscribe at the bottom and leak the subscription --
        // the channel stays registered in the fanout forever, accumulating chunks nobody reads.
        try
        {
            // The replay goes out before the live tail is read, so a reattaching client sees the
            // transcript in order. The client clears its view on open, which is what makes
            // reattach idempotent rather than duplicating output.
            if (subscription.Replay.Length > 0)
            {
                await socket.SendAsync(subscription.Replay, WebSocketMessageType.Binary, true, stop.Token);
            }

            var outbound = PumpOutboundAsync(
                socket, record, subscription, evicted.Token, stop.Token);
            var inbound = PumpInboundAsync(socket, session, attachment, viewer, stop.Token);

            await Task.WhenAny(outbound, inbound);

            if (evicted.IsCancellationRequested)
            {
                // The outbound pump has just sent the close frame carrying the reason, and the
                // inbound pump is still sitting on a ReceiveAsync. Cancelling that receive would
                // abort the socket and truncate the close we are trying to deliver, so the client is
                // given a moment to answer with its own close frame instead - which is what lets the
                // receive return cleanly. Bounded, because a client that never answers must not hold
                // the request open.
                await Task.WhenAny(
                    Task.WhenAll(outbound, inbound),
                    Task.Delay(EvictionGrace, CancellationToken.None));
            }

            // Force the loser to stop, then wait for it to actually finish unwinding.
            // ReceiveAsync/a channel read both observe cancellation near-instantly (no real I/O
            // needed), and both pump loops already catch OperationCanceledException internally,
            // so this both bounds how long the loser can still be touching the socket and turns
            // its eventual completion -- success or a caught exception -- into something this
            // method has actually observed, not an abandoned task that faults later, unseen, after
            // the caller has already disposed the socket. Awaiting the one that already finished
            // is a harmless no-op.
            await stop.CancelAsync();
            await Task.WhenAll(outbound, inbound);
        }
        catch (OperationCanceledException)
        {
            // The client went away before either pump loop got going, or this method's own
            // cancellation (above) is exactly what unwound the loser; nothing to report either way.
        }
        catch (WebSocketException)
        {
            // An aborted connection during the initial replay write is ordinary, not an error
            // worth surfacing -- the same as once the pump loops are running (see their own
            // catches below).
        }
        finally
        {
            record.Unsubscribe(subscription.Live);

            // Detaching here rather than at the call site so a client that aborts mid-replay gives
            // the terminal back too. Detaching resizes nothing -- see PtyAttachment.
            attachment.Detach(viewer);
        }
    }

    /// <summary>
    /// Every send lives here, including both ways this connection can be closed from the server side:
    /// the child exiting, and this client being evicted by a newer one. Keeping them together is what
    /// makes it safe to close while the inbound pump has a receive outstanding - a send and a receive
    /// may overlap on a WebSocket, but two sends may not.
    /// </summary>
    private static async Task PumpOutboundAsync(
        WebSocket socket,
        PtyRecord record,
        PtySubscription subscription,
        CancellationToken evicted,
        CancellationToken ct)
    {
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(ct, evicted);

        try
        {
            await foreach (var evt in subscription.Live.Reader.ReadAllAsync(reading.Token))
            {
                if (evt.Kind == "chunk" && evt.Chunk is { Length: > 0 })
                {
                    await socket.SendAsync(evt.Chunk, WebSocketMessageType.Binary, true, ct);
                }
                else if (evt.Kind == "exit")
                {
                    // CloseOutputAsync, not CloseAsync: CloseAsync does its own internal
                    // receive-wait for the peer's close acknowledgement, and PumpInboundAsync
                    // almost always has an outstanding ReceiveAsync on this SAME socket at this
                    // exact moment -- a second concurrent receive-family call throws
                    // InvalidOperationException ("There is already one outstanding 'ReceiveAsync'
                    // call"), which neither pump catches. CloseOutputAsync only sends the close
                    // frame; PumpInboundAsync's own already-pending ReceiveAsync is what observes
                    // the resulting close handshake and returns cleanly.
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure,
                        $"exit {evt.ExitCode}", ct);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (evicted.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Taken over by a newer client. The channel read was cancelled, which does not touch the
            // socket -- so unlike a cancelled ReceiveAsync, this leaves it healthy enough to still
            // say why it is closing. CloseOutputAsync for the same reason as the exit branch above:
            // the inbound pump has a receive outstanding, and a second receive-family call would
            // throw. That pending receive is what observes the client's answering close frame.
            try
            {
                await socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure, EvictedReason, CancellationToken.None);
            }
            catch (WebSocketException)
            {
                // The client had already gone. Nothing to tell it.
            }
        }
        catch (OperationCanceledException)
        {
            // The client went away; nothing to report.
        }
        catch (WebSocketException)
        {
            // An aborted connection is ordinary, not an error worth surfacing.
        }
    }

    private static async Task PumpInboundAsync(
        WebSocket socket,
        IPtySession session,
        PtyAttachment attachment,
        long viewer,
        CancellationToken ct)
    {
        var buffer = new byte[8192];

        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                // Text frames are control JSON and must never reach the child as keystrokes.
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    Apply(attachment, viewer, Encoding.UTF8.GetString(buffer, 0, result.Count));
                    continue;
                }

                session.Write(buffer.AsSpan(0, result.Count));
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    private static void Apply(PtyAttachment attachment, long viewer, string json)
    {
        Control? control;
        try
        {
            control = JsonSerializer.Deserialize<Control>(json, JsonOptions);
        }
        catch (JsonException)
        {
            // A malformed control frame is dropped, never thrown: it must not kill a live terminal.
            return;
        }

        if (control is { Type: "resize", Cols: > 0, Rows: > 0 })
        {
            // Clamped, not trusted: this value ends up in ResizePseudoConsole (Windows) or
            // TIOCSWINSZ (Linux) -- an OS call, exactly like the connect path's cols/rows, which
            // PtyDimensionLimits already clamps. See its own doc comment for why the two share one
            // bound rather than each holding its own copy of the number.
            attachment.Report(
                viewer,
                PtyDimensionLimits.Clamp(control.Cols.Value),
                PtyDimensionLimits.Clamp(control.Rows.Value));
        }
    }
}
