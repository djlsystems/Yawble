namespace Harness.Host;

/// <summary>
/// A PLUGIN'S RUN SAYS WHICH OF ITS CONNECTIONS FAILED IT, AND HOW, on its <c>result</c> record:
/// additive fields of <see cref="PluginManifest.ProtocolV1"/>, read only on <c>ok: false</c>.
///
/// <code>{"t":"result","ok":false,"error":"Stripe refused the key.","connection":"billing","connectionFailure":"refused"}</code>
///
/// <list type="bullet">
/// <item><c>connection</c> - the SLOT, as the manifest declares it, whose connection failed.</item>
/// <item><c>connectionFailure</c> - exactly <c>refused</c>, <c>unreachable</c> or <c>sign-in</c>.</item>
/// </list>
///
/// The kind is the plugin's to say; the platform never infers it from the sentence. <c>refused</c> and
/// <c>sign-in</c> mark the connection <c>needs-reconnect</c>, so every member using it reads Needs you and
/// waits; <c>unreachable</c> fails only the run. A result naming no kind, an unknown kind, or a slot the run
/// was not handed a connection for fails the run as before and marks nothing.
/// </summary>
public static class RunConnectionFailures
{
    /// <summary>The result record's field naming the slot.</summary>
    public const string SlotField = "connection";

    /// <summary>The result record's field naming the kind of failure.</summary>
    public const string KindField = "connectionFailure";

    /// <summary>The service refused what the connection holds: a wrong password, a revoked token, an
    /// expired session, a key it refuses. A person edits or reconnects it.</summary>
    public const string Refused = "refused";

    /// <summary>The service was not reached: down, a timeout. Only this run fails.</summary>
    public const string Unreachable = "unreachable";

    /// <summary>The service asks for a sign-in only a person can complete. A person signs in themselves.</summary>
    public const string SignIn = "sign-in";

    public static readonly IReadOnlyList<string> Kinds = [Refused, Unreachable, SignIn];

    /// <summary>Whether a kind holds the connection for a person.</summary>
    public static bool Holds(string? kind) => kind is Refused or SignIn;

    /// <summary>What a sign-in mark says when the plugin's own sentence does not say a person signs in themselves.</summary>
    public const string SignInSentence =
        "The service wants a person to sign in to it themselves before it lets this connection in again. "
        + "Sign in to it yourself, then test the connection.";

    /// <summary>
    /// The sentence a connection is marked with. A refusal: the plugin's own sentence. A sign-in: the
    /// plugin's sentence when it already tells a person to sign in themselves (it says sign in, and
    /// yourself or themselves), else <see cref="SignInSentence"/> followed by what the plugin said.
    /// The KIND is never read from the sentence; only whether the sentence already says what a
    /// person does.
    /// </summary>
    public static string Sentence(string kind, string? error)
    {
        var said = string.IsNullOrWhiteSpace(error) ? null : error.Trim();

        if (kind != SignIn) return said ?? "The service refused what this connection holds.";

        if (said is not null && SaysSignInYourself(said)) return said;

        return said is null ? SignInSentence : $"{SignInSentence} The service said: {said}";
    }

    private static bool SaysSignInYourself(string sentence) =>
        (sentence.Contains("sign in", StringComparison.OrdinalIgnoreCase)
            || sentence.Contains("sign-in", StringComparison.OrdinalIgnoreCase)
            || sentence.Contains("signs in", StringComparison.OrdinalIgnoreCase))
        && (sentence.Contains("yourself", StringComparison.OrdinalIgnoreCase)
            || sentence.Contains("themselves", StringComparison.OrdinalIgnoreCase));
}
