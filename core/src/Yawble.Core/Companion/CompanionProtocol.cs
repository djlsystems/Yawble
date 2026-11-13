namespace Yawble.Core.Companion;

/// <summary>
/// The yawble companion's control protocol: frames as <see cref="Ipc.FrameCodec"/>, JSON requests {id, method, params} and
/// replies {id, result | error{code, message}}, events {event, data}. Before auth only <see cref="Hello"/>,
/// <see cref="PairRequest"/> and <see cref="Auth"/> are answered, each frame at most <see cref="MaxUnauthenticatedFrame"/>
/// bytes; after auth only the four companion.* tool-level methods. Link-compiled into the yawble client: no dependencies.
/// </summary>
public static class CompanionProtocol
{
    public const int Version = 1;
    public const string Product = "yawble";

    /// <summary>The largest frame read before a connection has authenticated; a larger one closes it unread.</summary>
    public const int MaxUnauthenticatedFrame = 4096;

    /// <summary>The largest frame read after auth (a tool call's arguments are far smaller).</summary>
    public const int MaxFrame = 1024 * 1024;

    /// <summary>The longest companion.request waits for a decision, in seconds.</summary>
    public const int MaxWaitSeconds = 60;

    public const string Hello = "hello";
    public const string PairRequest = "pair.request";
    public const string Auth = "auth";
    public const string WhoAmI = "companion.whoami";
    public const string Tools = "companion.tools";
    public const string Call = "companion.call";
    public const string Request = "companion.request";

    /// <summary>Pushed on a pairing connection: {id, code, expires}; the final reply follows on the same connection.</summary>
    public const string PairCode = "pair.code";
}
