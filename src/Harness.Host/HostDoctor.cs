using System.Text.Json;
using System.Text.Json.Serialization;
using Harness.Host.SqlServer;
using Harness.Messaging;
using Harness.SqlServer;
using Microsoft.Data.SqlClient;

namespace Harness.Host;

public sealed record DoctorDataRoot(string Path, bool Writable, long? FreeBytes);

public sealed record DoctorSchema(
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> Pending,
    IReadOnlyList<string> Unknown,
    bool Accepted);

/// <param name="Path">The SQLite file; on SQL Server <c>&lt;server&gt;/&lt;database&gt;</c>, the two names
/// read from the connection string and nothing else of it.</param>
/// <param name="Error">Why the schema could not be read when <paramref name="Exists"/> is true and
/// <paramref name="Schema"/> is null: a file that is not a SQLite database, or one that cannot be
/// opened; on SQL Server a server that cannot be reached, or a database the Host would refuse. The
/// doctor exists for exactly that state, so it is a field, not an exception.</param>
/// <param name="Provider"><c>sqlserver</c> on SQL Server; absent from the JSON on SQLite, so the report
/// a SQLite instance gives is the one it always gave.</param>
/// <param name="Server">The SQL Server's name, from the connection string; absent on SQLite.</param>
/// <param name="Name">The SQL Server database's name, from the connection string; absent on SQLite.</param>
public sealed record DoctorDatabase(
    string Path, bool Exists, DoctorSchema? Schema, string? Error = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Provider = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Server = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null);

public sealed record DoctorBackups(string Directory, int DailyCount, DateTimeOffset? NewestDailyAt)
{
    /// <summary>No backups folder and no copies in it: what <see cref="DoctorReport.Backups"/> reads on
    /// SQL Server, whose backups are the server's own.</summary>
    public static DoctorBackups None { get; } = new("", 0, null);
}

/// <param name="CredentialVariable">The environment variable that signs this command in, from
/// <c>auth-probes.json</c>, or null when the probe names none. The operator CLI prints it in its
/// sign-in hint rather than carrying its own copy of the list.</param>
/// <param name="Installed">Whether the Host's last sign-in probe found the command on a worker's
/// PATH; false too when nothing was measured, which <see cref="MeasuredAt"/> null says. The JSON
/// carries <see cref="InstalledAsMeasured"/> under <c>installed</c>: null when not measured.</param>
/// <param name="MeasuredAt">When the probe this is from ran (<see cref="AgentAuthRecord"/>); null when
/// no worker answered for this command, or the Host has recorded no probe.</param>
/// <param name="MeasuredOn">The worker that answered it; null when none did.</param>
/// <param name="Updating">The gate's sentence when the Host's last probe found the command held by the
/// platform's update, so it was not asked: never "not installed". Null otherwise.</param>
public sealed record DoctorAgent(
    string Agent, [property: JsonIgnore] bool Installed, string? Version, bool? Authenticated, string Detail,
    string? CredentialVariable = null,
    // WHEN THE INSTALLED VERSION ARRIVED: the first recorded line - a start, or a person's update
    // through the platform - that had it after a different one. Null when the kept history never
    // saw it change; `VersionsSince` is then how far back that history reaches.
    DateTimeOffset? UpdatedAt = null,
    DateTimeOffset? VersionsSince = null,
    // WHETHER IT STARTS THE WAY A MEMBER RUN STARTS IT: the Host's last launch check
    // (AgentLaunchChecksRecord.ForCommand), the values GET /api/agents/auth carries. Null when the
    // Host has recorded none - not known, never ok.
    AgentLaunchReport? Launch = null,
    DateTimeOffset? MeasuredAt = null,
    string? MeasuredOn = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Updating = null)
{
    /// <summary>Installed as the doctor says it: null when nothing was measured, never "not installed" then.</summary>
    [JsonPropertyName("installed")]
    public bool? InstalledAsMeasured => MeasuredAt is null ? null : Installed;
}

/// <param name="BackupFiles">The backups folder and its daily copies, as <c>backups</c> in the JSON.
/// Null on SQL Server: there is no file to copy, the daily backup takes none, and point-in-time restore
/// is the backup, so the report has no backup-file rows rather than a folder that reads as empty.</param>
public sealed record DoctorReport(
    DateTimeOffset At,
    DoctorDataRoot DataRoot,
    DoctorDatabase Database,
    [property: JsonPropertyName("backups")] DoctorBackups? BackupFiles,
    DateTimeOffset? VersionsRecordedAt,
    IReadOnlyList<DoctorAgent> Agents,
    AgentLaunchRecord? AgentLaunch = null,
    AgentToolsRecord? AgentTools = null,
    DoctorWip? Wip = null,
    WorkersRecord? Workers = null,
    ConciergeSessionsRecord? Concierge = null)
{
    /// <summary>The backups section, <see cref="DoctorBackups.None"/> on SQL Server. Not in the JSON,
    /// which carries <see cref="BackupFiles"/> as <c>backups</c>.</summary>
    [JsonIgnore]
    public DoctorBackups Backups => BackupFiles ?? DoctorBackups.None;
}

/// <summary>
/// The doctor's <c>wip</c>: <c>GET /api/wip</c>'s <c>limit</c> and <c>runMemory</c>, as the running Host
/// last recorded them (<see cref="WipRecord"/>), and when.
/// </summary>
public sealed record DoctorWip(DateTimeOffset At, WipRunLimit Limit, RunMemoryReport RunMemory);

/// <summary>
/// What `--doctor` reports: the state of one data root, read and never changed. The operator CLI
/// runs this with an exec in the container and renders it; the checks themselves live here so the
/// CLI does not reimplement them in another language.
///
/// EVERY SECTION IS BUILT FROM THE CODE THAT ALREADY OWNS THE FACT. The schema verdict is the
/// migrator's own arithmetic (applied minus handed-in is "unknown", handed-in minus applied is
/// "pending"); the backup count is <see cref="DailyBackup"/>'s own listing; the writable check is
/// the one <c>/healthz</c> makes. A second implementation of any of these would be a second
/// answer waiting to disagree.
///
/// NOTHING IS WRITTEN. A missing database is reported, not created: <see cref="SchemaMigrator"/>
/// opens with create, so it is not asked at all when the file is absent. Null means not measured,
/// the rule every probe in this host follows.
///
/// ON SQL SERVER the database section says <c>provider: sqlserver</c> with the server's and the
/// database's names and the steps applied there, read without creating the migrator's ledger; there
/// are no backup files to report (<c>backups</c> is null), and no <c>messages.db</c> is looked for.
/// </summary>
public static class HostDoctor
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    /// <param name="backups">
    /// The backups folder the Host uses (<c>Backups:Directory</c>, else <c>&lt;dataRoot&gt;/backups</c>),
    /// as Program.cs read it; the defaults when omitted.
    /// </param>
    /// <param name="database">
    /// The database the Host runs on, as Program.cs read it (<c>HARNESS_DATABASE</c>); SQLite's file in
    /// the data root when omitted.
    /// </param>
    public static async Task<DoctorReport> ReportAsync(
        string dataRoot, CancellationToken ct = default, BackupSettings? backups = null, DatabaseOptions? database = null)
    {
        backups ??= BackupSettings.Defaults(dataRoot);
        var file = Path.Combine(dataRoot, "messages.db");
        var onSqlServer = database?.Provider == DatabaseProvider.SqlServer;
        var (recordedAt, versions) = await NewestVersionsAsync(dataRoot, ct);
        var history = await CliVersionHistory.In(dataRoot).ReadAsync(CliVersionHistory.MaxTake, ct);
        var launches = AgentLaunchChecksRecord.Read(dataRoot);

        return new DoctorReport(
            DateTimeOffset.UtcNow,
            DataRoot(dataRoot),
            onSqlServer ? await SqlServerDatabaseAsync(database!.Connection!, ct) : await DatabaseAsync(file, ct),
            // NO BACKUP-FILE ROWS ON SQL SERVER: its daily backup takes no copy, so a folder of none would
            // read as a backup that is missing rather than one the server keeps.
            onSqlServer ? null : Backups(backups.DailyBackupFor(file)),
            recordedAt,
            Agents(versions, history, launches, AgentAuthRecord.Read(dataRoot)),
            // Who the Host said, at its last start, agent children run as - and, when it
            // refuses them, why. Recorded by the Host, because the doctor is a different process.
            AgentLaunchRecord.Read(dataRoot),
            AgentToolsSection(dataRoot),
            WipSection(dataRoot),
            // Control's workers, as control last recorded them; null when it recorded none (all, or never).
            WorkersRecord.Read(dataRoot),
            // The running Concierge sessions - whose, on which worker, last activity, when the idle
            // window would end each - as the Host last recorded them; null when it recorded none.
            ConciergeSessionsRecord.Read(dataRoot));
    }

    /// <summary>
    /// THE AGENT TOOLS SECTION: per preset, isolated, foreign tools found (named) or not verified,
    /// with its recorded gaps, and the Concierge's tools as information. The Host's last pre-flight
    /// (<see cref="AgentToolPreflight"/>), read and never re-run: listing the CLIs here would run
    /// them as whoever started the doctor, and needs the catalog, which a diagnostic must not load.
    /// Null when the Host has not recorded one - not measured, never clean.
    /// </summary>
    public static AgentToolsRecord? AgentToolsSection(string dataRoot) => AgentToolsRecord.Read(dataRoot);

    /// <summary>
    /// THE WIP SECTION: the running limit and what each run's memory is held to, as the Host last
    /// recorded them (<see cref="WipRecord"/>) from the code that decides them. Never worked out here: this
    /// process has neither the Host's cgroup nor its settings. Null when the Host has recorded none.
    /// </summary>
    public static DoctorWip? WipSection(string dataRoot) =>
        WipRecord.Read(dataRoot) is { } wip ? new DoctorWip(wip.At, wip.Limit, wip.RunMemory) : null;

    /// <summary>
    /// The newest start's versions, as `scripts/ensure-agent-clis.sh` recorded them. Reading the
    /// file rather than running each `--version` keeps the doctor from executing five binaries off
    /// PATH, the same rule <see cref="AgentInstallProbe"/> holds. A host outside the container has
    /// no file, and that is null, not a failure.
    /// </summary>
    private static async Task<(DateTimeOffset? At, IReadOnlyDictionary<string, string?> Versions)> NewestVersionsAsync(
        string dataRoot, CancellationToken ct)
    {
        var starts = await CliVersionHistory.In(dataRoot).ReadAsync(1, ct);

        return starts is [var newest, ..]
            ? (newest.At, newest.Versions)
            : (null, new Dictionary<string, string?>(StringComparer.Ordinal));
    }

    /// <summary>
    /// Per COMMAND in `auth-probes.json`, in file order, which is the four agents the product
    /// supports out of the box. Not per preset: that needs the catalog, and loading it writes.
    ///
    /// `Installed` and `Authenticated` are the Host's LAST SIGN-IN PROBE (<see cref="AgentAuthRecord"/>),
    /// with when it ran and which worker answered: the CLIs are a worker's, and the doctor is another
    /// process that asks none and runs nothing. No record, or no worker that answered for a command:
    /// not measured, with why. `Version` is what the last start recorded. They are separate facts and
    /// are reported as such: a version is not blanked because the last probe did not find the command.
    /// </summary>
    private static IReadOnlyList<DoctorAgent> Agents(
        IReadOnlyDictionary<string, string?> versions, IReadOnlyList<CliVersionsAtStart> history,
        AgentLaunchChecksRecord? launches, AgentAuthRecord? probed)
    {
        var specs = AgentAuthProbe.LoadSpecs();
        var agents = new List<DoctorAgent>(specs.Count);

        foreach (var command in specs.Keys)
        {
            versions.TryGetValue(command, out var version);
            var now = CliVersionHistory.Now(command, history);

            var answer = probed?.For(command);
            var measured = answer is { Installed: { } installed }
                ? (Installed: installed, answer.Authenticated, answer.Detail, At: (DateTimeOffset?)probed!.At, On: probed.Worker)
                : (Installed: false, Authenticated: (bool?)null, Detail: NotMeasured(probed, answer), At: null, On: null);

            agents.Add(new DoctorAgent(
                command, measured.Installed, version, measured.Authenticated, measured.Detail, specs[command].CredentialVariable,
                now.UpdatedAt, now.Since, launches?.ForCommand(command), measured.At, measured.On,
                answer?.Updating));
        }

        return agents;
    }

    /// <summary>Why a command's sign-in is not measured, in the probe's own words where it ran.</summary>
    private static string NotMeasured(AgentAuthRecord? probed, CommandSignIn? answer) =>
        probed is null
            ? "Not measured: the Host has recorded no sign-in probe yet. It asks a worker when one joins or comes back, when an update ends, and when the Agents screen or the Concierge reads sign-ins."
            : answer is null
                ? $"Not measured: the Host's last sign-in probe, at {probed.At.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC, did not ask this command."
                : $"{answer.Detail} (the Host's last sign-in probe, at {probed.At.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC)";

    public static string ToJson(DoctorReport report) => JsonSerializer.Serialize(report, Json);

    private static DoctorDataRoot DataRoot(string dataRoot)
    {
        long? free;
        try
        {
            free = new DriveInfo(dataRoot).AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            free = null;
        }

        return new DoctorDataRoot(dataRoot, HealthEndpoints.DataRootWritable(dataRoot).Ok, free);
    }

    private static async Task<DoctorDatabase> DatabaseAsync(string database, CancellationToken ct)
    {
        if (!File.Exists(database)) return new DoctorDatabase(database, Exists: false, Schema: null);

        IReadOnlyList<string> applied;
        try
        {
            applied = await new SchemaMigrator(database).AppliedAsync(ct);
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException
                                              or IOException
                                              or UnauthorizedAccessException
                                              or InvalidOperationException)
        {
            // A file that is not a database, or one this process cannot open, is the report - not
            // a stack trace in place of one. Cancellation passes through.
            return new DoctorDatabase(database, Exists: true, Schema: null, Error: exception.Message);
        }

        return new DoctorDatabase(database, Exists: true, SchemaOf(applied, SchemaModules.All));
    }

    /// <summary>
    /// THE SQL SERVER DATABASE, read and never written: its applied steps against
    /// <see cref="SqlServerSchemaModules.All"/>, by the migrator's own read that creates no ledger.
    ///
    /// <para>
    /// NEVER THE CONNECTION STRING: it may hold a password. The server's and the database's names are
    /// read from it and are all of it the report carries; a failure is the start's own one sentence
    /// (<see cref="SqlServerStartRefusal"/>), which names the server and nothing else.
    /// </para>
    /// </summary>
    private static async Task<DoctorDatabase> SqlServerDatabaseAsync(string connection, CancellationToken ct)
    {
        const string provider = "sqlserver";
        string server, name;

        try
        {
            var builder = new SqlConnectionStringBuilder(connection);
            (server, name) = (builder.DataSource, builder.InitialCatalog);
        }
        catch (ArgumentException exception)
        {
            return new DoctorDatabase(
                "(unreadable)", Exists: true, Schema: null, SqlServerStartRefusal.Sentence(exception, connection), provider);
        }

        var path = $"{server}/{name}";

        IReadOnlyList<string> applied;
        try
        {
            applied = await new SqlServerMigrator(connection).AppliedIfAnyAsync(ct);
        }
        catch (Exception exception) when (SqlServerStartRefusal.Sentence(exception, connection) is { } sentence)
        {
            // Unreachable, unopenable, the wrong collation, row versioning off: the report, not a trace.
            return new DoctorDatabase(path, Exists: true, Schema: null, sentence, provider, server, name);
        }

        return new DoctorDatabase(path, Exists: true, SchemaOf(applied, SqlServerSchemaModules.All), Error: null, provider, server, name);
    }

    /// <summary>The migrator's own arithmetic: applied minus known is unknown, known minus applied is pending.</summary>
    private static DoctorSchema SchemaOf(IReadOnlyList<string> applied, IReadOnlyList<Harness.Contracts.MigrationStep> steps)
    {
        var known = steps.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var appliedSet = applied.ToHashSet(StringComparer.Ordinal);

        var unknown = applied.Where(id => !known.Contains(id)).Order(StringComparer.Ordinal).ToArray();
        var pending = steps.Select(s => s.Id).Where(id => !appliedSet.Contains(id)).ToArray();

        return new DoctorSchema(applied, pending, unknown, Accepted: unknown.Length == 0);
    }

    private static DoctorBackups Backups(DailyBackup daily) =>
        new(daily.Directory, daily.Existing().Count, daily.Newest());
}
