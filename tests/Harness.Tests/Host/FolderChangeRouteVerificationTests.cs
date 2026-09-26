using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// Folder-change triggers through the real Host: every folder-trigger
/// route an agent could reach is refused to it, the upload payload never carries contents, and a
/// symbolic link is refused over HTTP.
/// </summary>
public sealed class FolderChangeRouteVerificationTests(HostFixture host) : IClassFixture<HostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string DocumentsFolder(string relative)
    {
        var root = host.Services.GetRequiredService<TeamDocuments>().EnsureFor(host.Alpha);
        var folder = Path.Combine(root, relative);
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    public async Task Create_edit_delete_test_and_watch_roots_are_refused_to_an_agent()
    {
        DocumentsFolder("agent-proof");
        using var person = await host.PersonAsync();

        var created = await person.PostAsJsonAsync(
            $"/api/teams/{host.Alpha}/triggers",
            new { name = "agent-proof", instruction = "x", kind = "folderChange", watchRoot = "documents", watchPath = "agent-proof" },
            Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString();

        try
        {
            using var agent = host.Container(host.AlphaContainerKey);

            var patch = await agent.PatchAsJsonAsync(
                $"/api/teams/{host.Alpha}/triggers/{id}", new { watchPath = "" }, Ct);
            var roots = await agent.GetAsync($"/api/teams/{host.Alpha}/triggers/watch-roots", Ct);
            var delete = await agent.DeleteAsync($"/api/teams/{host.Alpha}/triggers/{id}", Ct);
            var convert = await agent.PostAsJsonAsync(
                $"/api/teams/{host.Alpha}/triggers",
                new { name = "y", instruction = "y", kind = "every", intervalSeconds = 60 },
                Ct);

            Assert.Equal(HttpStatusCode.Forbidden, patch.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, roots.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, convert.StatusCode);

            var row = (await person.GetFromJsonAsync<JsonElement[]>($"/api/teams/{host.Alpha}/triggers", Ct))!
                .Single(t => t.GetProperty("id").GetString() == id);
            Assert.Equal("agent-proof", row.GetProperty("watchPath").GetString());
        }
        finally
        {
            await person.DeleteAsync($"/api/teams/{host.Alpha}/triggers/{id}", Ct);
        }
    }

    [Fact]
    public async Task An_upload_announces_the_path_and_count_never_the_contents()
    {
        const string secret = "UPLOAD-CONTENTS-MUST-NOT-LEAK-4c1d";
        using var client = await host.PersonAsync();

        using var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(secret)), "file", "leak-check.txt" },
            { new StringContent("verify-upload"), "path" },
        };

        var response = await client.PostAsync($"/api/teams/{host.Alpha}/documents/upload", form, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var log = host.Services.GetRequiredService<IMessageLog>();
        var rows = await log.ReadAfterAsync(0, [MessageTypes.FileChanged], int.MaxValue, Ct);
        var row = Assert.Single(rows, m => m.Payload.Contains("verify-upload/leak-check.txt", StringComparison.Ordinal));

        Assert.DoesNotContain(secret, row.Payload);
        Assert.DoesNotContain(rows, m => m.Payload.Contains(secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_symbolic_link_in_the_documents_is_refused_on_create_and_on_test()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"harness-verify-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        var link = Path.Combine(DocumentsFolder("links"), "out");
        Directory.CreateSymbolicLink(link, outside);

        try
        {
            using var client = await host.PersonAsync();
            const string sentence = "\"links/out\" is a symbolic link, and a watched folder may not be one.";

            var create = await client.PostAsJsonAsync(
                $"/api/teams/{host.Alpha}/triggers",
                new { name = "link", instruction = "x", kind = "folderChange", watchRoot = "documents", watchPath = "links/out" },
                Ct);
            Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
            Assert.Equal(sentence, (await create.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());

            var test = await client.PostAsJsonAsync(
                $"/api/teams/{host.Alpha}/triggers/test-folder",
                new { watchRoot = "documents", watchPath = "links/out" },
                Ct);
            var body = await test.Content.ReadFromJsonAsync<JsonElement>(Ct);
            Assert.Equal(HttpStatusCode.OK, test.StatusCode);
            Assert.False(body.GetProperty("ok").GetBoolean());
            Assert.Equal(sentence, body.GetProperty("refusal").GetString());
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(outside, recursive: true);
        }
    }
}
