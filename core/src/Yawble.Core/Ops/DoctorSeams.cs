namespace Yawble.Core.Ops;

// The seams of the doctor. Each is small so a test can fake it: no real system program,
// no network, no real credential store, no clock but the one passed in.

/// <summary>One backup as ops.backups lists it. The backup service provides the real lister.</summary>
public sealed record BackupInfo(string Name, long Size, string Reason, DateTimeOffset Time);

/// <summary>Reads the backups the app has taken; the doctor only needs their ages.</summary>
public interface IBackupLister
{
    IReadOnlyList<BackupInfo> List();
}

/// <summary>
/// A Connection as the doctor needs it (ConnectionService.Index): no secret and no field value. <see cref="StoreKey"/> is
/// the random part of its credential entries, "connection/{StoreKey}/{field}".
/// </summary>
public sealed record ConnectionInfo(string Id, string Name, string Kind, string Status, string StoreKey)
{
    public bool NeedsReconnect => Status == "needs_reconnect";
}

public interface IConnectionSource
{
    IReadOnlyList<ConnectionInfo> List();
}

/// <summary>
/// A real credential store that can be asked about its keys. Never returns a value. The OS stores share one service name
/// across every data folder on the computer, so a key being listed here does not make it this app instance's: the doctor
/// also needs the issued-keys ledger (<see cref="Credentials.IIssuedKeyLedger"/>).
/// </summary>
public interface ICredentialIndex
{
    /// <summary>The keys that start with <paramref name="prefix"/>. A store that cannot list throws NotSupportedException.</summary>
    IReadOnlyList<string> KeysWithPrefix(string prefix);

    /// <summary>Whether an entry exists under this exact key (for a store that cannot list).</summary>
    bool Has(string key);

    bool Delete(string key);
}
/// <summary>A team as the doctor sees it.</summary>
public sealed record DoctorTeam(
    string Name, bool Paused, bool Archived, DateTimeOffset LastActivityAt,
    int UnattendedWorkflows, IReadOnlyList<StuckMember> Stuck,
    int BehindBase = 0, TimeSpan? BehindAge = null, string TeamBranch = "", string BaseBranch = "");

/// <summary>A member whose run has been going longer than the watchdog allows.</summary>
public sealed record StuckMember(string Name, TimeSpan Running);

public interface IDoctorTeams
{
    Task<IReadOnlyList<DoctorTeam>> ListAsync(CancellationToken ct);

    /// <summary>The existing team archive.</summary>
    Task ArchiveAsync(string team, CancellationToken ct);

    /// <summary>"Sync now" for a team whose branch fell behind its base: the same sync as the team page's button.</summary>
    Task<(bool Done, string Message)> SyncAsync(string team, CancellationToken ct) => throw new NotSupportedException("this core cannot sync a team");
}

/// <summary><paramref name="GatewayProblem"/>: null when a gateway answered a real request just now, otherwise why not.</summary>
public sealed record SandboxState(string? LauncherProblem, string GuardrailState, string? GuardrailDetail, string? GatewayProblem);

public sealed record LinkState(string State, string? Error);

/// <summary>What the doctor reads from this computer.</summary>
public interface IDoctorSystem
{
    /// <summary>PRAGMA quick_check: "ok", or what it found.</summary>
    string QuickCheck();

    IReadOnlyList<string> AppliedMigrations();

    IReadOnlyList<string> ShippedMigrations();

    long FreeBytesOnDataDrive();

    /// <summary>Bytes under teams/, by team name.</summary>
    IReadOnlyDictionary<string, long> TeamFolderSizes();

    long LogBytes();

    SandboxState Sandbox();

    LinkState Link();
}

