using System.Net;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Extensions.DependencyInjection;
using static Harness.Tests.Host.DocumentsChangeSupport;

namespace Harness.Tests.Host;

/// <summary>
/// An upload that names `onClash`, the server side of files dropped from the computer onto the
/// Documents dialog: asked about, kept both, replaced (a file only) or skipped. Without it an upload
/// replaces a file of the same name, as it always has.
/// </summary>
public sealed class DocumentsUploadClashTests(HostFixture host) : IClassFixture<HostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private TeamDocuments Docs => host.Services.GetRequiredService<TeamDocuments>();

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string team, string path, string name, string body, string? onClash)
    {
        using var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(body)), "file", name },
            { new StringContent(path), "path" },
        };
        if (onClash is not null) form.Add(new StringContent(onClash), "onClash");

        return await client.PostAsync($"/api/teams/{team}/documents/upload", form, Ct);
    }

    [Fact]
    public async Task An_upload_without_onClash_replaces_a_file_of_the_same_name_as_before()
    {
        using var client = await host.PersonAsync();
        Docs.Write(host.Alpha, "up-plain/a.md", "old");

        var response = await UploadAsync(client, host.Alpha, "up-plain", "a.md", "new", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("new", File.ReadAllText(Docs.At(host.Alpha, "up-plain/a.md")));
    }

    [Fact]
    public async Task Ask_answers_409_naming_the_clash_and_writes_nothing()
    {
        using var client = await host.PersonAsync();
        Docs.Write(host.Alpha, "up-ask/a.md", "old");
        var rows = (await host.Services.GetRequiredService<ITenantLog>().ReadAsync(null, 1000, Ct)).Total;

        var response = await UploadAsync(client, host.Alpha, "up-ask", "a.md", "new", "ask");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.BodyAsync();
        Assert.Equal("a.md is already in up-ask. Choose Keep both, Replace or Skip.", body.GetProperty("error").GetString());
        var clash = Assert.Single(body.GetProperty("clashes").EnumerateArray());
        Assert.Equal("a.md", clash.GetProperty("from").GetString());
        Assert.Equal("up-ask/a.md", clash.GetProperty("to").GetString());
        Assert.False(clash.GetProperty("isFolder").GetBoolean());
        Assert.Equal("old", File.ReadAllText(Docs.At(host.Alpha, "up-ask/a.md")));
        Assert.Equal(rows, (await host.Services.GetRequiredService<ITenantLog>().ReadAsync(null, 1000, Ct)).Total);
    }

    [Fact]
    public async Task Ask_with_no_clash_saves_the_file()
    {
        using var client = await host.PersonAsync();
        Docs.Folder(host.Alpha, "up-free");

        var response = await UploadAsync(client, host.Alpha, "up-free", "a.md", "new", "ask");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("up-free/a.md", (await response.BodyAsync()).GetProperty("path").GetString());
        Assert.Equal("new", File.ReadAllText(Docs.At(host.Alpha, "up-free/a.md")));
    }

    [Fact]
    public async Task Keep_both_saves_under_the_first_free_copy_name()
    {
        using var client = await host.PersonAsync();
        Docs.Write(host.Alpha, "up-keep/a.md", "old");
        Docs.Write(host.Alpha, "up-keep/a (copy).md", "older");
        var mark = await NoticeMarkAsync(host.Services);

        var response = await UploadAsync(client, host.Alpha, "up-keep", "a.md", "new", "keep-both");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("up-keep/a (copy 2).md", (await response.BodyAsync()).GetProperty("path").GetString());
        Assert.Equal("old", File.ReadAllText(Docs.At(host.Alpha, "up-keep/a.md")));
        Assert.Equal("new", File.ReadAllText(Docs.At(host.Alpha, "up-keep/a (copy 2).md")));

        var row = await host.Services.GetRequiredService<ITenantLog>().FindLatestAsync(TenantActions.DocumentUploaded, host.Alpha, Ct);
        using var detail = JsonDocument.Parse(row!.Detail!);
        Assert.Equal("up-keep/a (copy 2).md", detail.RootElement.GetProperty("path").GetString());
        Assert.Equal("keep-both", detail.RootElement.GetProperty("onClash").GetString());
        Assert.Equal(["up-keep/a (copy 2).md"], Assert.Single(await NoticesSinceAsync(host.Services, mark)).Changed);
    }

    [Fact]
    public async Task Replace_replaces_a_file_and_refuses_a_folder_of_that_name()
    {
        using var client = await host.PersonAsync();
        Docs.Write(host.Alpha, "up-replace/a.md", "old");
        Docs.Write(host.Alpha, "up-replace/tree/inside.md", "kept");

        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(client, host.Alpha, "up-replace", "a.md", "new", "replace")).StatusCode);
        Assert.Equal("new", File.ReadAllText(Docs.At(host.Alpha, "up-replace/a.md")));

        var folder = await UploadAsync(client, host.Alpha, "up-replace", "tree", "file", "replace");
        Assert.Equal(HttpStatusCode.Conflict, folder.StatusCode);
        Assert.Equal("tree is a folder in up-replace; an upload replaces only a file.", await folder.ErrorAsync());
        Assert.Equal("kept", File.ReadAllText(Docs.At(host.Alpha, "up-replace/tree/inside.md")));
    }

    [Fact]
    public async Task Skip_writes_nothing_and_says_skipped()
    {
        using var client = await host.PersonAsync();
        Docs.Write(host.Alpha, "up-skip/a.md", "old");
        var rows = (await host.Services.GetRequiredService<ITenantLog>().ReadAsync(null, 1000, Ct)).Total;
        var mark = await NoticeMarkAsync(host.Services);

        var response = await UploadAsync(client, host.Alpha, "up-skip", "a.md", "new", "skip");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.BodyAsync();
        Assert.True(body.GetProperty("skipped").GetBoolean());
        Assert.Equal("up-skip/a.md", body.GetProperty("path").GetString());
        Assert.Equal("old", File.ReadAllText(Docs.At(host.Alpha, "up-skip/a.md")));
        Assert.Equal(rows, (await host.Services.GetRequiredService<ITenantLog>().ReadAsync(null, 1000, Ct)).Total);
        Assert.Empty(await NoticesSinceAsync(host.Services, mark));
    }

    [Fact]
    public async Task An_unknown_onClash_is_refused_with_a_sentence()
    {
        using var client = await host.PersonAsync();
        Docs.Folder(host.Alpha, "up-unknown");

        var response = await UploadAsync(client, host.Alpha, "up-unknown", "a.md", "new", "merge");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("onClash is ask, keep-both, replace or skip.", await response.ErrorAsync());
        Assert.False(File.Exists(Docs.At(host.Alpha, "up-unknown/a.md")));
    }

    [Fact]
    public async Task An_upload_into_a_gone_team_is_still_refused()
    {
        using var client = await host.PersonAsync();
        var gone = await GoneTeamAsync(host.Services, "UploadGone", "was.md");

        foreach (var onClash in new[] { "ask", "keep-both", "replace", "skip" })
        {
            var response = await UploadAsync(client, gone, "", "new.md", "x", onClash);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("No team 'UploadGone'.", await response.ErrorAsync());
        }

        Assert.False(File.Exists(Docs.At(gone, "new.md")));
    }
}
