using System.Text.Json.Nodes;
using Yawble.Core.Gateway;
using Yawble.Core.Integrations;
using Yawble.Core.Ipc;
using Yawble.Core.Link;
using Yawble.Core.Sandboxing;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Integrations;

/// <summary>The local-only channel to the app's window for website actions, and image tool results.</summary>
public class WebsiteHostTests
{
    private const string Token = "0123456789abcdef0123456789abcdef-test";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WebsiteRequest Req(string op = "read") =>
        new(op, "partition-1", "page-a", "https://example.com/", new JsonObject { ["selector"] = "#x" });

    [Fact]
    public async Task RequestIsPublishedAndReplyCompletesIt()
    {
        var events = new HostEvents();
        var host = new ShellWebsiteHost(events, TimeSpan.FromSeconds(10));
        var seen = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = events.Subscribe(m => { seen.TrySetResult(m); return Task.CompletedTask; });
        using var bridge = host.BridgeConnected();
        Assert.True(host.Connected);

        var run = host.RunAsync(Req(), Ct);
        var pushed = await seen.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal("website.request", pushed["event"]!.GetValue<string>());
        var data = pushed["data"]!.AsObject();
        Assert.Equal(32, data["id"]!.GetValue<string>().Length);
        Assert.Equal("read", data["op"]!.GetValue<string>());
        Assert.Equal("partition-1", data["partition"]!.GetValue<string>());
        Assert.Equal("page-a", data["pageKey"]!.GetValue<string>());
        Assert.Equal("https://example.com/", data["siteUrl"]!.GetValue<string>());
        Assert.Equal("#x", data["args"]!["selector"]!.GetValue<string>());
        // no deadline given: the host's own clock and timeout
        var deadline = data["deadline"]!.GetValue<long>();
        var expected = (DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10)).ToUnixTimeMilliseconds();
        Assert.InRange(deadline, expected - 5000, expected + 1000);

        host.Reply(data["id"]!.GetValue<string>(), new JsonObject
        {
            ["ok"] = true, ["url"] = "https://example.com/a", ["title"] = "T", ["text"] = "hello",
            ["png"] = Convert.ToBase64String([1, 2, 3]), ["stoppedAt"] = "form",
        });
        var reply = await run;
        Assert.True(reply.Ok);
        Assert.Equal("https://example.com/a", reply.Url);
        Assert.Equal("T", reply.Title);
        Assert.Equal("hello", reply.Text);
        Assert.Equal(new byte[] { 1, 2, 3 }, reply.Png);
        Assert.Equal("form", reply.StoppedAt);
    }

    [Fact]
    public async Task UnknownReplyIdIsIgnored()
    {
        var events = new HostEvents();
        var host = new ShellWebsiteHost(events, TimeSpan.FromSeconds(30));
        var seen = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = events.Subscribe(m => { seen.TrySetResult(m); return Task.CompletedTask; });
        using var bridge = host.BridgeConnected();
        var run = host.RunAsync(Req(), Ct);
        var id = (await seen.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct))["data"]!["id"]!.GetValue<string>();

        host.Reply("nobody", new JsonObject { ["ok"] = true, ["text"] = "wrong" }); // must not throw or complete anything
        await Task.Delay(200, Ct);
        Assert.False(run.IsCompleted);

        host.Reply(id, new JsonObject { ["ok"] = true, ["text"] = "right" });
        Assert.Equal("right", (await run.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Text);
    }

    [Fact]
    public async Task APendingRequestFailsAtOnceWhenTheBridgeDrops()
    {
        var events = new HostEvents();
        var host = new ShellWebsiteHost(events, TimeSpan.FromSeconds(60));
        var seen = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = events.Subscribe(m => { seen.TrySetResult(m); return Task.CompletedTask; });
        var bridge = host.BridgeConnected();
        var run = host.RunAsync(Req(), Ct);
        await seen.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        bridge.Dispose();

        var ex = await Assert.ThrowsAsync<WebsiteException>(() => run.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.Equal("website actions need the app to be open on this computer", ex.Message);
    }

    [Fact]
    public async Task SignedInIsForwardedAtOnceAndOnlyFromTheBridge()
    {
        var websites = new ShellWebsiteHost(new HostEvents(), TimeSpan.FromSeconds(10));
        var seen = new List<long>();
        await using var server = IpcServer.Start(IpcEndpoint.Create(IpcEndpoint.NewAddress(Path.GetTempPath())), Token,
            new RpcDispatcher(new SandboxPlatformInfo(), () => []), new AppEvents(), new HostEvents(), websites, id => { lock (seen) seen.Add(id); });
        await using var client = await TestIpcClient.ConnectAsync(server.Address, Ct);
        await client.AuthAsync(Token, Ct);

        var before = await client.CallAsync("hostBridge.signedIn", new JsonObject { ["connectionId"] = 7 }, Ct);
        Assert.Equal("not the bridge", before!["error"]!["message"]!.GetValue<string>());
        await client.CallAsync("hostBridge.hello", new JsonObject(), Ct);
        var noId = await client.CallAsync("hostBridge.signedIn", new JsonObject(), Ct);
        Assert.NotNull(noId!["error"]);
        var ok = await client.CallAsync("hostBridge.signedIn", new JsonObject { ["connectionId"] = 7 }, Ct);
        Assert.True(ok!["result"]!["ok"]!.GetValue<bool>());
        lock (seen) Assert.Equal([7L], seen);
    }

    [Fact]
    public async Task OnlyTheBridgeConnectionMayReply()
    {
        var websites = new ShellWebsiteHost(new HostEvents(), TimeSpan.FromSeconds(10));
        await using var server = IpcServer.Start(IpcEndpoint.Create(IpcEndpoint.NewAddress(Path.GetTempPath())), Token,
            new RpcDispatcher(new SandboxPlatformInfo(), () => []), new AppEvents(), new HostEvents(), websites);
        await using var client = await TestIpcClient.ConnectAsync(server.Address, Ct);
        await client.AuthAsync(Token, Ct);

        var before = await client.CallAsync("hostBridge.reply", new JsonObject { ["id"] = "x", ["ok"] = true }, Ct);
        Assert.Equal("not the bridge", before!["error"]!["message"]!.GetValue<string>());

        await client.CallAsync("hostBridge.hello", new JsonObject(), Ct);
        var noId = await client.CallAsync("hostBridge.reply", new JsonObject { ["ok"] = true }, Ct);
        Assert.NotNull(noId!["error"]);
        Assert.Null(noId["result"]);
    }

    [Fact]
    public async Task ReplyTimesOut()
    {
        var host = new ShellWebsiteHost(new HostEvents(), TimeSpan.FromMilliseconds(100));
        using var bridge = host.BridgeConnected();
        var ex = await Assert.ThrowsAsync<WebsiteException>(() => host.RunAsync(Req(), Ct));
        Assert.Equal("the app's window did not answer in time; try again", ex.Message);
    }

    [Fact]
    public async Task NoBridgeSaysSo()
    {
        var host = new ShellWebsiteHost(new HostEvents(), TimeSpan.FromSeconds(5));
        Assert.False(host.Connected);
        var ex = await Assert.ThrowsAsync<WebsiteException>(() => host.RunAsync(Req(), Ct));
        Assert.Equal("website actions need the app to be open on this computer", ex.Message);

        var bridge = host.BridgeConnected();
        Assert.True(host.Connected);
        bridge.Dispose();
        Assert.False(host.Connected);
    }

    [Fact]
    public async Task OversizedPngIsRefused()
    {
        var events = new HostEvents();
        var host = new ShellWebsiteHost(events, TimeSpan.FromSeconds(10));
        var seen = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = events.Subscribe(m => { seen.TrySetResult(m); return Task.CompletedTask; });
        using var bridge = host.BridgeConnected();
        var run = host.RunAsync(Req("screenshot"), Ct);
        var id = (await seen.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct))["data"]!["id"]!.GetValue<string>();

        host.Reply(id, new JsonObject { ["ok"] = true, ["png"] = Convert.ToBase64String(new byte[4 * 1024 * 1024 + 1]) });

        var reply = await run;
        Assert.False(reply.Ok);
        Assert.Null(reply.Png);
        Assert.NotNull(reply.Error);
    }

    [Fact]
    public async Task HostEventsReachLocalConnectionsOnly()
    {
        // The Cloud Link is built from AppEvents only: a HostEvents cannot be handed to it.
        var linkEvents = typeof(CloudLink).GetConstructors().Single().GetParameters().Select(p => p.ParameterType);
        Assert.Contains(typeof(AppEvents), linkEvents);
        Assert.DoesNotContain(typeof(HostEvents), linkEvents);

        var appEvents = new AppEvents();
        var hostEvents = new HostEvents();
        var websites = new ShellWebsiteHost(hostEvents, TimeSpan.FromSeconds(10));
        await using var server = IpcServer.Start(IpcEndpoint.Create(IpcEndpoint.NewAddress(Path.GetTempPath())), Token,
            new RpcDispatcher(new SandboxPlatformInfo(), () => []), appEvents, hostEvents, websites);
        await using var client = await TestIpcClient.ConnectAsync(server.Address, Ct);
        Assert.True((await client.AuthAsync(Token, Ct))!["result"]!["authenticated"]!.GetValue<bool>());

        Assert.False(websites.Connected);
        Assert.True((await client.CallAsync("hostBridge.hello", new JsonObject(), Ct))!["result"]!["ok"]!.GetValue<bool>());
        Assert.True(websites.Connected);

        // the window's main connection (authenticated, never said hello) gets app events but never a website request
        await using var main = await TestIpcClient.ConnectAsync(server.Address, Ct);
        await main.AuthAsync(Token, Ct);

        var run = websites.RunAsync(Req() with { Deadline = 1234 }, Ct);
        var pushed = await client.ReadEventAsync(Ct);
        Assert.Equal(1234, pushed!["data"]!["deadline"]!.GetValue<long>());
        appEvents.Publish("probe", null);
        Assert.Equal("probe", (await main.ReadEventAsync(Ct))!["event"]!.GetValue<string>());
        Assert.Equal("website.request", pushed!["event"]!.GetValue<string>());
        var id = pushed["data"]!["id"]!.GetValue<string>();

        var answered = await client.CallAsync("hostBridge.reply", new JsonObject { ["id"] = id, ["ok"] = true, ["text"] = "page" }, Ct);
        Assert.True(answered!["result"]!["ok"]!.GetValue<bool>());
        Assert.Equal("page", (await run).Text);
    }

    [Fact]
    public async Task ABridgeConnectionClosingMarksTheHostDisconnected()
    {
        var websites = new ShellWebsiteHost(new HostEvents(), TimeSpan.FromSeconds(10));
        await using var server = IpcServer.Start(IpcEndpoint.Create(IpcEndpoint.NewAddress(Path.GetTempPath())), Token,
            new RpcDispatcher(new SandboxPlatformInfo(), () => []), new AppEvents(), new HostEvents(), websites);
        var client = await TestIpcClient.ConnectAsync(server.Address, Ct);
        await client.AuthAsync(Token, Ct);
        await client.CallAsync("hostBridge.hello", new JsonObject(), Ct);
        Assert.True(websites.Connected);

        await client.DisposeAsync();
        for (var i = 0; i < 100 && websites.Connected; i++) await Task.Delay(50, Ct);
        Assert.False(websites.Connected);
    }

    [Fact]
    public void ImageToolResultHasImageContent()
    {
        var withImage = McpBusServer.ToolResult("shot", isError: false, [1, 2, 3]);
        var content = withImage["content"]!.AsArray();
        Assert.Equal(2, content.Count);
        Assert.Equal("text", content[0]!["type"]!.GetValue<string>());
        Assert.Equal("shot", content[0]!["text"]!.GetValue<string>());
        Assert.Equal("image", content[1]!["type"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), content[1]!["data"]!.GetValue<string>());
        Assert.Equal("image/png", content[1]!["mimeType"]!.GetValue<string>());

        Assert.Single(McpBusServer.ToolResult("plain", isError: false)["content"]!.AsArray());
    }
}
