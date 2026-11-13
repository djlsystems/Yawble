using System.Text.Json.Nodes;
using Yawble.CodeBrowser;
using Yawble.Core.Code;
using Yawble.Core.Ipc;
using Yawble.Core.Link;
using Yawble.Core.Provenance;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Code;

/// <summary>The code browser's core wiring: the code. RPCs through the dispatcher, the Cloud Link allowing and refusing them, and "Show members' code to cloud viewers".</summary>
public sealed class CodeBrowserWiringTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "yawble-cbw-" + Guid.NewGuid().ToString("N")[..10]);
    private readonly string _clone;
    private readonly CodeBrowserService _service;

    public CodeBrowserWiringTests()
    {
        _clone = Path.Combine(_tmp, "clone");
        Directory.CreateDirectory(Path.Combine(_clone, "src"));
        File.WriteAllText(Path.Combine(_clone, "src", "a.cs"), "class A {}\n");
        _service = new CodeBrowserService(new OneMember(_clone), new CodeBrowserOptions { Tracker = new TrackerOptions { Watch = false } });
    }

    public async ValueTask DisposeAsync()
    {
        await _service.DisposeAsync();
        try { Directory.Delete(_tmp, recursive: true); } catch (IOException) { }
    }

    private sealed class OneMember(string clone) : ICodeHost
    {
        public Task<CodeMember?> FindAsync(MemberQuery query, CancellationToken ct) =>
            Task.FromResult<CodeMember?>(query.Member == "Ada" ? new CodeMember("1/2", "web", "Ada", clone, null) : null);
        public Task<IReadOnlyList<(CodeMember Member, DateTimeOffset StartedAt)>> RunningAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<(CodeMember, DateTimeOffset)>>([]);
        public void Publish(string name, JsonNode data) { }
        public IGitReader? Git => null;
        public bool PolicyDenied(string fullPath) => false;
    }

    private RpcDispatcher Dispatcher(CloudLink? link = null) =>
        new(new Sandboxing.SandboxPlatformInfo(), () => [], link: link, code: new CodeBrowserRpc(_service));

    [Theory]
    [InlineData("code.open")]
    [InlineData("code.list")]
    [InlineData("code.read")]
    [InlineData("code.download")]
    [InlineData("code.close")]
    public void ACloudViewerMayUseTheCodeBrowser(string method)
    {
        Assert.Null(RemoteAccess.Refusal(method, new JsonObject(), allowHostTerminals: false));
        Assert.Null(RemoteAccess.Refusal(method, new JsonObject(), allowHostTerminals: false, showCode: true));
    }

    [Theory]
    [InlineData("code.open")]
    [InlineData("code.read")]
    [InlineData("code.download")]
    public void WithTheSettingOffEveryCodeCallIsRefused(string method)
    {
        var refusal = RemoteAccess.Refusal(method, new JsonObject(), allowHostTerminals: true, showCode: false);
        Assert.NotNull(refusal);
        Assert.Contains("Settings > Cloud Link", refusal);
        // The setting is the person's at the computer: link. stays refused from the cloud.
        Assert.NotNull(RemoteAccess.Refusal("link.setShowCode", new JsonObject { ["show"] = true }, allowHostTerminals: true));
    }

    [Fact]
    public void CodeChangedGoesUpOnlyWhenTheSettingIsOn()
    {
        var e = new JsonObject { ["event"] = CodeBrowserService.ChangedEvent, ["data"] = new JsonObject { ["path"] = "a.cs", ["cloud"] = true } };
        Assert.True(CloudLink.Forwards(e, showCode: true));
        Assert.False(CloudLink.Forwards(e, showCode: false));
        Assert.True(CloudLink.Forwards(new JsonObject { ["event"] = "board.changed" }, showCode: false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void TheWindowsCodeChangedStaysOnThisComputer(bool? cloud)
    {
        var data = new JsonObject { ["session"] = "cb-1", ["path"] = "a.cs" };
        if (cloud is { } c) data["cloud"] = c;
        Assert.False(CloudLink.Forwards(new JsonObject { ["event"] = CodeBrowserService.ChangedEvent, ["data"] = data }, showCode: true));
        // A string "true" is not true.
        Assert.False(CloudLink.Forwards(new JsonObject { ["event"] = CodeBrowserService.ChangedEvent, ["data"] = new JsonObject { ["cloud"] = "true" } }, showCode: true));
    }

    [Fact]
    public async Task ACloudViewersSessionIsMarkedForTheCloud_TheWindowsIsNot()
    {
        var host = new Recording(_clone);
        await using var service = new CodeBrowserService(host, new CodeBrowserOptions { Tracker = new TrackerOptions { Watch = false } });
        var d = new RpcDispatcher(new Sandboxing.SandboxPlatformInfo(), () => [], code: new CodeBrowserRpc(service));
        var window = await d.DispatchAsAsync(Actor.Window, "code.open", new JsonObject { ["team"] = "web", ["member"] = "Ada" }, Ct);
        var cloud = await d.DispatchAsAsync(Actor.CloudViewer("v@example.test"), "code.open", new JsonObject { ["team"] = "web", ["member"] = "Ada" }, Ct);
        Assert.Equal(Path.GetFullPath(_clone), window!["root"]!.GetValue<string>());
        Assert.Equal("Ada", cloud!["root"]!.GetValue<string>()); // never the host's folder
        // A read takes the baseline; the agent edits; the next read takes the change (and pushes it).
        var read = new JsonObject { ["session"] = window["session"]!.GetValue<string>(), ["path"] = "src/a.cs" };
        await d.DispatchAsAsync(Actor.Window, "code.read", read.DeepClone().AsObject(), Ct);
        File.WriteAllText(Path.Combine(_clone, "src", "a.cs"), "class A {}\nclass B {}\n");
        await d.DispatchAsAsync(Actor.Window, "code.read", read.DeepClone().AsObject(), Ct);
        List<JsonObject> pushed;
        lock (host.Events) pushed = [.. host.Events];
        Assert.Equal(2, pushed.Count);
        var up = pushed.Where(e => CloudLink.Forwards(new JsonObject { ["event"] = CodeBrowserService.ChangedEvent, ["data"] = e.DeepClone() })).ToList();
        Assert.Equal(cloud["session"]!.GetValue<string>(), Assert.Single(up)["session"]!.GetValue<string>());
    }

    private sealed class Recording(string clone) : ICodeHost
    {
        public List<JsonObject> Events { get; } = [];
        public Task<CodeMember?> FindAsync(MemberQuery query, CancellationToken ct) =>
            Task.FromResult<CodeMember?>(query.Member == "Ada" ? new CodeMember("1/2", "web", "Ada", clone, null) : null);
        public Task<IReadOnlyList<(CodeMember Member, DateTimeOffset StartedAt)>> RunningAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<(CodeMember, DateTimeOffset)>>([]);
        public void Publish(string name, JsonNode data) { lock (Events) Events.Add(data.AsObject()); }
        public IGitReader? Git => null;
        public bool PolicyDenied(string fullPath) => false;
    }

    private sealed class StepClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void APolicyThatCannotBeReadDeniesEverything_NeverAllows()
    {
        var clock = new StepClock();
        var fail = true;
        var denied = new DeniedPaths(() => fail ? throw new InvalidOperationException("the store is locked") : [Path.Combine(_tmp, "secret")], clock);
        Assert.True(denied.IsDenied(Path.Combine(_clone, "src", "a.cs"))); // nothing read yet: fail closed

        clock.Now += TimeSpan.FromMinutes(1);
        fail = false;
        Assert.False(denied.IsDenied(Path.Combine(_clone, "src", "a.cs")));
        Assert.True(denied.IsDenied(Path.Combine(_tmp, "secret", "key.txt")));

        // The store breaks later: the last good list stands (still denying what it denied).
        clock.Now += TimeSpan.FromMinutes(1);
        fail = true;
        Assert.True(denied.IsDenied(Path.Combine(_tmp, "secret", "key.txt")));
        Assert.False(denied.IsDenied(Path.Combine(_clone, "src", "a.cs")));
    }

    [Fact]
    public void AnyExceptionFromTheStoreFailsClosed()
    {
        var denied = new DeniedPaths(() => throw new IOException("disk"));
        Assert.True(denied.IsDenied(Path.Combine(_clone, "src", "a.cs")));
        var bad = new DeniedPaths(() => throw new Microsoft.Data.Sqlite.SqliteException("corrupt", 11));
        Assert.True(bad.IsDenied(Path.Combine(_clone, "src", "a.cs")));
    }

    [Fact]
    public void TheSettingIsOnByDefaultAndKept()
    {
        var data = Path.Combine(_tmp, "data");
        Directory.CreateDirectory(data);
        // Written before the setting existed: no showCode key at all.
        File.WriteAllText(Path.Combine(data, "cloud-link.json"),
            """{"url":"https://cloud.example.test/","instanceId":"i1","secret":"s","instanceName":"pc","org":"o","workgroup":"w","allowHostTerminals":false}""");
        var link = new CloudLink(data, (_, _, _) => Task.FromResult<JsonNode?>(null), new AppEvents());
        Assert.True(link.ShowCode);
        Assert.True(link.Status()["showCode"]!.GetValue<bool>());
        link.SetShowCode(false);
        Assert.False(new CloudLink(data, (_, _, _) => Task.FromResult<JsonNode?>(null), new AppEvents()).ShowCode);

        var unattached = Path.Combine(_tmp, "fresh");
        Directory.CreateDirectory(unattached);
        var fresh = new CloudLink(unattached, (_, _, _) => Task.FromResult<JsonNode?>(null), new AppEvents());
        Assert.True(fresh.Status()["showCode"]!.GetValue<bool>());
        Assert.Equal("bad_state", Assert.Throws<RpcException>(() => fresh.SetShowCode(false)).Code);
    }

    [Fact]
    public async Task TheWindowTurnsTheSettingOffThroughTheDispatcher()
    {
        var data = Path.Combine(_tmp, "data2");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "cloud-link.json"), """{"url":"https://cloud.example.test/","instanceId":"i1","secret":"s"}""");
        var link = new CloudLink(data, (_, _, _) => Task.FromResult<JsonNode?>(null), new AppEvents());
        var status = await Dispatcher(link).DispatchAsAsync(Actor.Window, "link.setShowCode", new JsonObject { ["show"] = false }, Ct);
        Assert.False(status!["showCode"]!.GetValue<bool>());
        Assert.False(link.ShowCode);
    }

    [Fact]
    public async Task TheDispatcherRoutesCodeCallsAndMapsRefusals()
    {
        var d = Dispatcher();
        var open = await d.DispatchAsAsync(Actor.Window, "code.open", new JsonObject { ["team"] = "web", ["member"] = "Ada" }, Ct);
        var session = open!["session"]!.GetValue<string>();
        var list = await d.DispatchAsAsync(Actor.Window, "code.list", new JsonObject { ["session"] = session, ["path"] = "src" }, Ct);
        Assert.Equal("src/a.cs", list!["entries"]![0]!["path"]!.GetValue<string>());
        var refused = await Assert.ThrowsAsync<RpcException>(() => d.DispatchAsAsync(Actor.Window, "code.read", new JsonObject { ["session"] = session, ["path"] = "../x" }, Ct));
        Assert.Equal("code.refused", refused.Code);
        // A cloud viewer cannot use the window's session.
        var other = await Assert.ThrowsAsync<RpcException>(() => d.DispatchAsAsync(Actor.CloudViewer("v@example.test"), "code.list", new JsonObject { ["session"] = session, ["path"] = "" }, Ct));
        Assert.Equal("code.refused", other.Code);
        // ...but opens its own.
        Assert.NotNull((await d.DispatchAsAsync(Actor.CloudViewer("v@example.test"), "code.open", new JsonObject { ["team"] = "web", ["member"] = "Ada" }, Ct))!["session"]);
    }

    [Fact]
    public async Task OnlyThePersonMayCall()
    {
        var d = Dispatcher();
        var e = await Assert.ThrowsAsync<RpcException>(() => d.DispatchAsAsync(new Actor(Actor.Concierge, "x"), "code.open", new JsonObject { ["team"] = "web", ["member"] = "Ada" }, Ct));
        Assert.Equal("forbidden", e.Code);
    }

    [Fact]
    public void OpenAndCloseAreNotAudited()
    {
        Assert.True(AuditLog.IsReadOnly("code.open"));
        Assert.True(AuditLog.IsReadOnly("code.list"));
        Assert.True(AuditLog.IsReadOnly("code.read"));
        Assert.True(AuditLog.IsReadOnly("code.close"));
        Assert.False(AuditLog.IsReadOnly("code.download"));
    }
}
