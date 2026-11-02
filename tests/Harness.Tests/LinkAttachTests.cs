using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Harness.Contracts;
using Harness.Contracts.Link;
using Harness.Host.Link;
using Harness.Tests.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harness.Tests;

/// <summary>
/// ATTACH AND DETACH: an instance spends a one-time token at a management service for a credential,
/// stores the link (the credential only as ciphertext), and forgets it on detach. A failed attach
/// stores nothing and keeps an earlier link; every attach, detach and refusal is one tenant row that
/// names the service and never the token or the credential; the operator's exchange report never
/// carries either. The service here is faked - no test calls a real one.
/// </summary>
public sealed class LinkAttachTests : IAsyncLifetime
{
    private const string Service = "https://manage.example.com";

    private const string Token = "one-time-token-9d2c";

    private const string Credential = "link-credential-4b7e";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly SqliteIdentityDatabase _database = new();

    private readonly IDataProtectionProvider _protection = new EphemeralDataProtectionProvider();

    private readonly FakeService _service = new();

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "yawble-link-" + Guid.NewGuid().ToString("N"));

    private ILinkStore Store => _database.Links();

    private ITenantLog Tenant => _database.TenantLog();

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _database.DisposeAsync();
        try
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private LinkAttach Attach(HttpMessageHandler? handler = null) =>
        new(Store, _protection, Tenant, new HttpClient(handler ?? _service), TimeProvider.System);

    private LinkRequests Requests(Signals? signals = null) =>
        new(Attach(), Store, _dataRoot, NullLogger<LinkRequests>.Instance, signals);

    // ---- attach -----------------------------------------------------------------------------------

    [Fact]
    public async Task Attach_stores_the_link_and_spends_nothing_on_failure()
    {
        var attach = Attach();

        var outcome = await attach.AttachAsync(Service, Token, Ct);

        Assert.True(outcome.Ok, outcome.Sentence);
        Assert.Equal("Attached to Example management as Workshop.", outcome.Sentence);
        var sent = Assert.Single(_service.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal(Service + LinkProtocol.AttachPath, sent.Uri);
        Assert.Equal(Token, JsonDocument.Parse(sent.Body).RootElement.GetProperty("token").GetString());

        var stored = await Store.GetAsync(Ct);
        Assert.NotNull(stored);
        Assert.Equal(Service, stored.ServiceUrl);
        Assert.Equal("Example management", stored.ServiceName);
        Assert.Equal("Workshop", stored.InstanceName);
        Assert.Equal(LinkState.Connecting, stored.State);
        Assert.Equal(Credential, LinkCredential.Unprotect(_protection, stored.CredentialCipher));
        Assert.Equal(stored, outcome.Link);

        // A token used twice: the service's sentence, verbatim; nothing stored, the earlier link kept.
        _service.Answer = (HttpStatusCode.Gone, """{ "error": "This token was already used. Ask for a new one." }""");

        var refused = await attach.AttachAsync("https://manage.example.com/other", "second-token", Ct);

        Assert.False(refused.Ok);
        Assert.Equal("This token was already used. Ask for a new one.", refused.Sentence);
        Assert.Null(refused.Link);
        Assert.Equal(stored, await Store.GetAsync(Ct));
    }

    [Fact]
    public async Task An_unreachable_service_says_so_naming_the_url()
    {
        // A loopback port nothing listens on: the connection is refused at once.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var url = $"http://127.0.0.1:{port}";

        var outcome = await Attach(new SocketsHttpHandler()).AttachAsync(url, Token, Ct);

        Assert.False(outcome.Ok);
        Assert.Equal($"Could not reach {url}.", outcome.Sentence);
        Assert.Null(await Store.GetAsync(Ct));
    }

    [Fact]
    public async Task Detach_clears_and_writes_a_tenant_row()
    {
        var attach = Attach();
        await attach.AttachAsync(Service, Token, Ct);

        var sentence = await attach.DetachAsync(Ct);

        Assert.Equal("Detached from Example management.", sentence);
        Assert.Null(await Store.GetAsync(Ct));
        var row = await Tenant.FindLatestAsync(TenantActions.LinkDetached, Service, Ct);
        Assert.NotNull(row);
        Assert.Equal("Example management", row.SubjectName);

        // Detached already: nothing to forget and no second row.
        Assert.Equal("Not attached.", await attach.DetachAsync(Ct));
        Assert.Single((await Tenant.ReadAsync(ct: Ct)).Events, e => e.Action == TenantActions.LinkDetached);
    }

    [Fact]
    public async Task Attach_writes_a_tenant_row_naming_the_service_not_the_credential()
    {
        var attach = Attach();
        await attach.AttachAsync(Service, Token, Ct);
        _service.Answer = (HttpStatusCode.Gone, """{ "error": "This token has expired." }""");
        await attach.AttachAsync(Service, "expired-token", Ct);

        var attached = await Tenant.FindLatestAsync(TenantActions.LinkAttached, Service, Ct);
        Assert.NotNull(attached);
        Assert.Equal("Example management", attached.SubjectName);
        Assert.Contains("Workshop", attached.Detail);

        var refused = await Tenant.FindLatestAsync(TenantActions.LinkRefused, Service, Ct);
        Assert.NotNull(refused);
        Assert.Contains("This token has expired.", refused.Detail);

        var stored = await Store.GetAsync(Ct);
        foreach (var row in (await Tenant.ReadAsync(ct: Ct)).Events)
        {
            var text = JsonSerializer.Serialize(row);
            Assert.DoesNotContain(Token, text);
            Assert.DoesNotContain("expired-token", text);
            Assert.DoesNotContain(Credential, text);
            Assert.DoesNotContain(stored!.CredentialCipher, text);
        }
    }

    [Fact]
    public async Task Environment_attach_runs_once_when_no_link_is_stored_and_never_overrides_one()
    {
        var signals = new Signals();
        var requests = Requests(signals);

        var first = await requests.AttachFromEnvironmentAsync(Service, Token, Ct);

        Assert.NotNull(first);
        Assert.True(first.Ok, first.Sentence);
        Assert.Single(_service.Requests);
        Assert.Equal(1, signals.Count);

        // The next start, with the variables still set: a link is stored, so the service is not asked again.
        var again = await Requests(signals).AttachFromEnvironmentAsync(Service, Token, Ct);
        Assert.Null(again);

        // Nor does another URL and token override it.
        var other = await Requests(signals).AttachFromEnvironmentAsync("https://manage.example.com/other", "other-token", Ct);
        Assert.Null(other);
        Assert.Single(_service.Requests);
        Assert.Equal(Service, (await Store.GetAsync(Ct))!.ServiceUrl);
        Assert.Equal(1, signals.Count);

        // Unset, it does nothing at all.
        await Store.ClearAsync(Ct);
        Assert.Null(await Requests(signals).AttachFromEnvironmentAsync(null, null, Ct));
        Assert.Single(_service.Requests);
    }

    [Fact]
    public async Task The_report_file_never_carries_the_token_or_credential()
    {
        var signals = new Signals();
        var requests = Requests(signals);
        requests.Prepare();
        var folder = Path.Combine(_dataRoot, LinkRequests.Folder);
        var report = Path.Combine(folder, LinkRequests.ReportFile);

        await AskAsync(folder, new { request = "n-1", op = "attach", url = Service, token = Token });
        Assert.True(await requests.AnswerAsync(Ct));
        var attached = await File.ReadAllTextAsync(report, Ct);

        using (var answer = JsonDocument.Parse(attached))
        {
            Assert.Equal("n-1", answer.RootElement.GetProperty("request").GetString());
            Assert.Equal(200, answer.RootElement.GetProperty("status").GetInt32());
            Assert.Equal("Attached to Example management as Workshop.", answer.RootElement.GetProperty("sentence").GetString());
        }

        Assert.Equal(1, signals.Count);
        Assert.False(File.Exists(Path.Combine(folder, LinkRequests.RequestFile)));

        await AskAsync(folder, new { request = "n-2", op = "status" });
        Assert.True(await requests.AnswerAsync(Ct));
        var status = await File.ReadAllTextAsync(report, Ct);

        using (var answer = JsonDocument.Parse(status))
        {
            var link = answer.RootElement.GetProperty("link");
            Assert.Equal("Example management", link.GetProperty("serviceName").GetString());
            Assert.Equal(Service, link.GetProperty("serviceUrl").GetString());
            Assert.Equal("Workshop", link.GetProperty("instanceName").GetString());
            Assert.Equal("connecting", link.GetProperty("state").GetString());
        }

        var cipher = (await Store.GetAsync(Ct))!.CredentialCipher;
        foreach (var text in new[] { attached, status })
        {
            Assert.DoesNotContain(Token, text);
            Assert.DoesNotContain(Credential, text);
            Assert.DoesNotContain(cipher, text);
        }

        await AskAsync(folder, new { request = "n-3", op = "detach" });
        Assert.True(await requests.AnswerAsync(Ct));
        Assert.Contains("Detached from Example management.", await File.ReadAllTextAsync(report, Ct));
        Assert.Equal(2, signals.Count);

        await AskAsync(folder, new { request = "n-4", op = "status" });
        Assert.True(await requests.AnswerAsync(Ct));
        using (var answer = JsonDocument.Parse(await File.ReadAllTextAsync(report, Ct)))
        {
            Assert.Equal("Not attached.", answer.RootElement.GetProperty("sentence").GetString());
            Assert.Equal(JsonValueKind.Null, answer.RootElement.GetProperty("link").ValueKind);
        }
    }

    private static async Task AskAsync(string folder, object request)
    {
        var temporary = Path.Combine(folder, LinkRequests.RequestFile + ".tmp");
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(request), Ct);
        File.Move(temporary, Path.Combine(folder, LinkRequests.RequestFile), overwrite: true);
    }

    /// <summary>Counts what the link client would be told.</summary>
    private sealed class Signals : ILinkSignal
    {
        public int Count { get; private set; }

        public void Changed() => Count++;
    }

    /// <summary>A management service that answers every attach with <see cref="Answer"/>.</summary>
    private sealed class FakeService : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Uri, string Body)> Requests { get; } = [];

        public (HttpStatusCode Status, string Body) Answer { get; set; } =
            (HttpStatusCode.OK, $$"""{ "credential": "{{Credential}}", "serviceName": "Example management", "instanceName": "Workshop" }""");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.Method, request.RequestUri!.ToString(), body));
            return new HttpResponseMessage(Answer.Status) { Content = new StringContent(Answer.Body, Encoding.UTF8, "application/json") };
        }
    }
}
