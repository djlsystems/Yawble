using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Pty;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// AN IMAGE A PERSON GIVES THEIR CONCIERGE, through the real Host: saved under the person's own
/// Concierge folder with a generated name the agent can read, only when its content is PNG, JPEG,
/// GIF or WebP and under the cap, only for a person, and only together with its
/// <c>concierge.attachment-added</c> row. Removed when the session ends, and by age at start. A
/// Host of its own per test, because the row's failure is made by taking its table away.
/// </summary>
public sealed class ConciergeAttachmentTests : IAsyncLifetime
{
    private const string Email = "person@example.test";

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 1, 2, 3];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dataRoot =
        Path.Combine(Path.GetTempPath(), $"harness-concierge-attachments-{Guid.NewGuid():N}");

    private WebApplicationFactory<Program> _factory = null!;

    private string _user = "";

    private string Database => Path.Combine(_dataRoot, "messages.db");

    private TeamPaths Paths => _factory.Services.GetRequiredService<TeamPaths>();

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        SqliteConnection.ClearAllPools();

        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Starts this test's Host, with a person and (unless told not to) their Concierge folder.</summary>
    private async Task StartAsync(Action<IWebHostBuilder>? configure = null, bool workspace = true)
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseSetting("DataRoot", _dataRoot).UseSetting("Logging:LogLevel:Default", "Warning");
            configure?.Invoke(host);
        });

        _user = (await _factory.Services.GetRequiredService<IUserStore>().CreateAsync(Email, HostFixture.Password, Ct)).Id;

        if (workspace) ConciergeWorkspaces.Resolve(Paths, _user, Email);
    }

    private async Task<HttpClient> PersonAsync()
    {
        var client = _factory.CreateClient();
        (await client.PostAsJsonAsync("/api/auth/login", new { email = Email, password = HostFixture.Password }, Ct))
            .EnsureSuccessStatusCode();
        return client;
    }

    private static MultipartFormDataContent Form(byte[] bytes, string name = "../../screenshot.txt", string type = "text/plain")
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(type);
        return new MultipartFormDataContent { { file, "file", name } };
    }

    private string Attachments => Path.Combine(ConciergeWorkspaces.TryExisting(Paths, _user)!, ConciergeAttachments.FolderName);

    private string[] Stored() => Directory.Exists(Attachments) ? Directory.GetFiles(Attachments) : [];

    private async Task<List<string?>> RowsAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={Database};Pooling=false");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT detail FROM tenant_events WHERE action = $action ORDER BY seq";
        command.Parameters.AddWithValue("$action", TenantActions.ConciergeAttachmentAdded);

        List<string?> rows = [];
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct)) rows.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        return rows;
    }

    [Fact]
    public async Task An_image_is_stored_in_the_concierge_folder_under_a_generated_name_the_agent_can_read()
    {
        await StartAsync();
        using var client = await PersonAsync();

        using var answer = await client.PostAsync("/api/concierge/attachments", Form(Png), Ct);

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        var body = await answer.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var path = body.GetProperty("path").GetString()!;

        // Under the person's own folder, named by the Host: the uploaded name and its extension
        // are nowhere, and the extension is the content's.
        Assert.Equal(Attachments, Path.GetDirectoryName(path));
        Assert.True(Path.IsPathRooted(path));
        Assert.Matches(@"^\d{8}T\d{9}Z-1\.png$", Path.GetFileName(path));
        Assert.Equal(Png, await File.ReadAllBytesAsync(path, Ct));
        Assert.Equal(Png.Length, body.GetProperty("size").GetInt64());
        Assert.Equal("image/png", body.GetProperty("type").GetString());

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(ConciergeAttachments.FileMode, File.GetUnixFileMode(path));
        }

        // Its row: size and type, never the content or the uploaded name.
        var detail = Assert.Single(await RowsAsync());
        using var json = JsonDocument.Parse(detail!);
        Assert.Equal(Png.Length, json.RootElement.GetProperty("size").GetInt64());
        Assert.Equal("image/png", json.RootElement.GetProperty("type").GetString());
        Assert.DoesNotContain("screenshot", detail);

        // A second image in the same folder gets its own name.
        using var again = await client.PostAsync("/api/concierge/attachments", Form(Png), Ct);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(2, Stored().Length);
    }

    [Fact]
    public async Task A_file_that_is_not_an_image_by_content_is_refused_whatever_its_name_and_type_say()
    {
        await StartAsync();
        using var client = await PersonAsync();

        using var answer = await client.PostAsync(
            "/api/concierge/attachments", Form("<svg onload=alert(1)>"u8.ToArray(), "picture.png", "image/png"), Ct);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, answer.StatusCode);
        Assert.Contains("not a PNG, JPEG, GIF or WebP", (await answer.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
        Assert.Empty(Stored());
        Assert.Empty(await RowsAsync());
    }

    [Fact]
    public async Task An_image_over_the_cap_is_refused_with_a_sentence_and_nothing_is_kept()
    {
        await StartAsync(host => host.UseSetting("ConciergeAttachmentMaxBytes", "1024"));
        using var client = await PersonAsync();

        using var answer = await client.PostAsync("/api/concierge/attachments", Form([.. Png, .. new byte[2048]]), Ct);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, answer.StatusCode);
        Assert.Contains("larger than", (await answer.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
        Assert.Empty(Stored());
        Assert.Empty(await RowsAsync());
    }

    [Fact]
    public async Task A_machine_principal_is_refused()
    {
        await StartAsync();
        var key = await _factory.Services.GetRequiredService<IPrincipalStore>().MintAsync(
            "alpha/Worker", PrincipalKind.Container, "alpha", Permits.All, ct: Ct);
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(Harness.Host.Auth.ApiKeyAuthenticationHandler.Header, key);

        using var answer = await client.PostAsync("/api/concierge/attachments", Form(Png), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace((await answer.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString()));
        Assert.Empty(Stored());
        Assert.Empty(await RowsAsync());
    }

    [Fact]
    public async Task Without_its_row_the_file_is_not_kept()
    {
        await StartAsync();
        using var client = await PersonAsync();

        await using (var connection = new SqliteConnection($"Data Source={Database};Pooling=false"))
        {
            await connection.OpenAsync(Ct);
            await using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TABLE tenant_events";
            await drop.ExecuteNonQueryAsync(Ct);
        }

        using var answer = await client.PostAsync("/api/concierge/attachments", Form(Png), Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, answer.StatusCode);
        Assert.Contains("nothing was kept", (await answer.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
        Assert.Empty(Stored());
    }

    [Fact]
    public async Task A_person_with_no_concierge_folder_is_told_to_open_one_and_none_is_made()
    {
        await StartAsync(workspace: false);
        using var client = await PersonAsync();

        using var answer = await client.PostAsync("/api/concierge/attachments", Form(Png), Ct);

        Assert.Equal(HttpStatusCode.Conflict, answer.StatusCode);
        Assert.Null(ConciergeWorkspaces.TryExisting(Paths, _user));
    }

    [Fact]
    public async Task An_attachments_folder_that_is_a_link_is_not_written_through()
    {
        if (OperatingSystem.IsWindows()) return;

        await StartAsync();
        using var client = await PersonAsync();
        var elsewhere = Directory.CreateDirectory(Path.Combine(_dataRoot, "elsewhere")).FullName;
        Directory.CreateSymbolicLink(Attachments, elsewhere);

        using var answer = await client.PostAsync("/api/concierge/attachments", Form(Png), Ct);

        Assert.Equal(HttpStatusCode.Conflict, answer.StatusCode);
        Assert.Empty(Directory.GetFileSystemEntries(elsewhere));
    }

    [Fact]
    public async Task Ending_the_concierge_session_removes_its_attachments()
    {
        await StartAsync(
            host => host.ConfigureTestServices(services => services.AddSingleton<IPtyEngine>(new SilentEngine())),
            workspace: false);
        var consoles = _factory.Services.GetRequiredService<ConciergeSessionStore>();
        var key = new ConciergeSessionKey(_user);

        // The real launch, as the socket route makes it: it makes the person's folder.
        await consoles.AttachAsync(key, "", 80, 24, Ct);
        using var client = await PersonAsync();
        using var answer = await client.PostAsync("/api/concierge/attachments", Form(Png), Ct);
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Single(Stored());

        await consoles.EndAsync(key);

        Assert.False(Directory.Exists(Attachments));
        Assert.True(File.Exists(TeamPaths.ConciergeMarkerIn(ConciergeWorkspaces.TryExisting(Paths, _user)!)));
    }

    [Fact]
    public async Task Attachments_older_than_the_retention_are_removed_at_start_and_newer_ones_kept()
    {
        // Laid down before the Host starts, as a Host stopped mid-session leaves them.
        var paths = new TeamPaths(_dataRoot);
        var workspace = paths.ConciergeWorkspaceFor("someone");
        TeamPaths.EnsureConciergeWorkspace(workspace, "user-someone");
        var folder = Directory.CreateDirectory(Path.Combine(workspace, ConciergeAttachments.FolderName)).FullName;
        var old = Path.Combine(folder, "20260101T000000000Z-1.png");
        var recent = Path.Combine(folder, "20260101T000000000Z-2.png");
        await File.WriteAllBytesAsync(old, Png, Ct);
        await File.WriteAllBytesAsync(recent, Png, Ct);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow - TimeSpan.FromDays(8));
        File.SetLastWriteTimeUtc(recent, DateTime.UtcNow - TimeSpan.FromDays(6));

        // An unmarked folder is not a Concierge workspace, and nothing in it is touched.
        var stray = Directory.CreateDirectory(Path.Combine(paths.ConciergeWorkspacesRoot, "stray", ConciergeAttachments.FolderName)).FullName;
        var strayFile = Path.Combine(stray, "keep.png");
        await File.WriteAllBytesAsync(strayFile, Png, Ct);
        File.SetLastWriteTimeUtc(strayFile, DateTime.UtcNow - TimeSpan.FromDays(30));

        await StartAsync(workspace: false);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(strayFile));
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 }, "image/png", "png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 1 }, "image/jpeg", "jpg")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 1, 0, 1, 0, 0, 0 }, "image/gif", "gif")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x37, 0x61, 1, 0, 1, 0, 0, 0 }, "image/gif", "gif")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0x24, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 }, "image/webp", "webp")]
    public void Each_accepted_type_is_told_by_its_signature(byte[] head, string type, string extension) =>
        Assert.Equal((type, extension), ConciergeAttachments.Detect(head));

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47 })]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0x24, 0, 0, 0, 0x57, 0x41, 0x56, 0x45 })]
    [InlineData(new byte[] { 0x3C, 0x73, 0x76, 0x67, 0x3E, 0, 0, 0, 0, 0, 0, 0 })]
    [InlineData(new byte[] { 0x42, 0x4D, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
    public void Anything_else_is_not_an_image(byte[] head) =>
        Assert.Null(ConciergeAttachments.Detect(head));

    /// <summary>A terminal that prints nothing and never exits.</summary>
    private sealed class SilentEngine : IPtyEngine
    {
        public Task<IPtySession> SpawnAsync(PtySpec spec, CancellationToken ct) =>
            Task.FromResult<IPtySession>(new SilentSession());
    }

    private sealed class SilentSession : IPtySession
    {
#pragma warning disable CS0067 // never raised: this terminal is silent
        public event Action<byte[]>? Output;
        public event Action<int>? Exited;
#pragma warning restore CS0067

        public void Write(ReadOnlySpan<byte> bytes) { }

        public void Resize(int cols, int rows) { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
