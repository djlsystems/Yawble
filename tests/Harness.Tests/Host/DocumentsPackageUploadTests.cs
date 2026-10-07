using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Extensions.DependencyInjection;
using static Harness.Tests.Host.DocumentsChangeSupport;

namespace Harness.Tests.Host;

/// <summary>
/// A whole folder, or a .zip, uploaded into a team's documents: subfolders kept, a zip unpacked into
/// a folder of its name and refused - with nothing written - when an entry is a link, absolute, or
/// leaves that folder; the folder made answers to the same `onClash` choices a file upload does.
/// </summary>
public sealed class DocumentsPackageUploadTests(HostFixture host) : IClassFixture<HostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private TeamDocuments Docs => host.Services.GetRequiredService<TeamDocuments>();

    private static async Task<HttpResponseMessage> UploadFolderAsync(
        HttpClient client, string team, string path, string? onClash, params (string RelativePath, string Body)[] files)
    {
        using var form = new MultipartFormDataContent();

        foreach (var (relativePath, body) in files)
        {
            form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(body)), "file", Path.GetFileName(relativePath));
            form.Add(new StringContent(relativePath), "relativePath");
        }

        form.Add(new StringContent(path), "path");
        if (onClash is not null) form.Add(new StringContent(onClash), "onClash");

        return await client.PostAsync($"/api/teams/{team}/documents/upload-folder", form, Ct);
    }

    private static async Task<HttpResponseMessage> UploadZipAsync(
        HttpClient client, string team, string path, string name, byte[] zip, string? onClash)
    {
        using var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(zip), "file", name },
            { new StringContent(path), "path" },
        };
        if (onClash is not null) form.Add(new StringContent(onClash), "onClash");

        return await client.PostAsync($"/api/teams/{team}/documents/upload-zip", form, Ct);
    }

    /// <summary>A zip of these entries. A body of null makes a directory entry; a link is a Unix
    /// symlink entry (mode 0120777) whose content is its target, the way `zip --symlinks` stores one.</summary>
    private static byte[] Zip(params (string Name, string? Body, bool Link)[] entries)
    {
        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, body, link) in entries)
            {
                var entry = archive.CreateEntry(name);
                if (link) entry.ExternalAttributes = unchecked((int)(0xA1FFu << 16));
                if (body is null) continue;

                using var stream = entry.Open();
                stream.Write(Encoding.UTF8.GetBytes(body));
            }
        }

        return buffer.ToArray();
    }

    private static (string, string?, bool) F(string name, string body) => (name, body, false);

    private async Task<long> RowCountAsync() =>
        (await host.Services.GetRequiredService<ITenantLog>().ReadAsync(null, 1000, Ct)).Total;

    [Fact]
    public async Task A_folder_upload_keeps_its_subfolders()
    {
        using var client = await host.PersonAsync();
        Docs.Folder(host.Alpha, "pk-folder");
        var mark = await NoticeMarkAsync(host.Services);

        var response = await UploadFolderAsync(client, host.Alpha, "pk-folder", null,
            ("job-tracker/solution.json", "{}"),
            ("job-tracker/members/scheduler.md", "scheduler"),
            ("job-tracker/sites/board/files/index.html", "<p>board</p>"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.BodyAsync();
        Assert.Equal("pk-folder/job-tracker", body.GetProperty("path").GetString());
        Assert.Equal(
            ["pk-folder/job-tracker/members/scheduler.md", "pk-folder/job-tracker/sites/board/files/index.html", "pk-folder/job-tracker/solution.json"],
            body.GetProperty("files").EnumerateArray().Select(f => f.GetString()!).Order(StringComparer.Ordinal));
        Assert.Equal("{}", File.ReadAllText(Docs.At(host.Alpha, "pk-folder/job-tracker/solution.json")));
        Assert.Equal("scheduler", File.ReadAllText(Docs.At(host.Alpha, "pk-folder/job-tracker/members/scheduler.md")));
        Assert.Equal("<p>board</p>", File.ReadAllText(Docs.At(host.Alpha, "pk-folder/job-tracker/sites/board/files/index.html")));

        var row = await host.Services.GetRequiredService<ITenantLog>().FindLatestAsync(TenantActions.DocumentUploaded, host.Alpha, Ct);
        using var detail = JsonDocument.Parse(row!.Detail!);
        Assert.Equal("pk-folder/job-tracker", detail.RootElement.GetProperty("path").GetString());
        Assert.Equal("folder", detail.RootElement.GetProperty("kind").GetString());
        Assert.Equal(3, detail.RootElement.GetProperty("files").GetInt32());
        Assert.Equal(3, (await NoticesSinceAsync(host.Services, mark)).Count);
    }

    [Fact]
    public async Task A_folder_upload_with_a_path_that_leaves_the_folder_writes_nothing()
    {
        using var client = await host.PersonAsync();
        Docs.Folder(host.Alpha, "pk-folder-escape");
        var rows = await RowCountAsync();

        var response = await UploadFolderAsync(client, host.Alpha, "pk-folder-escape", null,
            ("pkg/a.md", "a"),
            ("pkg/../../outside.md", "out"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("The folder was not uploaded: pkg/../../outside.md leaves the folder.", await response.ErrorAsync());
        Assert.False(Directory.Exists(Docs.At(host.Alpha, "pk-folder-escape/pkg")));
        Assert.False(File.Exists(Docs.At(host.Alpha, "outside.md")));
        Assert.Equal(rows, await RowCountAsync());
    }

    [Fact]
    public async Task A_zip_unpacks_into_a_folder_of_its_name()
    {
        using var client = await host.PersonAsync();
        Docs.Folder(host.Alpha, "pk-zip");

        var response = await UploadZipAsync(client, host.Alpha, "pk-zip", "notes.zip",
            Zip(F("solution.json", "{}"), F("members/a.md", "a"), ("empty/", null, false)), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("pk-zip/notes", (await response.BodyAsync()).GetProperty("path").GetString());
        Assert.Equal("{}", File.ReadAllText(Docs.At(host.Alpha, "pk-zip/notes/solution.json")));
        Assert.Equal("a", File.ReadAllText(Docs.At(host.Alpha, "pk-zip/notes/members/a.md")));
        Assert.True(Directory.Exists(Docs.At(host.Alpha, "pk-zip/notes/empty")));

        var row = await host.Services.GetRequiredService<ITenantLog>().FindLatestAsync(TenantActions.DocumentUploaded, host.Alpha, Ct);
        using var detail = JsonDocument.Parse(row!.Detail!);
        Assert.Equal("zip", detail.RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task A_zip_whose_entries_sit_in_a_folder_of_its_name_is_not_doubled_and_leaves_out_macos_litter()
    {
        using var client = await host.PersonAsync();
        Docs.Folder(host.Alpha, "pk-zip-top");

        var response = await UploadZipAsync(client, host.Alpha, "pk-zip-top", "job-tracker.zip",
            Zip(("job-tracker/", null, false), F("job-tracker/solution.json", "{}"), F("__MACOSX/job-tracker/._solution.json", "fork")), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{}", File.ReadAllText(Docs.At(host.Alpha, "pk-zip-top/job-tracker/solution.json")));
        Assert.False(Directory.Exists(Docs.At(host.Alpha, "pk-zip-top/job-tracker/job-tracker")));
        Assert.False(Directory.Exists(Docs.At(host.Alpha, "pk-zip-top/job-tracker/__MACOSX")));
    }

    [Fact]
    public async Task A_zip_holding_a_link_is_refused_and_writes_nothing()
    {
        using var client = await host.PersonAsync();
        Docs.Folder(host.Alpha, "pk-zip-link");
        var rows = await RowCountAsync();

        var response = await UploadZipAsync(client, host.Alpha, "pk-zip-link", "evil.zip",
            Zip(F("a.md", "a"), ("passwd", "/etc/passwd", true)), null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("The zip was not unpacked: passwd is a link.", await response.ErrorAsync());
        Assert.False(Directory.Exists(Docs.At(host.Alpha, "pk-zip-link/evil")));
        Assert.Equal(rows, await RowCountAsync());
    }

    [Fact]
    public async Task A_zip_with_a_path_that_leaves_its_folder_is_refused_and_writes_nothing()
    {
        using var client = await host.PersonAsync();
        Docs.Folder(host.Alpha, "pk-zip-dotdot");
        var rows = await RowCountAsync();

        var response = await UploadZipAsync(client, host.Alpha, "pk-zip-dotdot", "evil.zip",
            Zip(F("a.md", "a"), F("../../outside.md", "out")), null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("The zip was not unpacked: ../../outside.md leaves the folder.", await response.ErrorAsync());
        Assert.False(Directory.Exists(Docs.At(host.Alpha, "pk-zip-dotdot/evil")));
        Assert.False(File.Exists(Docs.At(host.Alpha, "outside.md")));
        Assert.Equal(rows, await RowCountAsync());
    }

    [Fact]
    public async Task A_zip_with_an_absolute_path_is_refused_and_writes_nothing()
    {
        using var client = await host.PersonAsync();
        Docs.Folder(host.Alpha, "pk-zip-abs");

        var response = await UploadZipAsync(client, host.Alpha, "pk-zip-abs", "evil.zip",
            Zip(F("a.md", "a"), F("/tmp/outside.md", "out")), null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("The zip was not unpacked: /tmp/outside.md is an absolute path.", await response.ErrorAsync());
        Assert.False(Directory.Exists(Docs.At(host.Alpha, "pk-zip-abs/evil")));
    }

    [Fact]
    public async Task A_file_not_named_zip_is_refused()
    {
        using var client = await host.PersonAsync();

        var response = await UploadZipAsync(client, host.Alpha, "", "notes.tar", Zip(F("a.md", "a")), null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Send a .zip file.", await response.ErrorAsync());
    }

    [Fact]
    public async Task Ask_answers_409_naming_the_folder_and_writes_nothing_for_a_folder_and_a_zip()
    {
        using var client = await host.PersonAsync();
        Docs.Write(host.Alpha, "pk-ask/pkg/a.md", "old");
        var rows = await RowCountAsync();

        var folder = await UploadFolderAsync(client, host.Alpha, "pk-ask", "ask", ("pkg/a.md", "new"));
        var zip = await UploadZipAsync(client, host.Alpha, "pk-ask", "pkg.zip", Zip(F("a.md", "new")), "ask");

        foreach (var response in new[] { folder, zip })
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var body = await response.BodyAsync();
            Assert.Equal("pkg is already in pk-ask. Choose Keep both, Replace or Skip.", body.GetProperty("error").GetString());
            var clash = Assert.Single(body.GetProperty("clashes").EnumerateArray());
            Assert.Equal("pkg", clash.GetProperty("from").GetString());
            Assert.Equal("pk-ask/pkg", clash.GetProperty("to").GetString());
            Assert.True(clash.GetProperty("isFolder").GetBoolean());
        }

        Assert.Equal("old", File.ReadAllText(Docs.At(host.Alpha, "pk-ask/pkg/a.md")));
        Assert.Equal(rows, await RowCountAsync());
    }

    [Fact]
    public async Task Keep_both_makes_the_first_free_copy_folder_for_a_folder_and_a_zip()
    {
        using var client = await host.PersonAsync();
        Docs.Write(host.Alpha, "pk-keep/pkg/a.md", "old");

        var folder = await UploadFolderAsync(client, host.Alpha, "pk-keep", "keep-both", ("pkg/a.md", "folder"));
        var zip = await UploadZipAsync(client, host.Alpha, "pk-keep", "pkg.zip", Zip(F("a.md", "zip")), "keep-both");

        Assert.Equal(HttpStatusCode.OK, folder.StatusCode);
        Assert.Equal("pk-keep/pkg (copy)", (await folder.BodyAsync()).GetProperty("path").GetString());
        Assert.Equal(HttpStatusCode.OK, zip.StatusCode);
        Assert.Equal("pk-keep/pkg (copy 2)", (await zip.BodyAsync()).GetProperty("path").GetString());
        Assert.Equal("old", File.ReadAllText(Docs.At(host.Alpha, "pk-keep/pkg/a.md")));
        Assert.Equal("folder", File.ReadAllText(Docs.At(host.Alpha, "pk-keep/pkg (copy)/a.md")));
        Assert.Equal("zip", File.ReadAllText(Docs.At(host.Alpha, "pk-keep/pkg (copy 2)/a.md")));
    }

    [Fact]
    public async Task Replace_refuses_a_folder_of_that_name_for_a_folder_and_a_zip()
    {
        using var client = await host.PersonAsync();
        Docs.Write(host.Alpha, "pk-replace/pkg/a.md", "old");

        var folder = await UploadFolderAsync(client, host.Alpha, "pk-replace", "replace", ("pkg/a.md", "new"));
        var zip = await UploadZipAsync(client, host.Alpha, "pk-replace", "pkg.zip", Zip(F("a.md", "new")), "replace");

        foreach (var response in new[] { folder, zip })
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("pkg is a folder in pk-replace; an upload replaces only a file.", await response.ErrorAsync());
        }

        Assert.Equal("old", File.ReadAllText(Docs.At(host.Alpha, "pk-replace/pkg/a.md")));
    }

    [Fact]
    public async Task Replace_replaces_a_file_of_that_name_with_the_folder()
    {
        using var client = await host.PersonAsync();
        Docs.Write(host.Alpha, "pk-replace-file/pkg", "a file");

        var response = await UploadZipAsync(client, host.Alpha, "pk-replace-file", "pkg.zip", Zip(F("a.md", "new")), "replace");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("new", File.ReadAllText(Docs.At(host.Alpha, "pk-replace-file/pkg/a.md")));
    }

    [Fact]
    public async Task Skip_writes_nothing_and_says_skipped_for_a_folder_and_a_zip()
    {
        using var client = await host.PersonAsync();
        Docs.Write(host.Alpha, "pk-skip/pkg/a.md", "old");
        var rows = await RowCountAsync();
        var mark = await NoticeMarkAsync(host.Services);

        var folder = await UploadFolderAsync(client, host.Alpha, "pk-skip", "skip", ("pkg/a.md", "new"), ("pkg/b.md", "new"));
        var zip = await UploadZipAsync(client, host.Alpha, "pk-skip", "pkg.zip", Zip(F("a.md", "new"), F("b.md", "new")), "skip");

        foreach (var response in new[] { folder, zip })
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.BodyAsync();
            Assert.True(body.GetProperty("skipped").GetBoolean());
            Assert.Equal("pk-skip/pkg", body.GetProperty("path").GetString());
        }

        Assert.Equal("old", File.ReadAllText(Docs.At(host.Alpha, "pk-skip/pkg/a.md")));
        Assert.False(File.Exists(Docs.At(host.Alpha, "pk-skip/pkg/b.md")));
        Assert.Equal(rows, await RowCountAsync());
        Assert.Empty(await NoticesSinceAsync(host.Services, mark));
    }

    [Fact]
    public async Task Without_onClash_a_folder_of_that_name_takes_the_files_and_a_file_of_the_same_path_is_replaced()
    {
        using var client = await host.PersonAsync();
        Docs.Write(host.Alpha, "pk-plain/pkg/a.md", "old");
        Docs.Write(host.Alpha, "pk-plain/pkg/kept.md", "kept");

        var response = await UploadFolderAsync(client, host.Alpha, "pk-plain", null, ("pkg/a.md", "new"), ("pkg/sub/b.md", "b"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("new", File.ReadAllText(Docs.At(host.Alpha, "pk-plain/pkg/a.md")));
        Assert.Equal("b", File.ReadAllText(Docs.At(host.Alpha, "pk-plain/pkg/sub/b.md")));
        Assert.Equal("kept", File.ReadAllText(Docs.At(host.Alpha, "pk-plain/pkg/kept.md")));
    }

    [Fact]
    public async Task Without_onClash_a_link_in_the_folder_it_writes_into_is_refused_and_nothing_written()
    {
        using var client = await host.PersonAsync();
        var outside = Directory.CreateTempSubdirectory("harness-pk-outside-").FullName;
        Docs.Folder(host.Alpha, "pk-plain-link/pkg");
        Directory.CreateSymbolicLink(Docs.At(host.Alpha, "pk-plain-link/pkg/sub"), outside);

        var response = await UploadFolderAsync(client, host.Alpha, "pk-plain-link", null, ("pkg/a.md", "a"), ("pkg/sub/b.md", "b"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("pk-plain-link/pkg/sub is a link; nothing was uploaded.", await response.ErrorAsync());
        Assert.False(File.Exists(Path.Combine(outside, "b.md")));
        Assert.False(File.Exists(Docs.At(host.Alpha, "pk-plain-link/pkg/a.md")));
    }

    [Fact]
    public async Task An_unknown_onClash_is_refused_with_a_sentence()
    {
        using var client = await host.PersonAsync();

        var folder = await UploadFolderAsync(client, host.Alpha, "", "merge", ("pk-unknown/a.md", "a"));
        var zip = await UploadZipAsync(client, host.Alpha, "", "pk-unknown.zip", Zip(F("a.md", "a")), "merge");

        foreach (var response in new[] { folder, zip })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("onClash is ask, keep-both, replace or skip.", await response.ErrorAsync());
        }

        Assert.False(Directory.Exists(Docs.At(host.Alpha, "pk-unknown")));
    }

    [Fact]
    public async Task A_folder_or_zip_upload_into_a_gone_team_is_refused()
    {
        using var client = await host.PersonAsync();
        var gone = await GoneTeamAsync(host.Services, "PackageGone", "was.md");

        var folder = await UploadFolderAsync(client, gone, "", null, ("pkg/a.md", "a"));
        var zip = await UploadZipAsync(client, gone, "", "pkg.zip", Zip(F("a.md", "a")), null);

        foreach (var response in new[] { folder, zip })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("No team 'PackageGone'.", await response.ErrorAsync());
        }

        Assert.False(Directory.Exists(Docs.At(gone, "pkg")));
    }
}
