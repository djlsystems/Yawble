using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// The folder-change trigger through the real Host: the Documents dialog announcing its own writes, the trigger routes
/// refusing a folder a watch may not have, "Test this folder", and the runner polling a new row.
///
/// No trigger here covers a folder anything is written into, so nothing wakes a member - the Host's
/// agents are real processes.
/// </summary>
public sealed class FolderChangeRouteTests(HostFixture host) : IClassFixture<HostFixture>
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
    public async Task An_upload_through_the_documents_dialog_publishes_file_changed_at_once()
    {
        using var client = await host.PersonAsync();

        using var form = new MultipartFormDataContent
        {
            { new ByteArrayContent("hello"u8.ToArray()), "file", "note.txt" },
            { new StringContent("uploads"), "path" },
        };

        var response = await client.PostAsync($"/api/teams/{host.Alpha}/documents/upload", form, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var log = host.Services.GetRequiredService<IMessageLog>();
        var published = (await log.ReadAfterAsync(0, [MessageTypes.FileChanged], int.MaxValue, Ct))
            .Where(m => m.Payload.Contains("uploads/note.txt", StringComparison.Ordinal))
            .ToList();

        var row = Assert.Single(published);
        using var payload = JsonDocument.Parse(row.Payload);
        Assert.Equal(host.Alpha, payload.RootElement.GetProperty(PayloadFields.Team).GetString());
        Assert.Equal("documents", payload.RootElement.GetProperty(PayloadFields.Root).GetString());
        Assert.Equal("uploads", payload.RootElement.GetProperty(PayloadFields.Path).GetString());
        Assert.Equal(1, payload.RootElement.GetProperty(PayloadFields.Count).GetInt32());
        Assert.Equal(host.Alpha, MessageTeam.Of(row));

        // A delete through the same dialog is announced the same way.
        var deleted = await client.DeleteAsync(
            $"/api/teams/{host.Alpha}/documents?path=uploads/note.txt", Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        Assert.Equal(2, (await log.ReadAfterAsync(0, [MessageTypes.FileChanged], int.MaxValue, Ct))
            .Count(m => m.Payload.Contains("uploads/note.txt", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_path_outside_the_allowed_roots_is_refused_with_a_sentence()
    {
        using var client = await host.PersonAsync();

        Assert.Equal(
            "That path leaves the team's documents folder.",
            await RefusalAsync(client, new { watchRoot = "documents", watchPath = $"../{host.Beta}" }));

        var dataRoot = host.Services.GetRequiredService<FileBrowserPolicy>().Roots[0].Name;
        Assert.Equal(
            "The platform's own data folder cannot be watched.",
            await RefusalAsync(client, new { watchRoot = $"root:{dataRoot}", watchPath = "" }));

        Assert.Equal(
            "No file-browser root is named \"Elsewhere\".",
            await RefusalAsync(client, new { watchRoot = "root:Elsewhere", watchPath = "" }));

        DocumentsFolder("refused");
        Assert.Equal(
            "pollSeconds must be at least 15.",
            await RefusalAsync(client, new { watchRoot = "documents", watchPath = "refused", pollSeconds = 5 }));

        var triggers = await client.GetFromJsonAsync<JsonElement[]>($"/api/teams/{host.Alpha}/triggers", Ct);
        Assert.DoesNotContain(triggers!, t => t.GetProperty("name").GetString() == "refused");
    }

    [Fact]
    public async Task A_created_folder_trigger_carries_its_watch_fields_and_the_runner_takes_a_baseline()
    {
        DocumentsFolder("watched");
        File.WriteAllText(Path.Combine(DocumentsFolder("watched"), "already.txt"), "there before");
        using var client = await host.PersonAsync();

        var response = await client.PostAsJsonAsync(
            $"/api/teams/{host.Alpha}/triggers",
            new
            {
                name = "watched",
                instruction = "Look at {event.changed}",
                kind = "folderChange",
                watchRoot = "documents",
                watchPath = "./watched/",
                eventType = "agentContainer.completed",
            },
            Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var row = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var id = row.GetProperty("id").GetString();

        Assert.Equal(MessageTypes.FileChanged, row.GetProperty("eventType").GetString());
        Assert.Equal("documents", row.GetProperty("watchRoot").GetString());
        Assert.Equal("watched", row.GetProperty("watchPath").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("watchGlob").ValueKind);
        Assert.Equal(60, row.GetProperty("pollSeconds").GetInt32());
        Assert.Equal(30, row.GetProperty("quietSeconds").GetInt32());
        Assert.Equal(60, row.GetProperty("minIntervalSeconds").GetInt32());
        Assert.True(row.GetProperty("idleOnly").GetBoolean());

        try
        {
            // The runner polls a new row at once: the first look is the baseline and fires nothing.
            JsonElement polled = default;

            for (var i = 0; i < 100; i++)
            {
                var rows = await client.GetFromJsonAsync<JsonElement[]>($"/api/teams/{host.Alpha}/triggers", Ct);
                polled = rows!.Single(t => t.GetProperty("id").GetString() == id);
                if (polled.GetProperty("lastPollAt").ValueKind != JsonValueKind.Null) break;
                await Task.Delay(100, Ct);
            }

            Assert.NotEqual(JsonValueKind.Null, polled.GetProperty("lastPollAt").ValueKind);
            Assert.Equal(1, polled.GetProperty("lastPollEntries").GetInt32());
            Assert.True(polled.GetProperty("lastPollMs").GetInt32() >= 0);
            Assert.Equal(JsonValueKind.Null, polled.GetProperty("lastPollError").ValueKind);
            Assert.Equal(JsonValueKind.Null, polled.GetProperty("lastChangeAt").ValueKind);
            Assert.NotEqual(JsonValueKind.Null, polled.GetProperty("lastFingerprint").ValueKind);

            var patched = await client.PatchAsJsonAsync(
                $"/api/teams/{host.Alpha}/triggers/{id}", new { watchPath = "../../elsewhere" }, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, patched.StatusCode);
            Assert.Equal(
                "That path leaves the team's documents folder.",
                (await patched.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());

            var glob = await client.PatchAsJsonAsync(
                $"/api/teams/{host.Alpha}/triggers/{id}", new { watchGlob = "*.pdf", quietSeconds = 5 }, Ct);
            Assert.Equal(HttpStatusCode.OK, glob.StatusCode);
            var moved = await glob.Content.ReadFromJsonAsync<JsonElement>(Ct);
            Assert.Equal("*.pdf", moved.GetProperty("watchGlob").GetString());
            Assert.Equal(5, moved.GetProperty("quietSeconds").GetInt32());
        }
        finally
        {
            await client.DeleteAsync($"/api/teams/{host.Alpha}/triggers/{id}", Ct);
        }
    }

    [Fact]
    public async Task Test_this_folder_lists_what_it_sees_and_how_long_the_listing_took()
    {
        var folder = DocumentsFolder("tested");
        File.WriteAllText(Path.Combine(folder, "one.csv"), "1");
        File.WriteAllText(Path.Combine(folder, "two.csv"), "22");
        File.WriteAllText(Path.Combine(folder, "skip.tmp"), "t");
        using var client = await host.PersonAsync();

        var response = await client.PostAsJsonAsync(
            $"/api/teams/{host.Alpha}/triggers/test-folder",
            new { watchRoot = "documents", watchPath = "tested" },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("refusal").ValueKind);
        Assert.Equal("documents/tested", body.GetProperty("folder").GetString());
        Assert.Equal(2, body.GetProperty("count").GetInt32());
        Assert.False(body.GetProperty("truncated").GetBoolean());
        Assert.True(body.GetProperty("elapsedMs").GetInt32() >= 0);
        Assert.Equal(
            ["tested/one.csv", "tested/two.csv"],
            body.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("path").GetString()!).ToArray());
        Assert.Equal(2, body.GetProperty("entries")[1].GetProperty("size").GetInt64());

        var refused = await client.PostAsJsonAsync(
            $"/api/teams/{host.Alpha}/triggers/test-folder",
            new { watchRoot = "documents", watchPath = "not-there" },
            Ct);
        var refusal = await refused.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        Assert.False(refusal.GetProperty("ok").GetBoolean());
        Assert.Equal(
            "There is no folder at \"not-there\" in the team's documents.",
            refusal.GetProperty("refusal").GetString());

        var roots = await client.GetFromJsonAsync<JsonElement[]>($"/api/teams/{host.Alpha}/triggers/watch-roots", Ct);
        Assert.Equal("documents", roots![0].GetProperty("value").GetString());
        Assert.DoesNotContain(roots, r => r.GetProperty("value").GetString()!.StartsWith("root:Harness", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_folder_trigger_routes_are_for_people_only()
    {
        using var container = host.Container(host.AlphaContainerKey);

        var test = await container.PostAsJsonAsync(
            $"/api/teams/{host.Alpha}/triggers/test-folder", new { watchRoot = "documents", watchPath = "" }, Ct);
        var create = await container.PostAsJsonAsync(
            $"/api/teams/{host.Alpha}/triggers",
            new { name = "x", instruction = "x", kind = "folderChange", watchRoot = "documents", watchPath = "" },
            Ct);

        Assert.Equal(HttpStatusCode.Forbidden, test.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
    }

    private async Task<string?> RefusalAsync(HttpClient client, object watch)
    {
        var body = JsonSerializer.SerializeToElement(watch);
        var request = new Dictionary<string, object?>
        {
            ["name"] = "refused",
            ["instruction"] = "never stored",
            ["kind"] = "folderChange",
        };

        foreach (var property in body.EnumerateObject()) request[property.Name] = property.Value;

        var response = await client.PostAsJsonAsync($"/api/teams/{host.Alpha}/triggers", request, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString();
    }
}
