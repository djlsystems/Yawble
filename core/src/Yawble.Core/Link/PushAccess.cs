namespace Yawble.Core.Link;

/// <summary>
/// The least access a Cloud Link viewer needs to receive each push the core sends up. Every pushed message
/// ({type:"event", event, data}) carries it as "access": "read" for a push a read-only viewer (a workgroup Visitor) may
/// see, "full" for one only a full viewer may. The site delivers a "full" push to full viewers only.
/// <para>
/// A push is "read" only when it is listed so here: an event whose name is not in the table is "full", so a new event
/// reaches read-only viewers only once it is listed as a read. A push is a read when a Visitor may already read what it
/// carries through a call (<see cref="RemoteAccess.Kinds"/>).
/// </para>
/// </summary>
public static class PushAccess
{
    /// <summary>The field of a pushed message that names its least access.</summary>
    public const string Field = "access";

    /// <summary>Every event the core publishes, with the least access that receives it over the Cloud Link.</summary>
    internal static readonly IReadOnlyDictionary<string, string> Levels = Table(
        read:
        [
            // "Something changed, ask again", with no data: the lists behind them are reads (board.list, app.overview,
            // network.asks, tools.asks, folder.asks).
            Board.BoardService.ChangedEvent,
            Bus.BusEngine.LeaseChangedEvent,
            Integrations.NetworkAskService.ChangedEvent,
            Integrations.ToolAskService.ChangedEvent,
            Integrations.FolderAskService.ChangedEvent,
            // A Concierge chat's reply as it streams and the updates posted to it: what concierge.get reads.
            Concierge.ConciergeService.TurnEvent,
            Concierge.ConciergeService.UpdateEvent,
            // The org dashboard's summary and the live logs a viewer watches (org.summary.watch, logs.tail: reads).
            OrgSummaryService.ChangedEvent,
            Logs.LogTail.EntryEvent,
            // A change in a member's code, for a code browser session a cloud viewer opened (code.open, code.read: reads).
            // It goes up at all only while members' code is shown (CloudLink.Forwards).
            Yawble.CodeBrowser.CodeBrowserService.ChangedEvent,
        ],
        full:
        [
            // Terminals: what a shell or an agent prints, and a session's state. A Visitor is refused every terminal call.
            Ui.TerminalHub.OutputEvent,
            Ui.TerminalHub.StateEvent,
            // The Cloud Link's own state (the site, who is viewing): link. calls are the computer's alone.
            CloudLink.StateEvent,
            // Connections hold sign-ins: their calls are the computer's alone.
            Connections.ConnectionService.ChangedEvent,
            // An installer's progress on this computer (tools.install is the window's alone).
            Integrations.ToolAskService.ProgressEvent,
            // For the app's own window: the install was removed, a website action to run, companion clients, an agent's
            // Terminal / Test session. (The last three never go up: CloudLink.Forwards, and a channel of their own.)
            Lifetime.RemovalWatch.EventName,
            Integrations.ShellWebsiteHost.RequestEvent,
            Companion.CompanionService.ChangedEvent,
            Agents.AgentTestSessions.ChangedEvent,
        ]);

    /// <summary>The least access that receives this event: <see cref="RemoteAccess.Read"/> when listed so, else <see cref="RemoteAccess.Full"/>.</summary>
    public static string Of(string? eventName) =>
        eventName is not null && Levels.TryGetValue(eventName, out var access) ? access : RemoteAccess.Full;

    private static Dictionary<string, string> Table(string[] read, string[] full)
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in read) table.Add(e, RemoteAccess.Read);
        foreach (var e in full) table.Add(e, RemoteAccess.Full);
        return table;
    }
}
