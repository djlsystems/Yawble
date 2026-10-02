using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Identity;
using Harness.Messaging;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// A plugin READS the collections of its own team's sites that its manifest declares in
/// <c>reads</c>: delivered on stdin at run start as <c>sites</c>, newest first, within a per-run
/// budget whose cut is stated, never silent. Through the real member runtime, the real plugin runner
/// and the real site store: a /bin/sh fixture copies its stdin to a temp path outside the data root.
/// </summary>
public sealed class PluginSiteReadsTests : IAsyncLifetime
{
    private static readonly ContainerId Plug = new("alpha", "plug");

    private const string Secret = "s3cr3tvalue";
    private const string Person = "person@example.test";

    private static readonly DateTimeOffset Base = new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = Directory.CreateTempSubdirectory("harness-plugin-reads-").FullName;

    // OUTSIDE the data root, so the plugin's copy of its stdin is not among what the run wrote.
    private readonly string _outside = Directory.CreateTempSubdirectory("plugin-reads-stdin-").FullName;
    private readonly List<IDisposable> _registrations = [];
    private readonly List<ContainerTestBed> _beds = [];

    private string _database = "";
    private SqliteSiteStore _store = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string StdinCopy => Path.Combine(_outside, "request.json");

    public async ValueTask InitializeAsync()
    {
        _database = Path.Combine(_dataRoot, "harness.db");
        await new SchemaMigrator(_database).ApplyAsync(AuthSchema.Steps, ct: Ct);
        _store = new SqliteSiteStore(_database);

        // `board` on both teams; `ledger` on beta only.
        foreach (var (team, site) in new[] { ("alpha", "board"), ("beta", "board"), ("beta", "ledger") })
        {
            Assert.True(await _store.CreateAsync(
                new SiteRow(team, site, null, 1, DateTimeOffset.UtcNow, Person),
                new TriggerAudit(null, null, "site.created", $"{team}/{site}", site, null), Ct));
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var bed in _beds) await bed.DisposeAsync();
        foreach (var registration in _registrations) registration.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var directory in new[] { _dataRoot, _outside })
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }

    private sealed class Secrets(Dictionary<string, string> values) : ISecretStore
    {
        public string? TryGet(string logicalKey) => values.GetValueOrDefault(logicalKey);
    }

    private sealed class Settings(PluginMemberSettings settings) : IPluginMemberSettings
    {
        public Task<PluginMemberSettings> ForAsync(ContainerId member, CancellationToken ct = default) => Task.FromResult(settings);
    }

    private Task PutAsync(string team, string site, string collection, string id, string json, DateTimeOffset at) =>
        _store.PutDocumentAsync(team, site, collection, id, json, Person, at, Ct);

    /// <summary>The plugin: copies its stdin outside the data root, then does <paramref name="then"/>.</summary>
    private string CopyStdin(string then = """echo '{"t":"result","ok":true,"output":"done"}'""") =>
        $"cat > '{StdinCopy}'\n{then}";

    /// <summary>
    /// One run of the fixture plugin as alpha/plug, declaring <paramref name="reads"/> (JSON, or
    /// null for none), through the real member runtime, plugin runner, worker and site store.
    /// </summary>
    private async Task<ContainerTestBed> RunAsync(
        string? reads, string? script = null, long? budget = null, bool serveSites = true)
    {
        PluginInstall.Write(_dataRoot, "fixture", script: script ?? CopyStdin(), manifest: PluginInstall.Manifest("fixture", edit: m =>
        {
            m["secrets"] = JsonNode.Parse("""{"token":{"required":true}}""");
            if (reads is not null) m["reads"] = JsonNode.Parse(reads);
        }));

        var catalog = new PluginCatalog(PluginInstall.PluginsRoot(_dataRoot));
        catalog.Rescan();
        _registrations.Add(EventCatalog.Register(catalog));

        var bed = new ContainerTestBed();
        _beds.Add(bed);
        var heartbeat = new RunHeartbeat();
        var sites = new SiteService(
            _store,
            team => new[] { "alpha", "beta" }.FirstOrDefault(t => string.Equals(t, team, StringComparison.OrdinalIgnoreCase)),
            new TeamPaths(_dataRoot),
            bed.Store);

        var settings = new Settings(new PluginMemberSettings(
            new Dictionary<string, JsonElement>(), new Dictionary<string, string> { ["token"] = "FIXTURE_TOKEN" }));
        var secrets = new Secrets(new() { ["FIXTURE_TOKEN"] = Secret });
        var reports = new MemberReports(bed.Host, bed.Store, heartbeat);

        var plugins = budget is { } limit
            ? new PluginMemberRunner(catalog, reports, heartbeat, null, settings, secrets, sites: serveSites ? sites : null, siteReadBudget: limit)
            : new PluginMemberRunner(catalog, reports, heartbeat, null, settings, secrets, sites: serveSites ? sites : null);

        await bed.Host.AddAsync(
            ContainerTestBed.Definition(Plug) with { Agent = "plugin:fixture", WorkingDirectory = _dataRoot },
            new MemberRunnerRouter(bed.Runner, plugins), Ct);

        await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(Plug), JsonSerializer.Serialize(new { instruction = "sync" }), "alpha/manager"), Ct);

        Assert.True(await bed.PumpUntilAsync(async () =>
            (await bed.Store.ReadAfterAsync(0, [MessageTypes.Completed, MessageTypes.Failed], 10, Ct)).Count > 0, attempts: 400));

        return bed;
    }

    private JsonArray Sites() => JsonNode.Parse(File.ReadAllText(StdinCopy))!["sites"]!.AsArray();

    private static string[] Ids(JsonNode envelope) =>
        [.. envelope["documents"]!.AsArray().Select(d => (string)d!["id"]!)];

    private static string Status(Message row) =>
        JsonDocument.Parse(row.Payload).RootElement.GetProperty("status").GetString()!;

    private static async Task<string[]> RowsAsync(ContainerTestBed bed) =>
        [.. (await bed.OfTypeAsync(MessageTypes.Progress)).Select(Status)];

    private static string Marker() => "marker-" + Guid.NewGuid().ToString("N");

    // ---- the manifest ----------------------------------------------------------------------------

    private static string? Refusal(string reads) =>
        PluginManifest.Parse(PluginInstall.Manifest("fixture", edit: m => m["reads"] = JsonNode.Parse(reads)).ToJsonString()).Refusal;

    private static string ThirtyThree() =>
        "[" + string.Join(",", Enumerable.Range(0, 33).Select(i => $$"""{"site":"board","collection":"c{{i}}"}""")) + "]";

    [Theory]
    [InlineData("""{"site":"board","collection":"items"}""", "`reads` must be a list of { site, collection }.")]
    [InlineData("""["board/items"]""", "`reads[0]` must be an object with `site` and `collection`.")]
    [InlineData("""[{"site":"board"}]""", "`reads[0]` must be an object with `site` and `collection`.")]
    [InlineData("""[{"site":"Board","collection":"items"}]""", "`reads[0].site`: \"Board\" is not a valid site name. Use 1-63 lower-case letters, digits and hyphens, starting with a letter or digit.")]
    [InlineData("""[{"site":"board","collection":"items-"}]""", "`reads[0].collection`: \"items-\" is not a valid collection name. Use 1-63 lower-case letters, digits and hyphens, starting with a letter or digit.")]
    [InlineData("""[{"site":"board","collection":"items","team":"beta"}]""", "`reads[0]` names a team; a plugin reads only its own team's sites.")]
    [InlineData("""[{"site":"board","collection":"items"},{"site":"board","collection":"items"}]""", "`reads[1]` repeats board/items.")]
    public void A_reads_declaration_that_is_not_a_site_and_collection_list_is_refused_with_a_sentence(string reads, string expected)
    {
        Assert.Equal(expected, Refusal(reads));
    }

    [Fact]
    public void A_reads_declaration_of_more_than_32_collections_is_refused_with_a_sentence()
    {
        Assert.Equal("`reads` declares 33 collections; a plugin reads at most 32.", Refusal(ThirtyThree()));
        Assert.Null(Refusal("[" + string.Join(",", Enumerable.Range(0, 32).Select(i => $$"""{"site":"board","collection":"c{{i}}"}""")) + "]"));
    }

    [Fact]
    public void A_reads_declaration_with_two_faults_is_refused_for_the_first()
    {
        Assert.Equal(
            "`reads[0].site`: \"Bad\" is not a valid site name. Use 1-63 lower-case letters, digits and hyphens, starting with a letter or digit.",
            Refusal("""[{"site":"Bad","collection":"items"},{"site":"board","collection":"items","team":"beta"}]"""));
    }

    [Fact]
    public void A_manifest_without_reads_or_with_null_reads_reads_nothing()
    {
        var (absent, none) = PluginManifest.Parse(PluginInstall.Manifest("fixture").ToJsonString());
        Assert.Null(none);
        Assert.Empty(absent!.Reads);

        var (nulled, refusal) = PluginManifest.Parse(PluginInstall.Manifest("fixture", edit: m => m["reads"] = null).ToJsonString());
        Assert.Null(refusal);
        Assert.Empty(nulled!.Reads);
        Assert.DoesNotContain("reads", nulled.Ignored);
    }

    // ---- delivery ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_plugin_receives_its_declared_collections_with_each_documents_id_and_who_changed_it_last()
    {
        await PutAsync("alpha", "board", "items", "t1", """{"title":"First","status":"open","n":[1,2]}""", Base);
        await _store.PutDocumentAsync("alpha", "board", "items", "t2", """{"title":"Second"}""", "alpha/other", Base.AddMinutes(5), Ct);

        await using var bed = await RunAsync("""[{"site":"board","collection":"items"}]""");

        var envelope = Assert.Single(Sites())!;
        Assert.Equal(("board", "items", 2), ((string)envelope["site"]!, (string)envelope["collection"]!, (int)envelope["total"]!));
        Assert.Null(envelope["cut"]);
        Assert.Null(envelope["missing"]);

        var documents = envelope["documents"]!.AsArray();
        Assert.Equal(["t2", "t1"], Ids(envelope));

        Assert.Equal("2026-09-01T10:05:00Z", (string)documents[0]!["updatedAt"]!);
        Assert.Equal("alpha/other", (string)documents[0]!["updatedBy"]!);
        Assert.Equal("2026-09-01T10:00:00Z", (string)documents[1]!["updatedAt"]!);
        Assert.Equal(Person, (string)documents[1]!["updatedBy"]!);

        // `doc` is the stored JSON, parsed: an object, not a string.
        Assert.IsType<JsonObject>(documents[1]!["doc"]);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"title":"First","status":"open","n":[1,2]}"""), documents[1]!["doc"]));
        Assert.Empty(await RowsAsync(bed));
    }

    [Fact]
    public async Task An_undeclared_collection_of_the_same_site_is_not_delivered()
    {
        var marker = Marker();
        await PutAsync("alpha", "board", "items", "t1", """{"title":"Kept"}""", Base);
        await PutAsync("alpha", "board", "notes", "n1", $$"""{"title":"{{marker}}"}""", Base);

        await using var bed = await RunAsync("""[{"site":"board","collection":"items"}]""");

        Assert.Equal(["t1"], Ids(Assert.Single(Sites())!));
        Assert.DoesNotContain(marker, File.ReadAllText(StdinCopy), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plugin_without_reads_gets_an_empty_sites_list()
    {
        await PutAsync("alpha", "board", "items", "t1", """{"title":"Unread"}""", Base);

        await using var bed = await RunAsync(reads: null);

        Assert.Empty(Sites());
        Assert.Empty(await RowsAsync(bed));
    }

    [Fact]
    public async Task Documents_are_delivered_newest_first_with_id_breaking_a_tie()
    {
        // Written with times in the OPPOSITE order to their ids, and one pair at the same moment.
        await PutAsync("alpha", "board", "items", "a", """{"n":1}""", Base.AddMinutes(4));
        await PutAsync("alpha", "board", "items", "b", """{"n":2}""", Base.AddMinutes(3));
        await PutAsync("alpha", "board", "items", "c", """{"n":3}""", Base.AddMinutes(2));
        await PutAsync("alpha", "board", "items", "e", """{"n":5}""", Base.AddMinutes(1));
        await PutAsync("alpha", "board", "items", "d", """{"n":4}""", Base.AddMinutes(1));
        await PutAsync("alpha", "board", "items", "f", """{"n":6}""", Base.AddMinutes(9));

        await using var bed = await RunAsync("""[{"site":"board","collection":"items"}]""");

        Assert.Equal(["f", "a", "b", "c", "d", "e"], Ids(Assert.Single(Sites())!));
    }

    /// <summary>Three small newest documents, a large one, then two small older ones that would
    /// fit on their own: under a 2000-byte budget only the first three fit.</summary>
    private async Task CutFixtureAsync()
    {
        for (var i = 1; i <= 3; i++) await PutAsync("alpha", "board", "items", $"s{i}", $$"""{"title":"small {{i}}"}""", Base.AddMinutes(10 - i));
        await PutAsync("alpha", "board", "items", "large", $$"""{"title":"{{new string('x', 3000)}}"}""", Base.AddMinutes(5));
        for (var i = 5; i <= 6; i++) await PutAsync("alpha", "board", "items", $"s{i}", $$"""{"title":"small {{i}}"}""", Base.AddMinutes(10 - i));
    }

    private const long SmallBudget = 2000;

    private static string PlainCut(int delivered, int total, string read) =>
        $"Only {delivered} of {total} documents of {read} were delivered (newest first): this run's site data is limited to 2000 bytes. The rest were cut.";

    [Fact]
    public async Task Over_the_budget_delivery_stops_at_the_first_document_that_does_not_fit()
    {
        await CutFixtureAsync();

        await using var bed = await RunAsync("""[{"site":"board","collection":"items"}]""", budget: SmallBudget);

        var envelope = Assert.Single(Sites())!;
        Assert.Equal(["s1", "s2", "s3"], Ids(envelope));
        Assert.Equal(6, (int)envelope["total"]!);
        Assert.Equal(PlainCut(3, 6, "board/items"), (string)envelope["cut"]!);

        foreach (var document in envelope["documents"]!.AsArray())
        {
            var stored = await _store.GetDocumentAsync("alpha", "board", "items", (string)document!["id"]!, Ct);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(stored!.Json), document["doc"]));
        }

        Assert.Equal(["Site data for this run was cut: board/items 3 of 6. This run's site data is limited to 2000 bytes."], await RowsAsync(bed));
    }

    [Fact]
    public async Task Once_the_budget_is_spent_a_later_collection_gets_no_documents_and_its_sentence()
    {
        await CutFixtureAsync();
        await PutAsync("alpha", "board", "notes", "n1", """{"title":"tiny"}""", Base.AddHours(1));
        await PutAsync("alpha", "board", "notes", "n2", """{"title":"tiny"}""", Base.AddHours(2));

        await using var bed = await RunAsync("""[{"site":"board","collection":"items"},{"site":"board","collection":"notes"}]""", budget: SmallBudget);

        var notes = Sites()[1]!;
        Assert.Equal("notes", (string)notes["collection"]!);
        Assert.Empty(Ids(notes));
        Assert.Equal(2, (int)notes["total"]!);
        Assert.Equal(PlainCut(0, 2, "board/notes"), (string)notes["cut"]!);
    }

    [Fact]
    public async Task A_cut_and_a_missing_site_each_write_exactly_one_progress_row()
    {
        await CutFixtureAsync();
        var marker = Marker();
        await PutAsync("alpha", "board", "notes", "n1", $$"""{"title":"{{marker}}"}""", Base.AddHours(1));
        await PutAsync("alpha", "board", "notes", "n2", $$"""{"title":"{{marker}}"}""", Base.AddHours(2));

        await using var bed = await RunAsync(
            """[{"site":"board","collection":"items"},{"site":"board","collection":"notes"},{"site":"ledger","collection":"items"}]""",
            budget: SmallBudget);

        var sites = Sites();
        Assert.Equal(PlainCut(3, 6, "board/items"), (string)sites[0]!["cut"]!);
        Assert.Equal(PlainCut(0, 2, "board/notes"), (string)sites[1]!["cut"]!);
        Assert.Null(sites[2]!["cut"]);
        Assert.Equal(SiteService.NoSuchSite, (string)sites[2]!["missing"]!);

        var rows = await RowsAsync(bed);
        Assert.Equal(
            [
                "Site data for this run was cut: board/items 3 of 6, board/notes 0 of 2. This run's site data is limited to 2000 bytes.",
                "A declared read found no site: ledger/items (No such site.).",
            ],
            rows);
        Assert.All(rows, row => Assert.DoesNotContain(marker, row, StringComparison.Ordinal));
        Assert.All(rows, row => Assert.DoesNotContain("small", row, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Under_the_budget_nothing_is_cut_and_no_row_is_written()
    {
        await CutFixtureAsync();

        await using var bed = await RunAsync("""[{"site":"board","collection":"items"},{"site":"board","collection":"notes"}]""", budget: 1024 * 1024);

        Assert.All(Sites(), envelope => Assert.Null(envelope!["cut"]));
        Assert.Equal(["s1", "s2", "s3", "large", "s5", "s6"], Ids(Sites()[0]!));
        Assert.Empty(await RowsAsync(bed));
    }

    [Fact]
    public async Task On_a_host_that_serves_no_sites_each_read_is_missing_with_a_sentence()
    {
        await using var bed = await RunAsync("""[{"site":"board","collection":"items"},{"site":"board","collection":"notes"}]""", serveSites: false);

        Assert.All(Sites(), envelope =>
        {
            Assert.Equal("This Host serves no sites.", (string)envelope!["missing"]!);
            Assert.Empty(Ids(envelope));
            Assert.Equal(0, (int)envelope["total"]!);
        });
        Assert.Equal(["A declared read found no site: board/items (This Host serves no sites.), board/notes (This Host serves no sites.)."], await RowsAsync(bed));
    }

    // ---- own team only, and the tenant log ---------------------------------------------------------

    [Fact]
    public async Task Another_teams_site_reads_as_missing_and_none_of_its_documents_are_delivered()
    {
        var marker = Marker();
        await PutAsync("beta", "ledger", "items", "b1", $$"""{"title":"{{marker}}"}""", Base);

        await using var bed = await RunAsync("""[{"site":"ledger","collection":"items"}]""");

        var envelope = Assert.Single(Sites())!;
        Assert.Equal(SiteService.NoSuchSite, (string)envelope["missing"]!);
        Assert.Empty(Ids(envelope));
        Assert.Equal(0, (int)envelope["total"]!);
        Assert.DoesNotContain(marker, File.ReadAllText(StdinCopy), StringComparison.Ordinal);
    }

    [Fact]
    public async Task When_both_teams_have_the_site_only_the_members_own_documents_are_delivered()
    {
        var own = Marker();
        var theirs = Marker();
        await PutAsync("alpha", "board", "items", "a1", $$"""{"title":"{{own}}"}""", Base);
        await PutAsync("beta", "board", "items", "b1", $$"""{"title":"{{theirs}}"}""", Base.AddMinutes(1));

        await using var bed = await RunAsync("""[{"site":"board","collection":"items"}]""");

        var stdin = File.ReadAllText(StdinCopy);
        Assert.Contains(own, stdin, StringComparison.Ordinal);
        Assert.DoesNotContain(theirs, stdin, StringComparison.Ordinal);
        Assert.Equal(["a1"], Ids(Assert.Single(Sites())!));
    }

    [Fact]
    public async Task A_declared_read_of_a_site_the_team_does_not_have_reads_as_missing_with_a_sentence()
    {
        await using var bed = await RunAsync("""[{"site":"nowhere","collection":"items"},{"site":"board","collection":"items"}]""");

        var sites = Sites();
        Assert.Equal(SiteService.NoSuchSite, (string)sites[0]!["missing"]!);
        Assert.Null(sites[1]!["missing"]);
        Assert.Equal(["A declared read found no site: nowhere/items (No such site.)."], await RowsAsync(bed));
    }

    private async Task<long> TenantRowsAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={_database}");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM tenant_events";
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static string Cell(SqliteDataReader reader, int i) =>
        reader.IsDBNull(i) ? "null"
        : reader.GetValue(i) is byte[] bytes ? Encoding.UTF8.GetString(bytes)
        : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "";

    private async Task<string[]> SiteRowsAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={_database}");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM site_documents";
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<string>();
        while (await reader.ReadAsync(Ct)) rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => Cell(reader, i))));
        return [.. rows.Order(StringComparer.Ordinal)];
    }

    private static async Task<string[]> TypesAsync(ContainerTestBed bed) =>
        [.. (await bed.Store.ReadRangeAsync(0, int.MaxValue, Ct)).Select(m => m.Type).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task Reading_site_data_appends_nothing_to_the_tenant_log_and_touches_no_document()
    {
        await PutAsync("alpha", "board", "items", "t1", """{"title":"One"}""", Base);
        await PutAsync("alpha", "board", "notes", "n1", """{"title":"Two"}""", Base.AddMinutes(1));
        var tenantBefore = await TenantRowsAsync();
        var documentsBefore = await SiteRowsAsync();

        await using var reading = await RunAsync("""[{"site":"board","collection":"items"},{"site":"board","collection":"notes"}]""");
        Assert.Equal(2, Sites().Sum(s => Ids(s!).Length));

        Assert.Equal(tenantBefore, await TenantRowsAsync());
        Assert.Equal(documentsBefore, await SiteRowsAsync());
        Assert.Empty(await reading.OfTypeAsync(MessageTypes.Progress));

        // THE SAME RUN WITHOUT `reads` writes exactly the same kinds of row, as many of each.
        await using var twin = await RunAsync(reads: null);
        var types = await TypesAsync(reading);
        Assert.Contains(MessageTypes.Completed, types);
        Assert.Equal(await TypesAsync(twin), types);
        Assert.Equal(tenantBefore, await TenantRowsAsync());
    }

    // ---- redaction ---------------------------------------------------------------------------------

    /// <summary>Every place a run could have left <paramref name="marker"/>: every row of every table
    /// of both databases but <c>site_documents</c>, and every file under the data root and the bed's
    /// folder but the databases themselves.</summary>
    private async Task AssertNowhereAsync(ContainerTestBed bed, string marker)
    {
        foreach (var database in new[] { _database, bed.DatabasePath })
        {
            await using var connection = new SqliteConnection($"Data Source={database}");
            await connection.OpenAsync(Ct);

            var tables = new List<string>();
            await using (var list = connection.CreateCommand())
            {
                list.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
                await using var names = await list.ExecuteReaderAsync(Ct);
                while (await names.ReadAsync(Ct)) tables.Add(names.GetString(0));
            }

            Assert.NotEmpty(tables);

            foreach (var table in tables.Where(t => t != "site_documents"))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"SELECT * FROM \"{table}\"";
                await using var reader = await command.ExecuteReaderAsync(Ct);
                while (await reader.ReadAsync(Ct))
                {
                    for (var i = 0; i < reader.FieldCount; i++) Assert.DoesNotContain(marker, Cell(reader, i), StringComparison.Ordinal);
                }
            }
        }

        foreach (var root in new[] { _dataRoot, Path.GetDirectoryName(bed.DatabasePath)! })
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith("harness.db", StringComparison.Ordinal) || name.StartsWith("messages.db", StringComparison.Ordinal)) continue;
                Assert.DoesNotContain(marker, await File.ReadAllTextAsync(file, Ct), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task Data_read_in_appears_nowhere_the_run_writes()
    {
        var marker = Marker();
        await PutAsync("alpha", "board", "items", "t1", $$"""{"title":"{{marker}}"}""", Base);

        await using var bed = await RunAsync("""[{"site":"board","collection":"items"}]""",
            script: CopyStdin("""echo '{"t":"progress","status":"working"}'; echo '{"t":"result","ok":true,"output":"synced"}'"""));

        // Positive control: the plugin did receive it.
        Assert.Contains(marker, File.ReadAllText(StdinCopy), StringComparison.Ordinal);

        var completed = Assert.Single(await bed.OfTypeAsync(MessageTypes.Completed));
        Assert.Contains("synced", completed.Payload, StringComparison.Ordinal);
        await AssertNowhereAsync(bed, marker);
    }

    [Fact]
    public async Task Data_read_in_appears_nowhere_a_failing_run_writes()
    {
        var marker = Marker();
        await PutAsync("alpha", "board", "items", "t1", $$"""{"title":"{{marker}}"}""", Base);

        await using var bed = await RunAsync("""[{"site":"board","collection":"items"}]""",
            script: CopyStdin("""echo '{"t":"result","ok":false}'; echo 'it went wrong' >&2; exit 3"""));

        Assert.Contains(marker, File.ReadAllText(StdinCopy), StringComparison.Ordinal);

        var failed = Assert.Single(await bed.OfTypeAsync(MessageTypes.Failed));
        // The failure reason is the card's: the member's snapshot.
        Assert.Equal("The plugin reported that it failed.", bed.Host.Find(Plug)!.Snapshot().Failed);
        Assert.Contains("it went wrong", failed.Payload, StringComparison.Ordinal);
        await AssertNowhereAsync(bed, marker);
    }

    [Fact]
    public async Task A_document_holding_a_secret_value_printed_by_the_plugin_is_redacted_in_its_output()
    {
        await PutAsync("alpha", "board", "items", "t1", $$"""{"note":"pasted {{Secret}} here"}""", Base);

        // The plugin prints its whole request: not a record, so it is kept as output text.
        await using var bed = await RunAsync("""[{"site":"board","collection":"items"}]""",
            script: CopyStdin($$"""cat '{{StdinCopy}}'; echo '{"t":"result","ok":true,"output":"printed"}'"""));

        Assert.Contains(Secret, File.ReadAllText(StdinCopy), StringComparison.Ordinal);
        var completed = Assert.Single(await bed.OfTypeAsync(MessageTypes.Completed));
        Assert.Contains("pasted [redacted] here", completed.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, completed.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_read_document_written_back_with_a_secret_is_stored_redacted()
    {
        await PutAsync("alpha", "board", "items", "t1", """{"title":"One","status":"open"}""", Base);

        await using var bed = await RunAsync("""[{"site":"board","collection":"items"}]""",
            script: CopyStdin($$$"""echo '{"t":"site.put","site":"board","collection":"items","id":"t1","doc":{"title":"One","status":"archived","note":"token {{{Secret}}}"}}'; echo '{"t":"result","ok":true}'"""));

        Assert.Equal(["t1"], Ids(Assert.Single(Sites())!));
        var stored = (await _store.GetDocumentAsync("alpha", "board", "items", "t1", Ct))!;
        Assert.Equal("archived", (string?)JsonNode.Parse(stored.Json)!["status"]);
        Assert.Equal("token [redacted]", (string?)JsonNode.Parse(stored.Json)!["note"]);
        Assert.Equal("alpha/plug", stored.UpdatedBy);
    }
}
