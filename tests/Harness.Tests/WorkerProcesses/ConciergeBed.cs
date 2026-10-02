using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Harness.Host;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// What the Concierge and live-view process tests add to a <see cref="ProcessBed"/>: a fake interactive
/// CLI the Concierge runs, a fake headless CLI that writes a live transcript while it runs, a browser's
/// socket to the Concierge, and reads of what a test checks. The fake terminal records its PID (as
/// <c>concierge-*.process</c>, never as a run's <c>.pid</c>) and its parent chain; a test stops it, by
/// that PID only, with <see cref="StopTerminals"/>.
/// </summary>
internal static class ConciergeBed
{
    /// <summary>The interactive preset the Concierge runs in these tests.</summary>
    public const string Terminal = "fake-tty";

    /// <summary>The headless preset whose runs write a live transcript.</summary>
    public const string Live = "fake-live";

    /// <summary>Writes the fakes and a catalog of the bed's own fake, the fake terminal and the live fake. Call before control starts.</summary>
    public static void AddFakes(this ProcessBed bed)
    {
        var terminal = Path.Combine(bed.Work, "terminal.sh");
        File.WriteAllText(terminal, TerminalScript(bed));
        var live = Path.Combine(bed.Work, "live.sh");
        File.WriteAllText(live, LiveScript(bed));
        Directory.CreateDirectory(Home(bed));

        AgentCatalogFile.Save(bed.Root,
        [
            new AgentDefinition("fake", AgentMode.Headless, new AgentLaunch("sh", [bed.Fake], UsageFormat: "claude-json", LanguageModel: false)),
            new AgentDefinition(Terminal, AgentMode.Interactive, new AgentLaunch("bash", [terminal], LanguageModel: false)),
            new AgentDefinition(
                Live, AgentMode.Headless, new AgentLaunch("sh", [live, "{sessionId}"], UsageFormat: "claude-json", LanguageModel: false),
                LiveView: new AgentLiveView("~/live/{sessionId}.jsonl", LiveView.ClaudeJsonl)),
        ]);
    }

    /// <summary>The HOME the bed's workers give their children: the live transcripts are under it.</summary>
    public static string Home(ProcessBed bed) => Path.Combine(bed.Work, "home");

    /// <summary>A worker whose children's HOME is the bed's, with more environment.</summary>
    public static HostProcess StartFakeWorker(this ProcessBed bed, string id, IReadOnlyDictionary<string, string>? more = null)
    {
        var environment = new Dictionary<string, string> { ["HOME"] = Home(bed) };
        foreach (var (name, value) in more ?? new Dictionary<string, string>()) environment[name] = value;
        return bed.StartWorker(id, more: environment);
    }

    public static async Task UntilWorkerAsync(this ProcessBed bed, string id, string state) =>
        await bed.UntilAsync($"{id} is {state}", async () =>
            (await bed.WorkersAsync()).Any(w => w.GetProperty("id").GetString() == id && w.GetProperty("state").GetString() == state));

    /// <summary>The Concierge runs the fake terminal.</summary>
    public static async Task UseFakeConciergeAsync(this ProcessBed bed) =>
        (await bed.Person.PutAsJsonAsync("/api/concierge", new { agent = Terminal })).EnsureSuccessStatusCode();

    /// <summary>A browser's socket to the person's Concierge, signed in as the bed's person.</summary>
    public static async Task<Browser> OpenConciergeAsync(this ProcessBed bed, int cols = 100, int rows = 30)
    {
        var cookies = new CookieContainer();
        using (var login = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = bed.Url })
        {
            (await login.PostAsJsonAsync("/api/auth/login", new { email = "person@example.test", password = "correct horse battery" }))
                .EnsureSuccessStatusCode();
        }

        var socket = new ClientWebSocket();
        socket.Options.Cookies = cookies;
        var uri = new UriBuilder(bed.Url) { Scheme = "ws", Path = "/api/concierge/ws", Query = $"cols={cols}&rows={rows}" }.Uri;
        await socket.ConnectAsync(uri, CancellationToken.None);
        return new Browser(socket);
    }

    /// <summary>The fake terminal's PID and parent chain, once it has started.</summary>
    public static (int Pid, IReadOnlyList<int> Chain)? TerminalProcess(this ProcessBed bed)
    {
        var file = Directory.GetFiles(bed.Out, "concierge-*.process").OrderBy(File.GetLastWriteTimeUtc).LastOrDefault();
        if (file is null) return null;

        var lines = File.ReadAllLines(file).Where(l => l.Length > 0).Select(int.Parse).ToList();
        return lines.Count == 0 ? null : (lines[0], lines);
    }

    /// <summary>Stops every fake terminal this bed started that is still alive, by its recorded PID, after checking it is that script.</summary>
    public static void StopTerminals(this ProcessBed bed)
    {
        foreach (var file in Directory.GetFiles(bed.Out, "concierge-*.process"))
        {
            if (File.ReadLines(file).FirstOrDefault() is not { } first || !int.TryParse(first, out var pid)) continue;
            if (!ProcessBed.Alive(pid)) continue;

            try
            {
                var command = File.ReadAllText($"/proc/{pid}/cmdline");
                if (command.Contains("terminal.sh", StringComparison.Ordinal)) Signal(pid, "KILL");
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>The heavy lease's holders and queue as the capacity sample read them, by member (the Concierge has none).</summary>
    public static async Task<(List<string?> Holding, List<string?> Queued)> HeavyAsync(this ProcessBed bed)
    {
        var capacity = await bed.GetAsync("/api/capacity");
        if (capacity.GetProperty("latest") is not { ValueKind: JsonValueKind.Object } latest
            || latest.GetProperty("heavyLease") is not { ValueKind: JsonValueKind.Object } lease)
        {
            return ([], []);
        }

        static List<string?> Names(JsonElement parties) =>
            [.. parties.EnumerateArray().Select(p => p.TryGetProperty("member", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null)];

        return (Names(lease.GetProperty("holding")), Names(lease.GetProperty("queued")));
    }

    /// <summary>How many Concierge credentials exist, read from control's own store.</summary>
    public static async Task<long> ConciergePrincipalsAsync(this ProcessBed bed)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(bed.Root, "messages.db")};Mode=ReadOnly;Pooling=false");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM principals WHERE id LIKE 'concierge-%'";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>A browser's end of the Concierge's socket: what it typed and what it was sent.</summary>
    public sealed class Browser(ClientWebSocket socket) : IAsyncDisposable
    {
        private readonly StringBuilder _seen = new();
        private readonly Lock _gate = new();
        private Task? _reading;

        /// <summary>Everything the terminal sent, as text.</summary>
        public string Seen
        {
            get
            {
                lock (_gate) return _seen.ToString();
            }
        }

        /// <summary>The first binary frame, whole, before reading the rest in the background.</summary>
        public async Task<string> FirstFrameAsync()
        {
            var text = await ReceiveAsync() ?? "";
            lock (_gate) _seen.Append(text);
            return text;
        }

        public bool Closed { get; private set; }

        /// <summary>What control's close said: <c>exit N</c> when the CLI exited.</summary>
        public string? CloseReason { get; private set; }

        public void Read() => _reading ??= Task.Run(async () =>
        {
            while (await ReceiveAsync() is { } text)
            {
                lock (_gate) _seen.Append(text);
            }

            Closed = true;
        });

        public Task TypeAsync(string text) =>
            socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Binary, true, CancellationToken.None);

        public Task ResizeAsync(int cols, int rows) =>
            socket.SendAsync(
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "resize", cols, rows })),
                WebSocketMessageType.Text, true, CancellationToken.None);

        private async Task<string?> ReceiveAsync()
        {
            var buffer = new byte[64 * 1024];
            using var message = new MemoryStream();
            try
            {
                while (true)
                {
                    var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        CloseReason = result.CloseStatusDescription;
                        return null;
                    }
                    message.Write(buffer, 0, result.Count);
                    if (result.EndOfMessage) return Encoding.UTF8.GetString(message.ToArray());
                }
            }
            catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException or OperationCanceledException)
            {
                return null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", bounded.Token);
                }
            }
            catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
            {
            }

            socket.Dispose();
        }
    }

    /// <summary>Sends <paramref name="signal"/> to the process with the PID this bed recorded for it.</summary>
    public static void Signal(int pid, string signal)
    {
        using var kill = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("kill", ["-" + signal, pid.ToString()]) { UseShellExecute = false })!;
        kill.WaitForExit(10_000);
    }

    private static string TerminalScript(ProcessBed bed) =>
        $$$"""
        #!/bin/bash
        out='{{{bed.Out}}}'
        file="$out/concierge-$$.process"
        p=$$; : > "$file.tmp"
        while [ -n "$p" ] && [ "$p" -gt 1 ]; do echo "$p" >> "$file.tmp"; p=$(sed 's/.*) //' /proc/$p/stat 2>/dev/null | cut -d' ' -f2); done
        mv "$file.tmp" "$file"
        echo "READY $HARNESS_URL"
        while IFS= read -r -t 600 line; do
          line="${line%$'\r'}"
          case "$line" in
            lease) echo "LEASE:$(curl -s -X POST -H "X-Api-Key: $HARNESS_KEY" -H 'Content-Type: application/json' -d '{"action":"acquire","name":"heavy"}' "$HARNESS_URL/api/me/lease")";;
            size) echo "SIZE:$(stty size)";;
            bye) exit 0;;
            *) echo "GOT:$line";;
          esac
        done
        """;

    private static string LiveScript(ProcessBed bed) =>
        $$$"""
        #!/bin/sh
        out='{{{bed.Out}}}'
        run="$HARNESS_MEMBER-$$"
        p=$$; : > "$out/$run.chain.tmp"
        while [ -n "$p" ] && [ "$p" -gt 1 ]; do echo "$p" >> "$out/$run.chain.tmp"; p=$(sed 's/.*) //' /proc/$p/stat 2>/dev/null | cut -d' ' -f2); done
        mv "$out/$run.chain.tmp" "$out/$run.chain"
        env > "$out/$run.env"
        mkdir -p "$HOME/live"
        transcript="$HOME/live/$1.jsonl"
        echo "$transcript" > "$out/$run.transcript"
        echo $$ > "$out/$run.pid"
        i=0
        while [ ! -e '{{{bed.GoFolder}}}'/"$HARNESS_MEMBER" ] && [ $i -lt 600 ]; do
          i=$((i+1))
          printf '{"type":"user","message":{"role":"user","content":"tick %s"}}\n' "$i" >> "$transcript"
          sleep 1
        done
        printf '{"type":"user","message":{"role":"user","content":"last"}}\n' >> "$transcript"
        touch "$out/$run.ended"
        printf '%s' '{"result":"done","usage":{"input_tokens":7,"output_tokens":11}}'
        """;
}
