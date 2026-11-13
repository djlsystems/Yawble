using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Integrations;
using Xunit;

namespace Yawble.Core.Tests.Integrations;

public class McpHttpClientTests
{
    private const string Url = "https://mcp.example.test/mcp";

    // the client disposes a request once sent, so a body is read (inside the fake server) the first time and remembered for later assertions
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<HttpRequestMessage, JsonObject> Bodies = new();
    private static JsonObject Body(HttpRequestMessage r) => Bodies.GetValue(r, q => (JsonObject)JsonNode.Parse(q.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!);

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK, string type = "application/json")
        => new(status) { Content = new StringContent(body, Encoding.UTF8, type) };

    private static string Reply(JsonObject req, string resultJson) => $"{{\"jsonrpc\":\"2.0\",\"id\":{req["id"]},\"result\":{resultJson}}}";

    /// <summary>A server: answers initialize (optionally with a session id), accepts the initialized notification, and passes tools/* to <paramref name="rest"/>.</summary>
    private static FakeHttp Server(Func<HttpRequestMessage, JsonObject, HttpResponseMessage> rest, string? session = null, string initResult = "{\"serverInfo\":{\"name\":\"fake\"}}")
    {
        var http = new FakeHttp();
        http.Add(Url, r =>
        {
            if (r.Method == HttpMethod.Delete) return new HttpResponseMessage(HttpStatusCode.OK);
            var b = Body(r);
            switch (b["method"]!.GetValue<string>())
            {
                case "initialize":
                    var resp = Json(Reply(b, initResult));
                    if (session is not null) resp.Headers.Add("Mcp-Session-Id", session);
                    return resp;
                case "notifications/initialized": return new HttpResponseMessage(HttpStatusCode.Accepted);
                default: return rest(r, b);
            }
        });
        return http;
    }

    private static McpHttpClient Client(HttpMessageHandler h, Func<CancellationToken, Task<IReadOnlyDictionary<string, string>>>? headers = null)
        => new(new Uri(Url), headers ?? (_ => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>())), h);

    [Fact]
    public async Task JsonReplyIsReturned()
    {
        var http = Server((_, b) => Json(Reply(b, "{\"tools\":[{\"name\":\"a\"}]}")));
        await using var c = Client(http);
        var tools = await c.ListToolsAsync(TestContext.Current.CancellationToken);
        Assert.Equal("a", tools[0]!["name"]!.GetValue<string>());
        var init = http.Requests[0];
        Assert.Equal(HttpMethod.Post, init.Method);
        Assert.Contains("application/json", init.Headers.Accept.ToString());
        Assert.Contains("text/event-stream", init.Headers.Accept.ToString());
        var p = Body(init)["params"]!;
        Assert.Equal("2025-06-18", p["protocolVersion"]!.GetValue<string>());
        Assert.Equal("yawble", p["clientInfo"]!["name"]!.GetValue<string>());
        Assert.Equal("notifications/initialized", Body(http.Requests[1])["method"]!.GetValue<string>());
    }

    [Fact]
    public async Task CallToolJoinsTextAndReportsIsError()
    {
        var http = Server((_, b) => Json(Reply(b, "{\"content\":[{\"type\":\"text\",\"text\":\"hi\"},{\"type\":\"image\"}],\"isError\":true}")));
        await using var c = Client(http);
        var (text, err) = await c.CallToolAsync("t", new JsonObject(), TestContext.Current.CancellationToken);
        Assert.Equal("hi\n[image]", text);
        Assert.True(err);
    }

    [Fact]
    public async Task SseReplySkipsNotifications()
    {
        var http = Server((_, b) =>
        {
            var sse = "event: message\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\",\"params\":{}}\n\n" +
                      "data: {\"jsonrpc\":\"2.0\",\"id\":999,\"method\":\"ping\"}\n\n" +
                      $"data: {Reply(b, "{\"tools\":[{\"name\":\"s\"}]}")}\n\n";
            return Json(sse, type: "text/event-stream");
        });
        await using var c = Client(http);
        var tools = await c.ListToolsAsync(TestContext.Current.CancellationToken);
        Assert.Equal("s", tools[0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task SessionIdIsSentAfterInitialize()
    {
        var http = Server((_, b) => Json(Reply(b, "{\"tools\":[]}")), session: "sess-1");
        await using var c = Client(http);
        await c.ListToolsAsync(TestContext.Current.CancellationToken);
        Assert.False(http.Requests[0].Headers.Contains("Mcp-Session-Id"));
        Assert.False(http.Requests[0].Headers.Contains("MCP-Protocol-Version"));
        var list = http.Requests[2];
        Assert.Equal("sess-1", list.Headers.GetValues("Mcp-Session-Id").Single());
        Assert.Equal("2025-06-18", list.Headers.GetValues("MCP-Protocol-Version").Single());
        await c.DisposeAsync();
        var del = http.Requests[^1];
        Assert.Equal(HttpMethod.Delete, del.Method);
        Assert.Equal("sess-1", del.Headers.GetValues("Mcp-Session-Id").Single());
    }

    [Fact]
    public async Task ExpiredSessionReinitializesOnce()
    {
        var calls = 0;
        var n = 0;
        var http = new FakeHttp();
        http.Add(Url, r =>
        {
            var b = Body(r);
            switch (b["method"]!.GetValue<string>())
            {
                case "initialize":
                    var resp = Json(Reply(b, "{}"));
                    resp.Headers.Add("Mcp-Session-Id", "s" + (++n));
                    return resp;
                case "notifications/initialized": return new HttpResponseMessage(HttpStatusCode.Accepted);
                default:
                    calls++;
                    return calls == 1 ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json(Reply(b, "{\"tools\":[]}"));
            }
        });
        await using var c = Client(http);
        await c.ListToolsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, n);
        Assert.Equal("s2", http.Requests[^1].Headers.GetValues("Mcp-Session-Id").Single());
    }

    [Fact]
    public async Task ExpiredSessionTwiceIsAnError()
    {
        var n = 0;
        var http = new FakeHttp();
        http.Add(Url, r =>
        {
            var b = Body(r);
            switch (b["method"]!.GetValue<string>())
            {
                case "initialize":
                    var resp = Json(Reply(b, "{}"));
                    resp.Headers.Add("Mcp-Session-Id", "s" + (++n));
                    return resp;
                case "notifications/initialized": return new HttpResponseMessage(HttpStatusCode.Accepted);
                default: return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        });
        await using var c = Client(http);
        await Assert.ThrowsAsync<McpException>(() => c.ListToolsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, n);
    }

    [Fact]
    public async Task HugeStreamIsCut()
    {
        var http = new FakeHttp();
        http.AddGenerated(Url, 5 * 1024 * 1024, "text/event-stream");
        await using var c = Client(http);
        // the generated route answers every POST, so initialize itself meets the 5 MB stream
        var e = await Assert.ThrowsAsync<McpException>(() => c.ListToolsAsync(TestContext.Current.CancellationToken));
        Assert.Contains("4 MB", e.Message);
        Assert.True(http.BytesServed < 5 * 1024 * 1024);
    }

    [Fact]
    public async Task UnauthorizedCarriesWwwAuthenticate()
    {
        var http = new FakeHttp();
        http.Add(Url, _ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            r.Headers.TryAddWithoutValidation("WWW-Authenticate", "Bearer resource_metadata=\"https://mcp.example.test/.well-known/oauth-protected-resource\"");
            return r;
        });
        await using var c = Client(http);
        var e = await Assert.ThrowsAsync<McpUnauthorizedException>(() => c.ListToolsAsync(TestContext.Current.CancellationToken));
        Assert.Contains("resource_metadata", e.WwwAuthenticate);
        Assert.IsAssignableFrom<McpException>(e);
    }

    [Fact]
    public async Task RedirectIsAnError()
    {
        var http = new FakeHttp();
        http.Add(Url, _ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Redirect);
            r.Headers.Location = new Uri("https://elsewhere.example.test/mcp");
            return r;
        });
        await using var c = Client(http);
        var e = await Assert.ThrowsAsync<McpException>(() => c.ListToolsAsync(TestContext.Current.CancellationToken));
        Assert.Contains("redirected", e.Message);
    }

    [Fact]
    public async Task HeadersAreAskedPerRequest()
    {
        var http = Server((_, b) => Json(Reply(b, "{\"tools\":[]}")));
        var token = 0;
        await using var c = Client(http, _ => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { ["Authorization"] = "Bearer t" + (++token) }));
        await c.ListToolsAsync(TestContext.Current.CancellationToken);
        var first = http.Requests[^1].Headers.GetValues("Authorization").Single();
        await c.ListToolsAsync(TestContext.Current.CancellationToken);
        var second = http.Requests[^1].Headers.GetValues("Authorization").Single();
        Assert.NotEqual(first, second);
        Assert.StartsWith("Bearer t", second);
    }

    [Fact]
    public async Task ServerErrorMessageIsRedactedAndLastErrorKept()
    {
        var http = Server((_, b) => Json($"{{\"jsonrpc\":\"2.0\",\"id\":{b["id"]},\"error\":{{\"message\":\"bad token sk-abcdefghijklmnopqrstuvwxyz123456\"}}}}"));
        await using var c = Client(http);
        var e = await Assert.ThrowsAsync<McpException>(() => c.ListToolsAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz123456", e.Message);
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz123456", c.LastError());
        Assert.NotEqual("no message", c.LastError());
    }

    private sealed class FuncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> f) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var r = await f(request, cancellationToken);
            r.RequestMessage ??= request;
            return r;
        }
    }

    private static Func<CancellationToken, Task<IReadOnlyDictionary<string, string>>> Hdr(string name, string value)
        => _ => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { [name] = value });

    [Fact]
    public async Task RedirectFollowedByTheHandlerIsAnError()
    {
        var inner = Server((_, b) => Json(Reply(b, "{\"tools\":[]}")));
        var handler = new FuncHandler(async (r, ct) =>
        {
            var resp = await new HttpMessageInvoker(inner, false).SendAsync(r, ct);
            resp.RequestMessage = new HttpRequestMessage(HttpMethod.Post, "https://elsewhere.example.test/mcp"); // as if a redirect had been followed
            return resp;
        });
        await using var c = Client(handler);
        var e = await Assert.ThrowsAsync<McpException>(() => c.ListToolsAsync(TestContext.Current.CancellationToken));
        Assert.Contains("redirected", e.Message);
    }

    [Theory]
    [InlineData("X-Key", "a\r\nX-Evil: 1")]
    [InlineData("X-Key\r\nX-Evil", "v")]
    [InlineData("Accept", "text/html")]
    [InlineData("mcp-session-id", "x")]
    [InlineData("Content-Type", "text/plain")]
    [InlineData("Host", "evil.test")]
    [InlineData("Content-Length", "1")]
    [InlineData("Content-Language", "v")]
    public async Task BadHeadersAreRefusedBeforeSending(string name, string value)
    {
        var http = Server((_, b) => Json(Reply(b, "{\"tools\":[]}")));
        await using var c = Client(http, Hdr(name, value));
        var e = await Assert.ThrowsAsync<McpException>(() => c.ListToolsAsync(TestContext.Current.CancellationToken));
        Assert.Empty(http.Requests);
        Assert.DoesNotContain("X-Evil: 1", e.Message);
        Assert.DoesNotContain("evil.test", e.Message);
    }

    [Fact]
    public async Task FailedInitializedNotificationReinitializesNextTime()
    {
        var inits = 0;
        var http = new FakeHttp();
        http.Add(Url, r =>
        {
            var b = Body(r);
            switch (b["method"]!.GetValue<string>())
            {
                case "initialize":
                    inits++;
                    var resp = Json(Reply(b, "{}"));
                    resp.Headers.Add("Mcp-Session-Id", "s" + inits);
                    return resp;
                case "notifications/initialized": return new HttpResponseMessage(inits == 1 ? HttpStatusCode.InternalServerError : HttpStatusCode.Accepted);
                default: return Json(Reply(b, "{\"tools\":[]}"));
            }
        });
        await using var c = Client(http);
        await Assert.ThrowsAsync<McpException>(() => c.ListToolsAsync(TestContext.Current.CancellationToken));
        await c.ListToolsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, inits);
        Assert.Equal("s2", http.Requests[^1].Headers.GetValues("Mcp-Session-Id").Single());
    }

    [Fact]
    public async Task ReasonPhraseIsRedacted()
    {
        var http = new FakeHttp();
        http.Add(Url, _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { ReasonPhrase = "oops sk-abcdefghijklmnopqrstuvwxyz123456" });
        await using var c = Client(http);
        var e = await Assert.ThrowsAsync<McpException>(() => c.ListToolsAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz123456", e.Message);
    }

    [Fact]
    public async Task ServerNameInLogIsOneLineAndClipped()
    {
        var logged = new List<string>();
        var http = Server((_, b) => Json(Reply(b, "{\"tools\":[]}")), initResult: "{\"serverInfo\":{\"name\":\"a\\nFORGED " + new string('x', 500) + "\"}}");
        await using var c = new McpHttpClient(new Uri(Url), _ => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>()), http, logged.Add);
        await c.ListToolsAsync(TestContext.Current.CancellationToken);
        var line = Assert.Single(logged);
        Assert.DoesNotContain('\n', line);
        Assert.True(line.Length < 200);
    }

    [Theory]
    [InlineData("{\"isError\":\"yes\"}")]
    [InlineData("{\"content\":\"text\"}")]
    [InlineData("{\"isError\":1}")]
    public async Task MalformedToolResultIsAnMcpException(string result)
    {
        var http = Server((_, b) => Json(Reply(b, result)));
        await using var c = Client(http);
        await Assert.ThrowsAsync<McpException>(() => c.CallToolAsync("t", new JsonObject(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NonStringTextAndTypeDoNotCrash()
    {
        var http = Server((_, b) => Json(Reply(b, "{\"content\":[{\"type\":5},{\"type\":\"text\",\"text\":7},\"x\"]}")));
        await using var c = Client(http);
        var (text, err) = await c.CallToolAsync("t", new JsonObject(), TestContext.Current.CancellationToken);
        Assert.Equal("[content]\n", text);
        Assert.False(err);
    }

    [Theory]
    [InlineData("\"result\":\"nope\"")]
    [InlineData("\"result\":[1]")]
    [InlineData("\"error\":{\"message\":42}")]
    [InlineData("\"error\":\"bad\"")]
    public async Task MalformedEnvelopeIsAnMcpException(string part)
    {
        var http = Server((_, b) => Json($"{{\"jsonrpc\":\"2.0\",\"id\":{b["id"]},{part}}}"));
        await using var c = Client(http);
        await Assert.ThrowsAsync<McpException>(() => c.ListToolsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ToolsThatAreNotAListIsAnMcpException()
    {
        var http = Server((_, b) => Json(Reply(b, "{\"tools\":\"x\"}")));
        await using var c = Client(http);
        await Assert.ThrowsAsync<McpException>(() => c.ListToolsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SessionIdFromAFailedInitializeIsIgnored()
    {
        var inits = 0;
        var http = new FakeHttp();
        http.Add(Url, r =>
        {
            var b = Body(r);
            switch (b["method"]!.GetValue<string>())
            {
                case "initialize":
                    inits++;
                    var resp = inits == 1 ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : Json(Reply(b, "{}"));
                    if (inits == 1) resp.Headers.Add("Mcp-Session-Id", "bad");
                    return resp;
                case "notifications/initialized": return new HttpResponseMessage(HttpStatusCode.Accepted);
                default: return Json(Reply(b, "{\"tools\":[]}"));
            }
        });
        await using var c = Client(http);
        await Assert.ThrowsAsync<McpException>(() => c.ListToolsAsync(TestContext.Current.CancellationToken));
        await c.ListToolsAsync(TestContext.Current.CancellationToken);
        Assert.All(http.Requests, r => Assert.False(r.Headers.Contains("Mcp-Session-Id")));
    }

    [Fact]
    public async Task NegotiatedProtocolVersionIsSent()
    {
        var http = Server((_, b) => Json(Reply(b, "{\"tools\":[]}")), initResult: "{\"protocolVersion\":\"2025-03-26\"}");
        await using var c = Client(http);
        await c.ListToolsAsync(TestContext.Current.CancellationToken);
        Assert.Equal("2025-03-26", http.Requests[^1].Headers.GetValues("MCP-Protocol-Version").Single());
    }

    [Fact]
    public async Task UnusableProtocolVersionFallsBack()
    {
        var http = Server((_, b) => Json(Reply(b, "{\"tools\":[]}")), initResult: "{\"protocolVersion\":\"a b\"}");
        await using var c = Client(http);
        await c.ListToolsAsync(TestContext.Current.CancellationToken);
        Assert.Equal("2025-06-18", http.Requests[^1].Headers.GetValues("MCP-Protocol-Version").Single());
    }

    [Fact]
    public async Task TwoCallsRunAtOnce()
    {
        var firstIn = new TaskCompletionSource();
        var secondIn = new TaskCompletionSource();
        var arrivals = 0;
        var handler = new FuncHandler(async (r, ct) =>
        {
            var b = Body(r);
            switch (b["method"]!.GetValue<string>())
            {
                case "initialize": return Json(Reply(b, "{}"));
                case "notifications/initialized": return new HttpResponseMessage(HttpStatusCode.Accepted);
                default:
                    if (Interlocked.Increment(ref arrivals) == 1)
                    {
                        firstIn.SetResult();
                        await secondIn.Task.WaitAsync(TimeSpan.FromSeconds(10), ct); // held until the second call arrives
                    }
                    else secondIn.SetResult();
                    return Json(Reply(b, "{\"tools\":[]}"));
            }
        });
        await using var c = Client(handler);
        var one = c.ListToolsAsync(TestContext.Current.CancellationToken);
        await firstIn.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var two = c.ListToolsAsync(TestContext.Current.CancellationToken);
        await Task.WhenAll(one, two).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DisposeStopsInFlightCallsAndLaterCallsAreRefused()
    {
        var inFlight = new TaskCompletionSource();
        var handler = new FuncHandler(async (r, ct) =>
        {
            var b = Body(r);
            switch (b["method"]!.GetValue<string>())
            {
                case "initialize": return Json(Reply(b, "{}"));
                case "notifications/initialized": return new HttpResponseMessage(HttpStatusCode.Accepted);
                default:
                    inFlight.SetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                    return Json("{}");
            }
        });
        var c = Client(handler);
        var call = c.ListToolsAsync(TestContext.Current.CancellationToken);
        await inFlight.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await c.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => call);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => c.ListToolsAsync(TestContext.Current.CancellationToken));
        await c.DisposeAsync(); // twice is fine
    }

    [Fact]
    public async Task StreamEndingWithoutTheReplyIsAnError()
    {
        var http = Server((_, b) => Json("data: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\"}\n\n", type: "text/event-stream"));
        await using var c = Client(http);
        var e = await Assert.ThrowsAsync<McpException>(() => c.ListToolsAsync(TestContext.Current.CancellationToken));
        Assert.Contains("without a reply", e.Message);
    }

    [Fact]
    public async Task MultiLineDataEventIsJoined()
    {
        var http = Server((_, b) => Json($"data: {{\"jsonrpc\":\"2.0\",\"id\":{b["id"]},\ndata: \"result\":{{\"tools\":[{{\"name\":\"m\"}}]}}}}\n\n", type: "text/event-stream"));
        await using var c = Client(http);
        var tools = await c.ListToolsAsync(TestContext.Current.CancellationToken);
        Assert.Equal("m", tools[0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task ServerInfoThatIsNotAnObjectIsTolerated()
    {
        var http = Server((_, b) => Json(Reply(b, "{\"tools\":[]}")), initResult: "{\"serverInfo\":\"just a string\"}");
        await using var c = Client(http);
        Assert.Empty(await c.ListToolsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InitializedNotificationRefusedWithSessionIsAnMcpException()
    {
        var http = new FakeHttp();
        http.Add(Url, r =>
        {
            var b = Body(r);
            if (b["method"]!.GetValue<string>() == "initialize")
            {
                var resp = Json(Reply(b, "{}"));
                resp.Headers.Add("Mcp-Session-Id", "s1");
                return resp;
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        await using var c = Client(http);
        var e = await Assert.ThrowsAsync<McpException>(() => c.ListToolsAsync(TestContext.Current.CancellationToken));
        Assert.Contains("session", e.Message);
        Assert.Equal(e.Message, c.LastError());
    }
}
