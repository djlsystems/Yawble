using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Harness.Pty;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harness.Tests;

/// <summary>
/// MCP 2's bed: the real Host with a FAKE WEB ADDRESS MCP SERVER answering through the Host's own HTTP handlers
/// (MCP 1's check on the worker and MCP 2's calls from the Host alike), so nothing reaches the network. Team
/// Alpha is given the server <see cref="Server"/>; Beta is given nothing. A member's run is a
/// <see cref="FakeAgent"/> that calls <c>/mcp</c> with the member's own key, as an agent CLI does.
/// </summary>
public sealed class McpServerToolsBed : IAsyncDisposable
{
    public const string Email = "person@example.test";
    public const string PersonPassword = "correct horse battery";
    public const string Token = "fake-mcp-token-5d1e77";
    public const string QueryKey = "fake-query-key-a1b2c3";
    public const string HostName = "tools.example.test";
    public const string Server = "fake";
    public static readonly string Url = $"https://{HostName}/mcp?key={QueryKey}";

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly ConcurrentDictionary<string, Func<AgentInvocation, Task>> _scripts = new();

    public string DataRoot { get; } = Path.Combine(Path.GetTempPath(), $"harness-test-mcp2-{Guid.NewGuid():N}");

    public FakeToolServer Upstream { get; } = new(Token, QueryKey);

    public FakeAgent Agents { get; } = new();

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public HttpClient Person { get; private set; } = null!;

    public IServiceProvider Services => Factory.Services;

    public string ServerId { get; private set; } = "";

    /// <summary>The person's user id, whose Concierge a test starts.</summary>
    public string PersonId { get; private set; } = "";

    public static async Task<McpServerToolsBed> StartAsync(
        TimeSpan? callTimeout = null, long maximumBinary = McpRunTools.DefaultMaximumBinary, bool assign = true,
        IReadOnlyList<string>? memberAgents = null, AgentLaunchUser? runAs = null, WorkerAsks? asks = null, IPtyEngine? terminals = null,
        McpStdioServers? stdio = null)
    {
        var bed = new McpServerToolsBed();
        await bed.InitializeAsync(
            callTimeout ?? TimeSpan.FromSeconds(10), maximumBinary, assign, memberAgents ?? ["claude-headless"], runAs, asks, terminals, stdio);
        return bed;
    }

    private async Task InitializeAsync(
        TimeSpan callTimeout, long maximumBinary, bool assign, IReadOnlyList<string> memberAgents, AgentLaunchUser? runAs, WorkerAsks? asks,
        IPtyEngine? terminals, McpStdioServers? stdio)
    {
        Directory.CreateDirectory(DataRoot);
        Agents.Behaviour = async invocation =>
        {
            foreach (var (marker, script) in _scripts)
            {
                if (invocation.Prompt.Contains(marker, StringComparison.Ordinal))
                {
                    await script(invocation);
                    break;
                }
            }

            return new AgentResult(0, "done");
        };

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", DataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentRunner>(Agents);
                if (terminals is not null) services.AddSingleton(terminals);
                services.AddSingleton(new ConnectionTester(http: Upstream, timeout: TimeSpan.FromSeconds(5)));
                services.AddSingleton(new McpForwarding(Upstream, callTimeout, TimeSpan.FromSeconds(5)));
                services.AddSingleton(sp => new McpRunTools(
                    sp.GetRequiredService<McpServers>(), sp.GetRequiredService<McpForwarding>(), sp.GetRequiredService<TimeProvider>(),
                    sp.GetRequiredService<ILogger<McpRunTools>>(), maximumBinary, runAs: runAs, asks: asks,
                    users: sp.GetRequiredService<IUserStore>(), teams: sp.GetRequiredService<TeamRegistry>,
                    stdio: sp.GetRequiredService<McpStdio>()));
                // THE SERVERS THE HOST'S OWN WORKER RUNS: a test's, pointed at a fake stdio server.
                if (stdio is not null) services.AddSingleton(stdio);
            }));

        PersonId = (await Services.GetRequiredService<IUserStore>().CreateAsync(Email, PersonPassword)).Id;
        Person = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await Person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = PersonPassword }, Ct)).EnsureSuccessStatusCode();

        var registry = Services.GetRequiredService<TeamRegistry>();
        await registry.CreateAsync("Alpha", "claude-headless", memberAgent: "claude-headless", ct: Ct);
        await registry.CreateAsync("Beta", "claude-headless", memberAgent: "claude-headless", ct: Ct);
        foreach (var team in new[] { "Alpha", "Beta" })
        {
            foreach (var agent in memberAgents)
            {
                var hired = await Person.PostAsJsonAsync($"/api/teams/{team}/containers", new { name = MemberName(agent), agent }, Ct);
                Assert.True(hired.IsSuccessStatusCode, await hired.Content.ReadAsStringAsync(Ct));
            }
        }

        var key = await PostAsync("/api/connections/credentials", new
        {
            kind = "apikey", preset = "other", name = "Fake key", fields = new[] { new { name = "token", value = Token, masked = true } },
        });
        var connection = key.GetProperty("connection").GetProperty("id").GetString()!;

        var saved = await PostAsync("/api/mcp-servers", new { name = Server, kind = "url", url = Url, connection });
        Assert.Equal("ok", saved.GetProperty("check").GetProperty("outcome").GetString());
        ServerId = saved.GetProperty("server").GetProperty("id").GetString()!;

        if (assign) await PostAsync("/api/teams/Alpha/mcp-servers", new { server = ServerId });
    }

    /// <summary>The member each agent preset is hired as: <c>Dev</c> for claude, <c>Dev-codex</c> and so on.</summary>
    public static string MemberName(string agent) => agent == "claude-headless" ? "Dev" : "Dev-" + agent.Replace("-headless", "", StringComparison.Ordinal);

    public async Task<JsonElement> PostAsync(string path, object body)
    {
        var response = await Person.PostAsJsonAsync(path, body, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>
    /// Runs <paramref name="member"/> once, by a person's instruction, with <paramref name="script"/> as what
    /// the agent does during the run. Answers when the script is done.
    /// </summary>
    public async Task RunAsync(string team, string member, Func<AgentInvocation, Task> script)
    {
        var marker = $"mcp-run-{Guid.NewGuid():N}";
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _scripts[marker] = async invocation =>
        {
            try
            {
                await script(invocation);
                done.TrySetResult();
            }
            catch (Exception exception)
            {
                done.TrySetException(exception);
            }
        };

        var told = await Person.PostAsJsonAsync($"/api/teams/{team}/containers/{member}/tell", new { instruction = marker }, Ct);
        Assert.True(told.IsSuccessStatusCode, await told.Content.ReadAsStringAsync(Ct));

        await done.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        _scripts.TryRemove(marker, out _);
        _ran.Add(new ContainerId(team, member));
    }

    private readonly List<ContainerId> _ran = [];

    /// <summary>A JSON-RPC client of this Host's <c>/mcp</c> with <paramref name="key"/>, as a CLI calls it.</summary>
    public HarnessMcp Mcp(string key, string path = "/mcp") => new(Factory.CreateClient(), key, path);

    public static string Key(AgentInvocation invocation) => invocation.Environment["HARNESS_KEY"];

    /// <summary>Every row of the message log, as text, for a test that looks for something that must not be there.</summary>
    public async Task<IReadOnlyList<string>> LogTextAsync()
    {
        var rows = new List<string>();
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(DataRoot, "messages.db")};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT type || ' ' || payload FROM messages ORDER BY seq";
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct)) rows.Add(reader.GetString(0));
        return rows;
    }

    public async ValueTask DisposeAsync()
    {
        // EVERY RUN ENDED BEFORE THE HOST GOES, so its end-of-run steps do not meet a disposed provider.
        foreach (var member in _ran.Distinct()) await McpServerToolsTests.WaitIdleAsync(this, member);

        Person.Dispose();
        await Factory.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(DataRoot, recursive: true); }
        catch (IOException) { }
    }
}

/// <summary>A bare JSON-RPC client of the harness server: <c>tools/list</c> and <c>tools/call</c>.</summary>
public sealed class HarnessMcp(HttpClient http, string key, string path) : IDisposable
{
    private int _id;

    /// <summary>The tools listed, page by page, as JSON objects.</summary>
    public async Task<IReadOnlyList<JsonObject>> ListAsync()
    {
        var tools = new List<JsonObject>();
        string? cursor = null;
        do
        {
            var result = await SendAsync("tools/list", cursor is null ? new JsonObject() : new JsonObject { ["cursor"] = cursor });
            tools.AddRange(((JsonArray)result["result"]!["tools"]!).OfType<JsonObject>());
            cursor = result["result"]!["nextCursor"]?.GetValue<string>();
        }
        while (cursor is not null);

        return tools;
    }

    /// <summary>A call's whole JSON-RPC answer: <c>result</c>, or <c>error</c> for an unknown tool.</summary>
    public Task<JsonObject> CallAsync(string name, JsonObject? arguments = null) =>
        SendAsync("tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments ?? new JsonObject() });

    /// <summary>The text of every content block of a call's result, one per line.</summary>
    public static string Text(JsonObject answer) =>
        string.Join("\n", ((JsonArray)answer["result"]!["content"]!).Select(b => b?["text"]?.GetValue<string>() ?? ""));

    public static bool IsError(JsonObject answer) => answer["result"]?["isError"]?.GetValue<bool>() == true;

    private async Task<JsonObject> SendAsync(string method, JsonObject parameters)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(
                new JsonObject { ["jsonrpc"] = "2.0", ["id"] = Interlocked.Increment(ref _id), ["method"] = method, ["params"] = parameters }.ToJsonString(),
                Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(ApiKeyAuthenticationHandler.Header, key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await http.SendAsync(request, McpServerToolsBed.Ct);
        var text = await response.Content.ReadAsStringAsync(McpServerToolsBed.Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);

        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            text = string.Join("\n", text.Split('\n').Where(l => l.StartsWith("data:", StringComparison.Ordinal)).Select(l => l[5..].Trim()).Last());
        }

        return (JsonObject)JsonNode.Parse(text)!;
    }

    public void Dispose() => http.Dispose();
}

/// <summary>
/// A fake MCP server at a web address. Its tools: <c>echo_credential</c> answers with what it was sent, the
/// credential included; <c>draw</c> answers a PNG and a line of text; <c>record</c> answers an audio clip and an
/// embedded PDF; <c>wait</c> never answers; <c>gone</c> is taken away when <see cref="Removed"/> is set;
/// <c>long_text</c> answers <see cref="LongText"/> characters; <see cref="LongName"/> is a tool whose name on the
/// harness server would be 70 characters. Every request needs the token and the URL's key.
/// </summary>
public sealed class FakeToolServer(string token, string queryKey) : HttpMessageHandler
{
    /// <summary>65 characters: with <c>fake_</c> in front, 70.</summary>
    public const string LongName = "summarise_every_open_pull_request_in_the_repository_with_reviews1";

    public const string Description = "Echoes what it was sent. Use it to see the request.";

    public static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Enumerable.Range(0, 600).Select(i => (byte)(i * 7))];

    public static readonly byte[] Audio = [.. Enumerable.Range(0, 900).Select(i => (byte)(i * 3))];

    public static readonly byte[] Pdf = [.. "%PDF-1.4 fake"u8.ToArray(), .. Enumerable.Range(0, 300).Select(i => (byte)i)];

    public static JsonObject EchoSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["message"] = new JsonObject { ["type"] = "string", ["description"] = "What to echo." } },
        ["required"] = new JsonArray("message"),
    };

    public int LongText { get; set; } = 150_000;

    public bool Removed { get; set; }

    /// <summary>Tools listed besides the usual ones, for a clash at save.</summary>
    public List<string> Extra { get; } = [];

    public ConcurrentQueue<string> Called { get; } = new();

    private IEnumerable<string> Names()
    {
        string[] names = ["echo_credential", "draw", "record", "wait", "long_text", LongName];
        return Removed ? [.. names, .. Extra] : [.. names, "gone", .. Extra];
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.Host != McpServerToolsBed.HostName) throw new HttpRequestException($"Connection refused ({request.RequestUri.Host})");

        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
        var method = body["method"]!.GetValue<string>();
        var authorization = request.Headers.Authorization?.ToString();

        if (authorization != $"Bearer {token}" || request.RequestUri.Query != $"?key={queryKey}")
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"error":"bad token"}""") };
        }

        if (body["id"] is null) return new HttpResponseMessage(HttpStatusCode.Accepted);

        JsonObject? result = null;
        JsonObject? error = null;
        switch (method)
        {
            case "initialize":
                result = new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(), ["serverInfo"] = new JsonObject { ["name"] = "fake" } };
                break;
            case "tools/list":
                result = new JsonObject
                {
                    ["tools"] = new JsonArray([.. Names().Select(n => (JsonNode)new JsonObject
                    {
                        ["name"] = n,
                        ["description"] = n == "echo_credential" ? Description : $"The fake {n} tool.",
                        ["inputSchema"] = n == "echo_credential" ? EchoSchema() : new JsonObject { ["type"] = "object" },
                    })]),
                };
                break;
            case "tools/call":
                var name = body["params"]!["name"]!.GetValue<string>();
                Called.Enqueue(name);
                (result, error) = await CallAsync(name, body["params"]?["arguments"] as JsonObject, authorization, cancellationToken);
                break;
            default:
                error = new JsonObject { ["code"] = -32601, ["message"] = "Method not found" };
                break;
        }

        var answer = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = body["id"]!.DeepClone() };
        if (error is not null) answer["error"] = error;
        else answer["result"] = result;

        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer.ToJsonString(), Encoding.UTF8, "application/json") };
        if (method == "initialize") response.Headers.Add("Mcp-Session-Id", "fake-session");
        return response;
    }

    private async Task<(JsonObject?, JsonObject?)> CallAsync(string name, JsonObject? arguments, string? authorization, CancellationToken ct)
    {
        static JsonObject Text(string text) => new() { ["type"] = "text", ["text"] = text };

        switch (name)
        {
            case "echo_credential":
                return (new JsonObject
                {
                    ["content"] = new JsonArray(Text($"you said {arguments?["message"]}; you sent '{authorization}' to ?key={queryKey}")),
                    ["structuredContent"] = new JsonObject { ["authorization"] = authorization, ["token"] = token.ToUpperInvariant() },
                }, null);
            case "draw":
                return (new JsonObject
                {
                    ["content"] = new JsonArray(
                        new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(Png), ["mimeType"] = "image/png" },
                        Text("a picture")),
                }, null);
            case "record":
                return (new JsonObject
                {
                    ["content"] = new JsonArray(
                        new JsonObject { ["type"] = "audio", ["data"] = Convert.ToBase64String(Audio), ["mimeType"] = "audio/wav" },
                        new JsonObject
                        {
                            ["type"] = "resource",
                            ["resource"] = new JsonObject { ["uri"] = "file:///report.pdf", ["mimeType"] = "application/pdf", ["blob"] = Convert.ToBase64String(Pdf) },
                        }),
                }, null);
            case "wait":
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return (null, null);
            case "long_text":
                return (new JsonObject { ["content"] = new JsonArray(Text(new string('x', LongText))) }, null);
            case "gone" when !Removed:
            case LongName:
                return (new JsonObject { ["content"] = new JsonArray(Text($"{name} ran")) }, null);
            default:
                return (null, new JsonObject { ["code"] = -32602, ["message"] = $"Unknown tool: {name}" });
        }
    }
}
