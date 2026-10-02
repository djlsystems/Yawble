using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// What the rename, move and copy tests share: writing documents, a gone team and a retired folder,
/// the three requests, the rows a change wrote and the folder notices it gave.
/// </summary>
internal static class DocumentsChangeSupport
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static string Write(this TeamDocuments docs, string folder, string relative, string body = "x")
    {
        var file = Path.Combine(docs.RootFor(folder), relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, body);
        return file;
    }

    public static string Folder(this TeamDocuments docs, string folder, string relative)
    {
        var path = Path.Combine(docs.RootFor(folder), relative);
        Directory.CreateDirectory(path);
        return path;
    }

    public static string At(this TeamDocuments docs, string folder, string relative) =>
        Path.Combine(docs.RootFor(folder), relative);

    private static string HeadlessAgent(IServiceProvider services) =>
        services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;

    /// <summary>A team created, given documents, and deleted: its folder kept, its team gone.</summary>
    public static async Task<string> GoneTeamAsync(IServiceProvider services, string name, params string[] files)
    {
        var registry = services.GetRequiredService<TeamRegistry>();
        var docs = services.GetRequiredService<TeamDocuments>();
        var agent = HeadlessAgent(services);
        var team = (await registry.CreateAsync(name, agent, memberAgent: agent, ct: Ct)).Id;

        docs.EnsureFor(team);
        foreach (var file in files) docs.Write(team, file);

        Assert.NotNull(await services.GetRequiredService<TeamDeletion>().DeleteAsync(team, ct: Ct));
        Assert.Null(registry.ExistingName(team));
        return team;
    }

    /// <summary>A gone team whose id a later team took: the folder on disk of the earlier one.</summary>
    public static async Task<string> RetiredFolderAsync(IServiceProvider services, string name, params string[] files)
    {
        var team = await GoneTeamAsync(services, name, files);
        var agent = HeadlessAgent(services);
        await services.GetRequiredService<TeamRegistry>().CreateAsync(team, agent, memberAgent: agent, ct: Ct);

        return Assert.Single(services.GetRequiredService<TeamDocuments>().Folders(), f => f.Retired && f.Team == team).Folder;
    }

    public static Task<HttpResponseMessage> RenameAsync(this HttpClient client, string team, params (string Path, string Name)[] items) =>
        client.PostAsJsonAsync(
            $"/api/teams/{team}/documents/rename",
            new { items = items.Select(item => new { path = item.Path, name = item.Name }) },
            Ct);

    public static Task<HttpResponseMessage> MoveAsync(this HttpClient client, string team, string toFolder, string? toPath, params object[] items) =>
        client.TransferAsync("move", team, toFolder, toPath, items);

    public static Task<HttpResponseMessage> CopyAsync(this HttpClient client, string team, string toFolder, string? toPath, params object[] items) =>
        client.TransferAsync("copy", team, toFolder, toPath, items);

    public static Task<HttpResponseMessage> TransferAsync(
        this HttpClient client, string verb, string team, string toFolder, string? toPath, params object[] items) =>
        client.PostAsJsonAsync(
            $"/api/teams/{team}/documents/{verb}",
            new { to = new { folder = toFolder, path = toPath }, items = items.Select(item => item is string path ? new { path } : item) },
            Ct);

    public static object Item(string path, string onClash) => new { path, onClash };

    public static async Task<JsonElement> BodyAsync(this HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    public static async Task<string> ErrorAsync(this HttpResponseMessage response) =>
        (await response.BodyAsync()).GetProperty("error").GetString()!;

    /// <summary>Each item's (from, to, outcome, reason), in the order answered.</summary>
    public static List<(string From, string To, string Outcome, string? Reason)> Results(this JsonElement body) =>
        [.. body.GetProperty("results").EnumerateArray().Select(result => (
            result.GetProperty("from").GetString()!,
            result.GetProperty("to").GetString()!,
            result.GetProperty("outcome").GetString()!,
            result.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String ? reason.GetString() : null))];

    /// <summary>This folder's `documents.*` rows, oldest first.</summary>
    public static async Task<List<TenantEvent>> RowsAsync(IServiceProvider services, string folder) =>
        [.. (await services.GetRequiredService<ITenantLog>().ReadAsync(null, 500, Ct)).Events
            .Where(e => e.Action.StartsWith("documents.", StringComparison.Ordinal) && e.Subject == folder)
            .OrderBy(e => e.Seq)];

    public static async Task<long> NoticeMarkAsync(IServiceProvider services) =>
        (await services.GetRequiredService<IMessageLog>().ReadAfterAsync(0, [MessageTypes.FileChanged], int.MaxValue, Ct))
            .Select(m => m.Seq).DefaultIfEmpty(0).Max();

    /// <summary>Every folder notice since <paramref name="mark"/>: (team, folder) to the files it named, sorted.</summary>
    public static async Task<List<(string Team, string Folder, List<string> Changed)>> NoticesSinceAsync(IServiceProvider services, long mark) =>
        [.. (await services.GetRequiredService<IMessageLog>().ReadAfterAsync(mark, [MessageTypes.FileChanged], int.MaxValue, Ct))
            .Select(m => JsonDocument.Parse(m.Payload).RootElement)
            .Select(p => (
                p.GetProperty(PayloadFields.Team).GetString()!,
                p.GetProperty(PayloadFields.Path).GetString()!,
                p.GetProperty(PayloadFields.Changed).EnumerateArray().Select(c => c.GetString()!).Order(StringComparer.Ordinal).ToList()))];

    public static bool CanList(string folder)
    {
        try { _ = Directory.EnumerateFileSystemEntries(folder).Any(); return true; }
        catch (UnauthorizedAccessException) { return false; }
    }
}

/// <summary>
/// A Host of a test's own, for what a shared one cannot do: inject failures into how documents are
/// moved, copied and removed, or take the tenant log away. Teams Alpha and Beta, and a person
/// signed in.
/// </summary>
internal sealed class DocumentsChangeHost : IAsyncDisposable
{
    private readonly string _dataRoot = Directory.CreateTempSubdirectory("harness-docs-change-").FullName;
    private readonly List<string> _unlock = [];

    private WebApplicationFactory<Program> _factory = null!;

    /// <summary>Paths whose move, copy or removal the Host refuses, and with what.</summary>
    public Dictionary<string, Exception> Refused { get; } = new(StringComparer.Ordinal);

    /// <summary>What happens just before the Host moves, copies or removes a path, which then goes ahead.</summary>
    public Dictionary<string, Action> Before { get; } = new(StringComparer.Ordinal);

    public IServiceProvider Services => _factory.Services;

    public TeamDocuments Docs => Services.GetRequiredService<TeamDocuments>();

    public HttpClient Client { get; private set; } = null!;

    public string DataRoot => _dataRoot;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<DocumentsChangeHost> StartAsync(int copyLimit = TeamDocuments.MaximumCopyFiles)
    {
        var host = new DocumentsChangeHost();
        await host.InitializeAsync(copyLimit);
        return host;
    }

    private void Check(string path)
    {
        if (Before.Remove(path, out var before)) before();
        if (Refused.TryGetValue(path, out var refusal)) throw refusal;
    }

    private async Task InitializeAsync(int copyLimit)
    {
        var ops = new DocumentFileOps(
            (from, to) => { Check(from); DocumentFileOps.Real.MoveFile(from, to); },
            (from, to) => { Check(from); DocumentFileOps.Real.MoveFolder(from, to); },
            (from, to) => { Check(from); DocumentFileOps.Real.CopyFile(from, to); });

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentRunner>(new FakeAgent());
                services.AddSingleton(sp => new TeamDocuments(sp.GetRequiredService<TeamPaths>(), ops, copyLimit));
                services.AddSingleton(sp => new FolderRemoval(
                    sp.GetRequiredService<AgentLaunchUser>(),
                    sp.GetRequiredService<IUnfinishedRemovals>(),
                    new FolderRemoval.HostDeletes(
                        path => { Check(path); File.Delete(path); },
                        path => { Check(path); Directory.Delete(path, recursive: false); })));
            }));

        var registry = Services.GetRequiredService<TeamRegistry>();
        var agent = Services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
        await registry.CreateAsync("Alpha", agent, memberAgent: agent, ct: Ct);
        await registry.CreateAsync("Beta", agent, memberAgent: agent, ct: Ct);
        await Services.GetRequiredService<IUserStore>().CreateAsync("person@example.test", HostFixture.Password);
        Docs.EnsureFor("Alpha");
        Docs.EnsureFor("Beta");

        Client = _factory.CreateClient();
        (await Client.PostAsJsonAsync("/api/auth/login", new { email = "person@example.test", password = HostFixture.Password }, Ct))
            .EnsureSuccessStatusCode();
    }

    /// <summary>Takes the tenant log away, the only honest way to make a row unwritable.</summary>
    public async Task DropTenantLogAsync()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(_dataRoot, "messages.db")};Pooling=false");
        await connection.OpenAsync(Ct);
        await using var drop = connection.CreateCommand();
        drop.CommandText = "DROP TABLE tenant_events";
        await drop.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>Makes a folder unlistable (mode 000) and undoes it at the end.</summary>
    public void Lock(string folder)
    {
        File.SetUnixFileMode(folder, UnixFileMode.None);
        _unlock.Add(folder);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var folder in _unlock.Where(Directory.Exists))
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Client?.Dispose();
        await _factory.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
