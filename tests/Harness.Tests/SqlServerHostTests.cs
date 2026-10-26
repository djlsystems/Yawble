using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.SqlServer;
using Harness.SqlServer.Messaging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>Tests that read what the Host prints, by taking the process's console for the length of a
/// start, run alone: the console is one per process, and two tests capturing it at once would each read
/// the other's lines or restore the wrong writer.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessConsoleCollection
{
    public const string Name = "Process console";
}

/// <summary>
/// THE HOST ON SQL SERVER, END TO END: started with <c>HARNESS_DATABASE=sqlserver</c> on an empty
/// database, it moves that database's schema forward itself, serves a person's first account, a team, a
/// backlog item and a tell, and every row lands in SQL Server, none in a SQLite file beside it. A start it
/// cannot make - the server unreachable, the database of the wrong kind or from a newer build - is one
/// sentence and a non-zero exit, never a stack trace and never a half-served instance.
/// </summary>
[Collection(ProcessConsoleCollection.Name)]
public sealed class SqlServerHostTests
{
    private const string Email = "first@example.test";
    private const string Password = "correct horse battery";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task The_host_starts_and_a_workflow_round_trips()
    {
        // EMPTY: no schema step has run, so the Host's own start is what creates every table.
        await using var database = await SqlServerTestDatabase.CreateAsync(Ct, applyModules: false);
        using var root = new TempRoot();
        using var console = new ConsoleCapture();

        await using var factory = Host(root.Path, ("HARNESS_DATABASE", "sqlserver"), ("HARNESS_DATABASE_CONNECTION", database.ConnectionString));
        _ = factory.Services;

        Assert.IsType<SqlServerMessageStore>(factory.Services.GetRequiredService<IMessageLog>());

        // THE FIRST ACCOUNT, through the route the sign-in screen uses, then signed in with it.
        using var person = factory.CreateClient();
        Assert.True((await JsonAsync(await person.GetAsync("/api/auth/state", Ct))).GetProperty("needsAdmin").GetBoolean());
        await JsonAsync(await person.PostAsJsonAsync("/api/auth/admin", new { email = Email, password = Password }, Ct));
        await JsonAsync(await person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct));

        // A TEAM, paused so the tell below queues and launches no agent.
        var agent = factory.Services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
        var team = (await JsonAsync(await person.PostAsJsonAsync(
            "/api/teams", new { name = "Alpha", agent, memberAgents = new[] { agent } }, Ct))).GetProperty("id").GetString()!;
        (await person.PostAsync($"/api/teams/{team}/pause", null, Ct)).EnsureSuccessStatusCode();

        // A BACKLOG ITEM, read back.
        await JsonAsync(await person.PostAsJsonAsync("/api/backlog", new { title = "Crawl the openings", body = "A spec." }, Ct));
        var backlog = await person.GetStringAsync("/api/backlog", Ct);
        Assert.Contains("Crawl the openings", backlog, StringComparison.Ordinal);

        // A TELL: the instruction is the log's row, and the board's card is that workflow.
        var told = await JsonAsync(await person.PostAsJsonAsync(
            $"/api/teams/{team}/containers/{TeamRegistry.DefaultManagerName}/tell", new { instruction = "Read the openings." }, Ct));
        var workflow = told.GetProperty("correlationId").GetInt64();

        var instruction = MessageTypes.InstructionFor(new ContainerId(team, TeamRegistry.DefaultManagerName));
        var rows = await factory.Services.GetRequiredService<IMessageLog>().ReadAfterAsync(0, [instruction], 100, Ct);
        Assert.Contains(rows, r => r.CorrelationId == workflow && r.Payload.Contains("Read the openings.", StringComparison.Ordinal));

        var board = await JsonAsync(await person.GetAsync($"/api/teams/{team}/kanban/board", Ct));
        Assert.Contains(board.GetProperty("cards").EnumerateArray(), c => c.GetProperty("workflowSeq").GetInt64() == workflow);

        // THE LEDGER: a person's close is the workflow's ledger row, in the export and in the store.
        var closed = await person.PostAsJsonAsync($"/api/teams/{team}/workflows/{workflow}/close", new { reason = "enough" }, Ct);
        Assert.True(closed.IsSuccessStatusCode, await closed.Content.ReadAsStringAsync(Ct));

        var export = await JsonAsync(await person.GetAsync("/api/ledger/export", Ct));
        Assert.False(string.IsNullOrEmpty(export.GetProperty("instanceId").GetString()));
        Assert.Contains(export.GetProperty("workflows").EnumerateArray(), w => w.GetProperty("correlation").GetInt64() == workflow);
        Assert.Equal(team, (await factory.Services.GetRequiredService<IUsageLedger>().ReadWorkflowAsync(workflow, Ct))!.TeamId);

        // IN SQL SERVER, read with its own client: the account, the team, the item, the tell and the close.
        Assert.Equal(1, await CountAsync(database, "SELECT COUNT(*) FROM users WHERE email = @p", Email));
        Assert.Equal(1, await CountAsync(database, "SELECT COUNT(*) FROM teams WHERE id = @p", team));
        Assert.Equal(1, await CountAsync(database, "SELECT COUNT(*) FROM backlog_items WHERE title = @p", "Crawl the openings"));
        Assert.Equal(1, await CountAsync(database, "SELECT COUNT(*) FROM messages WHERE seq = @p", workflow));
        Assert.Equal(1, await CountAsync(database, "SELECT COUNT(*) FROM workflow_ledger WHERE correlation = @p", workflow));

        // AND NO SQLITE FILE: nothing on this start created, migrated or opened messages.db.
        Assert.Empty(Directory.GetFiles(root.Path, "messages.db*"));

        // THE BACKUP BEFORE A SCHEMA STEP IS THE SERVER'S, said once when the schema moved.
        var said = console.Out;
        Assert.Equal(1, Occurrences(said, "point-in-time restore is the backup on SQL Server"));
        Assert.DoesNotContain("Backup written to", said, StringComparison.Ordinal);
    }

    /// <summary>
    /// EVERY SERVICE A SQLITE HOST RESOLVES, A SQLSERVER HOST RESOLVES TOO, and none of them is a SQLite
    /// class. The store guard sees only the store interfaces; this sees the whole container, so a service
    /// wired on the SQLite path alone - a gate, a loaded setting, a writer built from a concrete store -
    /// fails here rather than at the first request that reaches it.
    /// </summary>
    [Fact]
    public async Task A_sqlserver_host_resolves_every_service_a_sqlite_host_resolves()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(Ct, applyModules: false);
        using var sqliteRoot = new TempRoot();
        using var sqlServerRoot = new TempRoot();

        IServiceCollection? sqliteServices = null;
        IServiceCollection? sqlServerServices = null;

        await using var sqlite = Host(sqliteRoot.Path).WithWebHostBuilder(host => host.ConfigureServices(s => sqliteServices = s));
        await using var sqlServer = Host(sqlServerRoot.Path,
                ("HARNESS_DATABASE", "sqlserver"), ("HARNESS_DATABASE_CONNECTION", database.ConnectionString))
            .WithWebHostBuilder(host => host.ConfigureServices(s => sqlServerServices = s));

        _ = sqlite.Services;
        _ = sqlServer.Services;

        var onSqlite = sqliteServices!.Select(d => d.ServiceType).ToHashSet();
        var onSqlServer = sqlServerServices!.Select(d => d.ServiceType).ToHashSet();

        Assert.Empty(onSqlite.Except(onSqlServer).Except(SqliteOnly.Keys).Select(Name).Order());

        // THE ALLOWLIST STAYS TRUE: each named type is still registered on SQLite and still absent on SQL Server.
        Assert.All(SqliteOnly.Keys, type => Assert.Contains(type, onSqlite));
        Assert.All(SqliteOnly.Keys, type => Assert.DoesNotContain(type, onSqlServer));

        // RESOLVED, not only registered: whatever of this solution's own services a SQLite Host builds, a
        // SQL Server Host builds too, and builds as something that is not a SQLite class.
        var ours = onSqlite.Where(type => !SqliteOnly.ContainsKey(type) && !type.ContainsGenericParameters
            && type.Assembly.GetName().Name!.StartsWith("Harness.", StringComparison.Ordinal));

        using var sqliteScope = sqlite.Services.CreateScope();
        using var sqlServerScope = sqlServer.Services.CreateScope();
        var unbuilt = new List<string>();
        var builtOnSqlite = new List<string>();

        foreach (var type in ours)
        {
            if (Resolve(sqliteScope.ServiceProvider, type) is null) continue;

            switch (Resolve(sqlServerScope.ServiceProvider, type))
            {
                case null:
                    unbuilt.Add(Name(type));
                    break;
                case var built when built.Any(o => o?.GetType().Name.StartsWith("Sqlite", StringComparison.Ordinal) == true):
                    builtOnSqlite.Add($"{Name(type)} as {string.Join(", ", built.Select(o => o?.GetType().Name))}");
                    break;
            }
        }

        Assert.Empty(unbuilt.Order());
        Assert.Empty(builtOnSqlite.Order());
        Assert.Empty(Directory.GetFiles(sqlServerRoot.Path, "messages.db*"));
    }

    /// <summary>
    /// The SQLite Host's concrete classes it registers beside their interface, for the Host's own SQLite
    /// tests that resolve them by type. No production code resolves them: every consumer takes the
    /// interface, which the SQL Server Host registers with its own class.
    /// </summary>
    private static readonly IReadOnlyDictionary<Type, string> SqliteOnly = new Dictionary<Type, string>
    {
        [typeof(ConnectionStore)] = "the SQLite IConnectionStore, registered as its class for the SQLite tests that resolve it",
        [typeof(WorkspaceFileChangeStore)] = "the SQLite IWorkspaceFileChangeStore, registered as its class for the SQLite tests that resolve it",
        [typeof(AgentStepStore)] = "the SQLite IAgentStepStore, registered as its class for the SQLite tests that resolve it",
    };

    /// <summary>
    /// ONE CONTROL PROCESS PER DATABASE: a second Host on its own data root but the first's database is
    /// refused by one sentence naming the database and nothing secret, and the first goes on serving.
    /// Two would each run the pump and the sweeps over the same rows. Once the first is gone, its
    /// session's lock goes with it and a Host starts there again.
    /// </summary>
    [Fact]
    public async Task A_second_host_on_the_same_database_is_refused_and_the_first_keeps_serving()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(Ct, applyModules: false);
        using var firstRoot = new TempRoot();

        var first = Host(firstRoot.Path, ("HARNESS_DATABASE", "sqlserver"), ("HARNESS_DATABASE_CONNECTION", database.ConnectionString));
        await using (first)
        {
            using var person = first.CreateClient();
            await JsonAsync(await person.GetAsync("/api/auth/state", Ct));

            var (said, exitCode, hadSqliteFile) = await RefusedStartAsync(database.ConnectionString);

            Assert.Equal(1, exitCode);
            Assert.Contains("Another control Host is already running", said, StringComparison.Ordinal);
            Assert.Contains(database.Name, said, StringComparison.Ordinal);
            if (new SqlConnectionStringBuilder(database.ConnectionString).Password is { Length: > 0 } password)
            {
                Assert.DoesNotContain(password, said, StringComparison.Ordinal);
            }
            AssertOneSentence(said);
            Assert.False(hadSqliteFile);

            // THE FIRST KEEPS SERVING, and holds the lock still.
            Assert.True((await JsonAsync(await person.GetAsync("/api/auth/state", Ct))).GetProperty("needsAdmin").GetBoolean());
        }

        using var againRoot = new TempRoot();
        await using var again = Host(againRoot.Path, ("HARNESS_DATABASE", "sqlserver"), ("HARNESS_DATABASE_CONNECTION", database.ConnectionString));
        using var client = again.CreateClient();
        await JsonAsync(await client.GetAsync("/api/auth/state", Ct));
    }

    [Fact]
    public async Task An_unreachable_server_is_one_sentence_and_a_non_zero_exit()
    {
        // A NAME NO RESOLVER ANSWERS (`.invalid` is reserved for exactly this) and a short wait.
        var (said, exitCode, hadSqliteFile) = await RefusedStartAsync(
            "Server=tcp:harness-no-such-server.invalid,1433;Database=harness;User ID=nobody;Password=unused;"
            + "Connect Timeout=3;ConnectRetryCount=0;Encrypt=False");

        Assert.Equal(1, exitCode);
        Assert.Contains("could not be reached", said, StringComparison.Ordinal);
        Assert.Contains("harness-no-such-server.invalid", said, StringComparison.Ordinal);
        Assert.DoesNotContain("unused", said, StringComparison.Ordinal);
        AssertOneSentence(said);
        Assert.False(hadSqliteFile);
    }

    [Fact]
    public async Task A_connection_string_sqlclient_cannot_read_is_one_sentence_and_a_non_zero_exit()
    {
        // A mis-quoted password holding `;x=`: SqlClient would name the fragment after the `;` as the keyword.
        var (said, exitCode, hadSqliteFile) = await RefusedStartAsync("Server=somewhere;Password=hunter2;leakfragment=rest");

        Assert.Equal(1, exitCode);
        Assert.Equal(Harness.Host.SqlServer.SqlServerStartRefusal.Unreadable, said.Trim());
        Assert.DoesNotContain("hunter2", said, StringComparison.Ordinal);
        Assert.DoesNotContain("leakfragment", said, StringComparison.OrdinalIgnoreCase);
        AssertOneSentence(said);
        Assert.False(hadSqliteFile);
    }

    [Fact]
    public async Task A_database_from_a_newer_build_is_one_sentence_and_a_non_zero_exit()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(Ct);
        await CountAsync(database, "INSERT INTO schema_steps (id, applied_at) VALUES (@p, '2099-01-01T00:00:00.0000000+00:00'); SELECT 0", "messages.from-a-newer-build");

        var (said, exitCode, hadSqliteFile) = await RefusedStartAsync(database.ConnectionString);

        Assert.Equal(1, exitCode);
        Assert.Contains("messages.from-a-newer-build", said, StringComparison.Ordinal);
        AssertOneSentence(said);
        Assert.False(hadSqliteFile);
    }

    [Fact]
    public async Task A_database_of_the_wrong_collation_is_one_sentence_and_a_non_zero_exit()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(Ct, collation: "SQL_Latin1_General_CP1_CI_AS", applyModules: false);

        var (said, exitCode, _) = await RefusedStartAsync(database.ConnectionString);

        Assert.Equal(1, exitCode);
        Assert.Contains(SqlServerMigrator.RequiredCollation, said, StringComparison.Ordinal);
        AssertOneSentence(said);
        Assert.Equal(0, await CountAsync(database, "SELECT COUNT(*) FROM sys.tables WHERE name = @p", "schema_steps"));
    }

    [Fact]
    public async Task A_database_without_row_versioning_is_one_sentence_and_a_non_zero_exit()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(Ct, applyModules: false, readCommittedSnapshot: false);

        var (said, exitCode, _) = await RefusedStartAsync(database.ConnectionString);

        Assert.Equal(1, exitCode);
        Assert.Contains("READ_COMMITTED_SNAPSHOT", said, StringComparison.Ordinal);
        AssertOneSentence(said);
        Assert.Equal(0, await CountAsync(database, "SELECT COUNT(*) FROM sys.tables WHERE name = @p", "schema_steps"));
    }

    // A START THE HOST REFUSES: what it printed to standard error, the exit code it set, and whether a
    // SQLite file appeared in its data root.
    private static async Task<(string Said, int ExitCode, bool HadSqliteFile)> RefusedStartAsync(string connection)
    {
        using var root = new TempRoot();
        using var console = new ConsoleCapture();
        int exitCode;

        try
        {
            await using var factory = Host(root.Path, ("HARNESS_DATABASE", "sqlserver"), ("HARNESS_DATABASE_CONNECTION", connection));

            // The entry point returns without building a host, which the factory reports as its own failure.
            Assert.ThrowsAny<Exception>(() => factory.Services);
        }
        finally
        {
            exitCode = Environment.ExitCode;
            Environment.ExitCode = 0;
        }

        return (console.Error, exitCode, root.HasSqliteFile);
    }

    // ONE LINE, ONE SENTENCE, NO TRACE: what an operator reads at the terminal.
    private static void AssertOneSentence(string said)
    {
        var lines = said.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.Single(lines);
        Assert.DoesNotContain("   at ", said, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", said, StringComparison.Ordinal);
    }

    private static WebApplicationFactory<Program> Host(string dataRoot, params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseSetting("DataRoot", dataRoot)
                .UseSetting("Logging:LogLevel:Default", "Warning")
                .UseSetting("ScheduleRunnerEnabled", "false");

            foreach (var (key, value) in settings) host.UseSetting(key, value);
        });

    private static IReadOnlyList<object?>? Resolve(IServiceProvider services, Type type)
    {
        try
        {
            return [.. services.GetServices(type)];
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Name(Type type) => type.FullName ?? type.Name;

    private static int Occurrences(string text, string of)
    {
        var count = 0;

        for (var at = text.IndexOf(of, StringComparison.Ordinal); at >= 0; at = text.IndexOf(of, at + of.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static async Task<int> CountAsync(SqlServerTestDatabase database, string sql, object value)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@p", value);

        return Convert.ToInt32(await command.ExecuteScalarAsync(Ct));
    }

    private sealed class TempRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"harness-sqlserver-host-{Guid.NewGuid():N}");

        public TempRoot() => Directory.CreateDirectory(Path);

        public bool HasSqliteFile => Directory.GetFiles(Path, "messages.db*").Length > 0;

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // THE PROCESS'S CONSOLE, taken for one start and given back. The class's collection runs alone.
    private sealed class ConsoleCapture : IDisposable
    {
        private readonly TextWriter _out = System.Console.Out;
        private readonly TextWriter _error = System.Console.Error;
        private readonly StringWriter _capturedOut = new();
        private readonly StringWriter _capturedError = new();

        public ConsoleCapture()
        {
            System.Console.SetOut(TextWriter.Synchronized(_capturedOut));
            System.Console.SetError(TextWriter.Synchronized(_capturedError));
        }

        public string Out => _capturedOut.ToString();

        public string Error => _capturedError.ToString();

        public void Dispose()
        {
            System.Console.SetOut(_out);
            System.Console.SetError(_error);
        }
    }
}
