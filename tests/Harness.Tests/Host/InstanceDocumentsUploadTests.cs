using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// A .zip uploaded into the INSTANCE's own documents, needing no team (`POST /api/documents/upload-zip`):
/// unpacked into `Uploads/&lt;zip name&gt;` by the team route's own checks and clash choices, answered
/// with the absolute folder the install routes take, a person's only, one tenant row.
/// </summary>
public sealed class InstanceDocumentsUploadTests(HostFixture host) : IClassFixture<HostFixture>
{
    private const string Route = "/api/documents/upload-zip";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private TeamDocuments Docs => host.Services.GetRequiredService<TeamDocuments>();

    private string Uploads => Path.Combine(Docs.InstanceRoot, TeamDocuments.UploadsFolder);

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string name, byte[] zip, string? onClash = null)
    {
        using var form = new MultipartFormDataContent { { new ByteArrayContent(zip), "file", name } };
        if (onClash is not null) form.Add(new StringContent(onClash), "onClash");

        return await client.PostAsync(Route, form, Ct);
    }

    /// <summary>A zip of these entries; a link is a Unix symlink entry whose content is its target.</summary>
    private static byte[] Zip(params (string Name, string Body, bool Link)[] entries)
    {
        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, body, link) in entries)
            {
                var entry = archive.CreateEntry(name);
                if (link) entry.ExternalAttributes = unchecked((int)(0xA1FFu << 16));

                using var stream = entry.Open();
                stream.Write(Encoding.UTF8.GetBytes(body));
            }
        }

        return buffer.ToArray();
    }

    private static (string, string, bool) F(string name, string body) => (name, body, false);

    private async Task<long> RowCountAsync() =>
        (await host.Services.GetRequiredService<ITenantLog>().ReadAsync(null, 1000, Ct)).Total;

    [Fact]
    public async Task A_zip_with_no_team_is_unpacked_into_uploads_and_answers_its_absolute_path_with_one_tenant_row()
    {
        using var client = await host.PersonAsync();

        var response = await UploadAsync(client, "iu-notes.zip", Zip(F("solution.json", "{}"), F("members/a.md", "a")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.BodyAsync();
        Assert.Equal("Uploads/iu-notes", body.GetProperty("path").GetString());
        Assert.Equal("iu-notes", body.GetProperty("name").GetString());
        Assert.Equal(
            Path.Combine(Docs.InstanceRoot, "Uploads", "iu-notes").Replace('\\', '/'),
            body.GetProperty("absolutePath").GetString());
        Assert.Equal(
            ["Uploads/iu-notes/members/a.md", "Uploads/iu-notes/solution.json"],
            body.GetProperty("files").EnumerateArray().Select(f => f.GetString()!).Order(StringComparer.Ordinal));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(Uploads, "iu-notes", "solution.json")));
        Assert.Equal("a", File.ReadAllText(Path.Combine(Uploads, "iu-notes", "members", "a.md")));

        var row = await host.Services.GetRequiredService<ITenantLog>().FindLatestAsync(TenantActions.DocumentUploaded, "Uploads/iu-notes", Ct);
        Assert.NotNull(row);
        using var detail = JsonDocument.Parse(row.Detail!);
        Assert.Equal("zip", detail.RootElement.GetProperty("kind").GetString());
        Assert.Equal("Uploads/iu-notes", detail.RootElement.GetProperty("path").GetString());
        Assert.Equal(JsonValueKind.Null, detail.RootElement.GetProperty("team").ValueKind);
        Assert.Equal(2, detail.RootElement.GetProperty("files").GetInt32());
    }

    [Fact]
    public async Task A_plugin_version_folder_uploaded_with_no_team_installs_from_the_answered_path()
    {
        using var client = await host.PersonAsync();
        var manifest = PluginInstall.Manifest("iu-echo", "0.3.0").ToJsonString();

        var upload = await UploadAsync(client, "iu-echo-0.3.0.zip", Zip(
            F(PluginManifest.FileName, manifest),
            F("bin/run", "#!/bin/sh\ncat >/dev/null; echo '{\"t\":\"result\",\"ok\":true,\"output\":\"ok\"}'\n")));
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var path = (await upload.BodyAsync()).GetProperty("absolutePath").GetString()!;

        var install = await client.PostAsJsonAsync("/api/plugins/install", new { path, replace = false }, Ct);
        var body = await install.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(HttpStatusCode.OK, install.StatusCode);
        Assert.True(body.GetProperty("installed").GetBoolean(), body.GetRawText());
        Assert.Equal("iu-echo", body.GetProperty("id").GetString());
        Assert.Equal("0.3.0", host.Services.GetRequiredService<PluginCatalog>().For("iu-echo")!.Manifest.Version);
    }

    [Theory]
    [InlineData("link", "The zip was not unpacked: passwd is a link.")]
    [InlineData("absolute", "The zip was not unpacked: /tmp/outside.md is an absolute path.")]
    [InlineData("dotdot", "The zip was not unpacked: ../../outside.md leaves the folder.")]
    public async Task An_unsafe_zip_is_refused_whole_and_nothing_is_written(string kind, string error)
    {
        using var client = await host.PersonAsync();
        var rows = await RowCountAsync();
        var name = $"iu-evil-{kind}";
        var bad = kind switch
        {
            "link" => ("passwd", "/etc/passwd", true),
            "absolute" => F("/tmp/outside.md", "out"),
            _ => F("../../outside.md", "out"),
        };

        var response = await UploadAsync(client, name + ".zip", Zip(F("a.md", "a"), bad));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(error, await response.ErrorAsync());
        Assert.False(Directory.Exists(Path.Combine(Uploads, name)));
        Assert.False(File.Exists(Path.Combine(Docs.InstanceRoot, "outside.md")));
        Assert.Equal(rows, await RowCountAsync());
    }

    [Fact]
    public async Task A_clash_under_ask_answers_409_and_keep_both_makes_a_copy()
    {
        using var client = await host.PersonAsync();
        Directory.CreateDirectory(Path.Combine(Uploads, "iu-pkg"));
        File.WriteAllText(Path.Combine(Uploads, "iu-pkg", "a.md"), "old");
        var rows = await RowCountAsync();

        var ask = await UploadAsync(client, "iu-pkg.zip", Zip(F("a.md", "new")), "ask");

        Assert.Equal(HttpStatusCode.Conflict, ask.StatusCode);
        var asked = await ask.BodyAsync();
        Assert.Equal("iu-pkg is already in Uploads. Choose Keep both, Replace or Skip.", asked.GetProperty("error").GetString());
        var clash = Assert.Single(asked.GetProperty("clashes").EnumerateArray());
        Assert.Equal("Uploads/iu-pkg", clash.GetProperty("to").GetString());
        Assert.True(clash.GetProperty("isFolder").GetBoolean());
        Assert.Equal("old", File.ReadAllText(Path.Combine(Uploads, "iu-pkg", "a.md")));
        Assert.Equal(rows, await RowCountAsync());

        var keep = await UploadAsync(client, "iu-pkg.zip", Zip(F("a.md", "new")), "keep-both");

        Assert.Equal(HttpStatusCode.OK, keep.StatusCode);
        var kept = await keep.BodyAsync();
        Assert.Equal("Uploads/iu-pkg (copy)", kept.GetProperty("path").GetString());
        Assert.EndsWith("/Uploads/iu-pkg (copy)", kept.GetProperty("absolutePath").GetString());
        Assert.Equal("new", File.ReadAllText(Path.Combine(Uploads, "iu-pkg (copy)", "a.md")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(Uploads, "iu-pkg", "a.md")));

        var skip = await UploadAsync(client, "iu-pkg.zip", Zip(F("a.md", "new")), "skip");

        Assert.Equal(HttpStatusCode.OK, skip.StatusCode);
        var skipped = await skip.BodyAsync();
        Assert.True(skipped.GetProperty("skipped").GetBoolean());
        Assert.Equal("Uploads/iu-pkg", skipped.GetProperty("path").GetString());
        Assert.EndsWith("/Uploads/iu-pkg", skipped.GetProperty("absolutePath").GetString());
    }

    [Fact]
    public async Task An_uploads_folder_that_is_a_link_is_refused_and_nothing_is_written_through_it()
    {
        using var client = await host.PersonAsync();
        var outside = Directory.CreateTempSubdirectory("harness-iu-outside-").FullName;
        var moved = Directory.Exists(Uploads) ? Uploads + "-kept-" + Guid.NewGuid().ToString("N") : null;
        if (moved is not null) Directory.Move(Uploads, moved);
        Directory.CreateDirectory(Docs.InstanceRoot);
        Directory.CreateSymbolicLink(Uploads, outside);

        try
        {
            var response = await UploadAsync(client, "iu-through.zip", Zip(F("a.md", "a")));

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("Uploads is a link; nothing was uploaded.", await response.ErrorAsync());
            Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
        }
        finally
        {
            Directory.Delete(Uploads);
            if (moved is not null) Directory.Move(moved, Uploads);
        }
    }

    [Fact]
    public async Task A_machine_principal_is_refused_with_the_humans_only_body_and_nothing_is_written()
    {
        using var machine = host.Container(host.AlphaContainerKey);

        var response = await UploadAsync(machine, "iu-machine.zip", Zip(F("a.md", "a")));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(PermitGate.HumansOnlyMessage, await response.ErrorAsync());
        Assert.False(Directory.Exists(Path.Combine(Uploads, "iu-machine")));
    }

    [Fact]
    public void The_route_asks_for_the_zip_routes_body_limit()
    {
        var endpoint = host.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Single(e => "/" + e.RoutePattern.RawText?.TrimStart('/') == Route);

        var asked = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata>();

        Assert.Equal(TeamDocuments.MaximumUploadBytes + 1024 * 1024, asked?.MaxRequestBodySize);
    }
}
