using System.Text.Json;
using Harness.Host;
using Harness.Identity;
using Harness.Messaging;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harness.Tests;

/// <summary>
/// THE OPERATOR CLI'S REQUEST FILE. A request is answered with the route's status and body and
/// deleted before it is acted on; one a stopped Host left is deleted unanswered at start; and in a
/// folder anybody but the Host could read or write it is deleted and acted on in no way.
/// </summary>
public sealed class AgentCredentialRequestsTests : IDisposable
{
    private const string Value = "fake-request-file-value-8Kd2Lq";

    private readonly string _root = Directory.CreateTempSubdirectory("harness-credential-requests-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private async Task<AgentCredentialRequests> RequestsAsync()
    {
        var database = Path.Combine(_root, "messages.db");
        await new SchemaMigrator(database).ApplyAsync(AuthSchema.Steps, ct: Ct);

        var settings = new TenantSettings(new SqliteTenantSettingsStore(database), new ConfigurationBuilder().Build());
        await settings.LoadAsync();

        var credentials = new AgentCredentials(
            new AgentCatalog(AgentCatalogFile.BuiltIns()),
            new AgentCredentialStore(database, new EphemeralDataProtectionProvider()),
            settings);

        return new AgentCredentialRequests(credentials, _root, NullLogger<AgentCredentialRequests>.Instance);
    }

    private static async Task WriteAsync(AgentCredentialRequests requests, object request)
    {
        await File.WriteAllTextAsync(
            Path.Combine(requests.Root, AgentCredentialRequests.RequestFile),
            JsonSerializer.Serialize(request, JsonSerializerOptions.Web), Ct);
    }

    [Fact]
    public async Task The_operator_clis_request_is_answered_and_no_request_file_remains()
    {
        var requests = await RequestsAsync();
        requests.Prepare();

        await WriteAsync(requests, new { request = "n1", action = "set", agent = "grok-headless", value = Value });
        Assert.True(await requests.AnswerAsync(Ct));

        Assert.False(File.Exists(Path.Combine(requests.Root, AgentCredentialRequests.RequestFile)));

        var reportPath = Path.Combine(requests.Root, AgentCredentialRequests.ReportFile);
        var text = await File.ReadAllTextAsync(reportPath, Ct);
        Assert.DoesNotContain(Value, text);

        var report = JsonDocument.Parse(text).RootElement;
        Assert.Equal("n1", report.GetProperty("request").GetString());
        Assert.Equal(200, report.GetProperty("status").GetInt32());
        Assert.Equal(
            """{"command":"grok","set":true,"setBy":"operator","setAt":"X"}""",
            System.Text.RegularExpressions.Regex.Replace(report.GetProperty("body").GetRawText(), "\"setAt\":\"[^\"]+\"", "\"setAt\":\"X\""));

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(reportPath));
        }

        // An action it does not know is answered too, and removed.
        await WriteAsync(requests, new { request = "n2", action = "reveal", agent = "grok" });
        Assert.True(await requests.AnswerAsync(Ct));
        Assert.Equal(400, JsonDocument.Parse(await File.ReadAllTextAsync(reportPath, Ct)).RootElement.GetProperty("status").GetInt32());
        Assert.False(File.Exists(Path.Combine(requests.Root, AgentCredentialRequests.RequestFile)));
    }

    [Fact]
    public async Task A_request_left_from_before_a_restart_is_deleted_unanswered()
    {
        var requests = await RequestsAsync();
        requests.Prepare();

        await WriteAsync(requests, new { request = "stale", action = "set", agent = "grok", value = Value });
        await File.WriteAllTextAsync(Path.Combine(requests.Root, AgentCredentialRequests.RequestFile + ".tmp"), Value, Ct);

        // A restart.
        requests.Prepare();

        Assert.Empty(Directory.EnumerateFiles(requests.Root));
        Assert.False(await requests.AnswerAsync(Ct));
    }

    [Fact]
    public async Task The_request_folder_is_closed_to_the_agent_user()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix modes.");

        var requests = await RequestsAsync();
        requests.Prepare();

        const UnixFileMode Permissions = (UnixFileMode)0x1FF;
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(requests.Root) & Permissions);
        Assert.Null(requests.FolderRefusal());

        // A folder the agent's group can read would show it a value for half a second: nothing in it
        // is acted on, the request is DELETED unanswered, and - the folder still the Host's own and
        // closed to writes - the CLI is told why in a report holding nothing the request held.
        File.SetUnixFileMode(requests.Root, File.GetUnixFileMode(requests.Root) | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        await WriteAsync(requests, new { request = "n1", action = "set", agent = "grok", value = Value });

        Assert.Contains("must be 0700", requests.FolderRefusal());
        Assert.True(await requests.AnswerAsync(Ct));
        Assert.False(File.Exists(Path.Combine(requests.Root, AgentCredentialRequests.RequestFile)));

        var text = await File.ReadAllTextAsync(Path.Combine(requests.Root, AgentCredentialRequests.ReportFile), Ct);
        Assert.DoesNotContain(Value, text);
        var report = JsonDocument.Parse(text).RootElement;
        Assert.Equal("n1", report.GetProperty("request").GetString());
        Assert.Equal(503, report.GetProperty("status").GetInt32());
        Assert.Equal(AgentCredentialRequests.FolderRefused, report.GetProperty("body").GetProperty("error").GetString());

        // Nothing was set.
        await File.WriteAllTextAsync(Path.Combine(requests.Root, AgentCredentialRequests.ReportFile), "", Ct);
        File.SetUnixFileMode(requests.Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await WriteAsync(requests, new { request = "n2", action = "status", agent = "grok" });
        Assert.True(await requests.AnswerAsync(Ct));
        Assert.False(JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(requests.Root, AgentCredentialRequests.ReportFile), Ct))
            .RootElement.GetProperty("body").GetProperty("set").GetBoolean());

        // A folder others can WRITE could have a link planted where the report goes: the request is
        // deleted unanswered and nothing is written there. The CLI sees its request gone, no report.
        File.Delete(Path.Combine(requests.Root, AgentCredentialRequests.ReportFile));
        File.SetUnixFileMode(requests.Root, File.GetUnixFileMode(requests.Root) | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute);
        await WriteAsync(requests, new { request = "n3", action = "set", agent = "grok", value = Value });

        Assert.Contains("writable by others", requests.FolderRefusal());
        Assert.False(await requests.AnswerAsync(Ct));
        Assert.Empty(Directory.EnumerateFiles(requests.Root));
    }
}
