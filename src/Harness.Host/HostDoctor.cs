using System.Text.Json;
using System.Text.Json.Serialization;
using Harness.Messaging;

namespace Harness.Host;

public sealed record DoctorDataRoot(string Path, bool Writable, long? FreeBytes);

public sealed record DoctorSchema(
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> Pending,
    IReadOnlyList<string> Unknown,
    bool Accepted);

/// <param name="Error">Why the schema could not be read when <paramref name="Exists"/> is true and
/// <paramref name="Schema"/> is null: a file that is not a SQLite database, or one that cannot be
/// opened. The doctor exists for exactly that state, so it is a field, not an exception.</param>
public sealed record DoctorDatabase(string Path, bool Exists, DoctorSchema? Schema, string? Error = null);

public sealed record DoctorBackups(string Directory, int DailyCount, DateTimeOffset? NewestDailyAt);

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
    // WHERE THIS COMMAND'S PRESETS SIGN IN FROM: `issued` when any preset launching it is set to the
    // credential issued in Admin > Agents (agents.credentialSource), else `home`. IssuedSet is
    // whether that credential is stored, null under home. Read from the database and agents.json,
    // never the key ring: whether a value decrypts is the Host's to say.
    string CredentialSource = TenantSettings.HomeSource,
    bool? IssuedSet = null,
    DateTimeOffset? MeasuredAt = null,
    string? MeasuredOn = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Updating = null)
{
    /// <summary>Installed as the doctor says it: null when nothing was measured, never "not installed" then.</summary>
    [JsonPropertyName("installed")]
    public bool? InstalledAsMeasured => MeasuredAt is null ? null : Installed;
}

public sealed record DoctorReport(
    DateTimeOffset At,
    DoctorDataRoot DataRoot,
    DoctorDatabase Database,
    DoctorBackups Backups,
    DateTimeOffset? VersionsRecordedAt,
    IReadOnlyList<DoctorAgent> Agents,
    AgentLaunchRecord? AgentLaunch = null,
    AgentToolsRecord? AgentTools = null,
    DoctorWip? Wip = null,
    WorkersRecord? Workers = null,
    ConciergeSessionsRecord? Concierge = null);

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
/// </summary>
public static class HostDoctor
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    public static async Task<DoctorReport> ReportAsync(string dataRoot, CancellationToken ct = default)
    {
        var database = Path.Combine(dataRoot, "messages.db");
        var (recordedAt, versions) = await NewestVersionsAsync(dataRoot, ct);
        var history = await CliVersionHistory.In(dataRoot).ReadAsync(CliVersionHistory.MaxTake, ct);
        var launches = AgentLaunchChecksRecord.Read(dataRoot);

        return new DoctorReport(
            DateTimeOffset.UtcNow,
            DataRoot(dataRoot),
            await DatabaseAsync(database, ct),
            Backups(database, Path.Combine(dataRoot, "backups")),
            recordedAt,
            Agents(versions, history, launches, Sources(dataRoot, database), AgentAuthRecord.Read(dataRoot)),
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
        AgentLaunchChecksRecord? launches, IReadOnlyDictionary<string, (string Source, bool? Set)> sources,
        AgentAuthRecord? probed)
    {
        var specs = AgentAuthProbe.LoadSpecs();
        var agents = new List<DoctorAgent>(specs.Count);

        foreach (var command in specs.Keys)
        {
            versions.TryGetValue(command, out var version);
            var now = CliVersionHistory.Now(command, history);

            var (source, issuedSet) = sources.TryGetValue(command, out var found) ? found : (TenantSettings.HomeSource, null);

            var answer = probed?.For(command);
            var measured = answer is { Installed: { } installed }
                ? (Installed: installed, answer.Authenticated, answer.Detail, At: (DateTimeOffset?)probed!.At, On: probed.Worker)
                : (Installed: false, Authenticated: (bool?)null, Detail: NotMeasured(probed, answer), At: null, On: null);

            agents.Add(new DoctorAgent(
                command, measured.Installed, version, measured.Authenticated, measured.Detail, specs[command].CredentialVariable,
                now.UpdatedAt, now.Since, launches?.ForCommand(command), source, issuedSet, measured.At, measured.On,
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

    /// <summary>
    /// Per command, whether a preset launching it is set to an issued credential and, when one is,
    /// whether that credential is stored. Presets are the build's plus agents.json READ, never
    /// repaired - loading the catalog can rewrite the file, which a diagnostic must not. A database
    /// without the setting or the table reads as every command on the home.
    /// </summary>
    private static IReadOnlyDictionary<string, (string Source, bool? Set)> Sources(string dataRoot, string database)
    {
        var answer = new Dictionary<string, (string, bool?)>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(database)) return answer;

        try
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                {
                    DataSource = database,
                    Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                    Pooling = false,
                }.ToString());
            connection.Open();

            Dictionary<string, string> map;
            using (var setting = connection.CreateCommand())
            {
                setting.CommandText = "SELECT value FROM tenant_settings WHERE name = $name";
                setting.Parameters.AddWithValue("$name", TenantSettings.AgentCredentialSourceName);
                map = setting.ExecuteScalar() is string stored
                    ? new Dictionary<string, string>(
                        JsonSerializer.Deserialize<Dictionary<string, string>>(stored) ?? [], StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var issuedCommands = Presets(dataRoot)
                .Where(p => p.Launch is not null && map.GetValueOrDefault(p.Name) == TenantSettings.IssuedSource)
                .Select(RunCredentials.CommandOf)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var command in issuedCommands)
            {
                using var stored = connection.CreateCommand();
                stored.CommandText = "SELECT COUNT(*) FROM agent_credentials WHERE command = $command";
                stored.Parameters.AddWithValue("$command", command);
                answer[command] = (TenantSettings.IssuedSource, Convert.ToInt64(stored.ExecuteScalar()) > 0);
            }
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or JsonException
                                              or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Not readable is not issued: the schema section says what is wrong with the database.
        }

        return answer;
    }

    /// <summary>The build's presets and agents.json's, read and never rewritten.</summary>
    private static IEnumerable<AgentDefinition> Presets(string dataRoot)
    {
        IReadOnlyList<AgentDefinition> custom = [];
        var path = AgentCatalogFile.PathIn(dataRoot);

        try
        {
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var agents = document.RootElement.ValueKind == JsonValueKind.Array
                    ? document.RootElement
                    : document.RootElement.TryGetProperty("agents", out var list) ? list : default;

                if (agents.ValueKind == JsonValueKind.Array)
                {
                    custom = agents.Deserialize<List<AgentDefinition>>(AgentCatalogFile.JsonOptions) ?? [];
                }
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A file the Host could not read either: it runs on the built-ins, and so does this.
        }

        return AgentCatalogFile.BuiltIns().Concat(custom.Where(d => !AgentCatalogFile.IsBuiltIn(d.Name)));
    }

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

        var known = SchemaModules.All.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var appliedSet = applied.ToHashSet(StringComparer.Ordinal);

        var unknown = applied.Where(id => !known.Contains(id)).Order(StringComparer.Ordinal).ToArray();
        var pending = SchemaModules.All.Select(s => s.Id).Where(id => !appliedSet.Contains(id)).ToArray();

        return new DoctorDatabase(
            database,
            Exists: true,
            new DoctorSchema(applied, pending, unknown, Accepted: unknown.Length == 0));
    }

    private static DoctorBackups Backups(string database, string directory)
    {
        var daily = new DailyBackup(database, directory);

        return new DoctorBackups(directory, daily.Existing().Count, daily.Newest());
    }
}
