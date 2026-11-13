namespace Yawble.Core.Provenance;

/// <summary>
/// Who is acting right now, and how they came in (0015_provenance.sql): set where a call enters the core (the window's
/// IPC, a Cloud Link viewer, a Concierge tool call) and read where work is recorded. It flows with the async call, so a
/// message the call publishes, a run it starts or a trigger it saves is stamped without passing it along by hand.
/// </summary>
public sealed record Actor(string Via, string? User)
{
    // Via App (Window) means: the person at THIS computer, at the app's window. The Concierge treats a turn as local only
    // when its message came with Via App; anything else (Cloud, Trigger, Concierge, none) is remote and its local-only
    // tools are refused. A new local entry point for the person must set Actor.Window, or its Concierge turns count as remote.
    public const string App = "app", Cloud = "cloud", Concierge = "concierge", Trigger = "trigger", Agent = "agent", Host = "host", External = "external";

    /// <summary>For Via External: the paired companion client that called (audit_log.client).</summary>
    public string? Client { get; init; }

    /// <summary>For Via External: the person who approved this exact call in the app's window (the only thing that unlocks restore and backup-now for an external client).</summary>
    public string? ApprovedBy { get; init; }

    private static readonly AsyncLocal<Actor?> Ambient = new();

    /// <summary>The account signed in to this computer: the person at the window.</summary>
    public static string LocalUser { get; } = Environment.UserName;

    public static Actor? Current => Ambient.Value;

    /// <summary>The person at this computer's window (the IPC sets it). See <see cref="App"/>: new local entry points must use it.</summary>
    public static Actor Window => new(App, LocalUser);

    public static Actor CloudViewer(string user) => new(Cloud, user);

    /// <summary>A paired companion client (e.g. Claude Code) acting through the control endpoint, on behalf of the local account.</summary>
    public static Actor ExternalClient(string client) => new(External, LocalUser) { Client = client };

    /// <summary>Acts as <paramref name="actor"/> until the result is disposed.</summary>
    public static IDisposable Use(Actor actor)
    {
        var before = Ambient.Value;
        Ambient.Value = actor;
        return new Restore(before);
    }

    private sealed class Restore(Actor? before) : IDisposable
    {
        public void Dispose() => Ambient.Value = before;
    }
}
