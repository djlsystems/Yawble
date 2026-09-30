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
public sealed record DoctorAgent(
    string Agent, bool Installed, string? Version, bool? Authenticated, string Detail,
    string? CredentialVariable = null);

public sealed record DoctorReport(
    DateTimeOffset At,
    DoctorDataRoot DataRoot,
    DoctorDatabase Database,
    DoctorBackups Backups,
    DateTimeOffset? VersionsRecordedAt,
    IReadOnlyList<DoctorAgent> Agents,
    AgentLaunchRecord? AgentLaunch = null,
    AgentToolsRecord? AgentTools = null);

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

        return new DoctorReport(
            DateTimeOffset.UtcNow,
            DataRoot(dataRoot),
            await DatabaseAsync(database, ct),
            Backups(database, Path.Combine(dataRoot, "backups")),
            recordedAt,
            await AgentsAsync(versions, ct),
            // Who the Host said, at its last start, agent children run as - and, when it
            // refuses them, why. Recorded by the Host, because the doctor is a different process.
            AgentLaunchRecord.Read(dataRoot),
            AgentToolsSection(dataRoot));
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
    /// `Installed` is measured now; `Version` is what the last start recorded. They are two facts
    /// and are reported as two: a version is not blanked because the command is missing today,
    /// and the reader sees both, which is the honest shape ("was 2.1.3 at the last start, is not on
    /// PATH now").
    /// </summary>
    private static async Task<IReadOnlyList<DoctorAgent>> AgentsAsync(
        IReadOnlyDictionary<string, string?> versions, CancellationToken ct)
    {
        var specs = AgentAuthProbe.LoadSpecs();
        var agents = new List<DoctorAgent>(specs.Count);

        // Asked as `agent`, the way the Host asks, and never by running an agent-installed
        // program as whoever started the doctor - see AgentAuthProbe.ProbeCommandAsync.
        var runAs = AgentLaunchUser.Resolve(Environment.GetEnvironmentVariable("HARNESS_AGENT_USER"));

        foreach (var command in specs.Keys)
        {
            var (installed, authenticated, detail) = await AgentAuthProbe.ProbeCommandAsync(command, specs, ct, runAs);
            versions.TryGetValue(command, out var version);

            agents.Add(new DoctorAgent(
                command, installed, version, authenticated, detail, specs[command].CredentialVariable));
        }

        return agents;
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
