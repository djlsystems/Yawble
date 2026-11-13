namespace Yawble.Companion;

/// <summary>What `yawble &lt;command&gt;` exits with (scripts may branch on these).</summary>
internal static class ExitCodes
{
    public const int Ok = 0;

    /// <summary>The call failed (an error result, a timeout, a lost connection, an unusable YAWBLE_HOME, Yawble busy).</summary>
    public const int Failed = 1;

    /// <summary>The command line was not understood.</summary>
    public const int Usage = 2;

    /// <summary>No running Yawble app on this computer (no control.json, or nothing listening at its address).</summary>
    public const int NotRunning = 3;

    /// <summary>No key for this client on this computer, or the app no longer accepts it (revoked).</summary>
    public const int NotPaired = 4;

    /// <summary>The app refused: the client's level does not allow it, or the person denied it.</summary>
    public const int Refused = 5;

    /// <summary>A destructive request is still waiting for the person's approval in the Yawble window.</summary>
    public const int Pending = 6;
}
