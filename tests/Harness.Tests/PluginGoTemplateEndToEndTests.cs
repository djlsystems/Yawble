using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Pty;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// THE GO TEMPLATE, END TO END, ON THE REAL HOST: <c>templates/plugin-go</c> is built as
/// the two static binaries its manifest's <c>platforms</c> names (<c>GOARCH=amd64</c> and
/// <c>arm64</c>, <c>CGO_ENABLED=0</c>), installed under a data root as docs/plugins.md tells an
/// operator to, hired by a person through the ordinary member route, and told work through the
/// ordinary `tell` route - on whichever processor this Host runs on, as
/// <see cref="PluginMemberEndToEndTests"/> does for the .NET test plugin. Go is in the product image,
/// where the release runs this suite; without it the build fails, naming the missing command.
/// </summary>
public sealed class PluginGoTemplateEndToEndTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";
    private const string TokenKey = "SAMPLE_ECHO_GO_E2E_TOKEN";
    private const string Id = "sample-echo-go";

    /// <summary>Built once for the class: both binaries, into a folder each test copies from.</summary>
    private static readonly Lazy<string> Build = new(BuildBothBinaries, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-plugin-go-e2e-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;
    private string _team = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    private ContainerId Echo => new(_team, "EchoGo");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        Install(_dataRoot);
        Environment.SetEnvironmentVariable(TokenKey, "abcd1234");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(new FakeAgent())));

        _team = (await Services.GetRequiredService<TeamRegistry>()
            .CreateAsync("Go", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        _person = _factory.CreateClient();
        (await _person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();

        var hired = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name = "EchoGo",
            agent = "plugin:" + Id,
            config = new { mode = "reverse" },
            secrets = new { token = TokenKey },
        }, Ct);
        Assert.True(hired.StatusCode == HttpStatusCode.OK, await hired.Content.ReadAsStringAsync(Ct));
    }

    public async ValueTask DisposeAsync()
    {
        Environment.SetEnvironmentVariable(TokenKey, null);
        _person.Dispose();
        await _factory.DisposeAsync();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void Both_binaries_are_static_linux_executables_for_the_processor_the_manifest_names()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(TemplatePath(), "plugin.json")));
        var platforms = manifest.RootElement.GetProperty("platforms");
        Assert.False(manifest.RootElement.TryGetProperty("requires", out _), "a static Go binary needs no runtime from the image");

        foreach (var (platform, machine) in new[] { ("linux-x64", ElfMachineX64), ("linux-arm64", ElfMachineArm64) })
        {
            var binary = Path.Combine(Build.Value, platforms.GetProperty(platform).GetString()!);
            var (elfMachine, hasInterpreter) = ReadElf(binary);
            Assert.Equal(machine, elfMachine);
            Assert.False(hasInterpreter, $"{platform}: the binary asks for a dynamic loader; build it with CGO_ENABLED=0");
        }
    }

    [Fact]
    public async Task It_installs_and_is_listed_with_its_skill_and_event()
    {
        using var listed = JsonDocument.Parse(await _person.GetStringAsync("/api/plugins", Ct));

        Assert.Empty(listed.RootElement.GetProperty("refused").EnumerateArray());
        var plugin = Assert.Single(listed.RootElement.GetProperty("plugins").EnumerateArray(), p => p.GetProperty("id").GetString() == Id);
        Assert.Equal("0.1.0", plugin.GetProperty("version").GetString());
        Assert.Contains(plugin.GetProperty("publishes").EnumerateArray(), e => e.GetProperty("type").GetString() == $"plugin.{Id}.done");
        Assert.NotEmpty(plugin.GetProperty("skills").EnumerateArray());
    }

    [Fact]
    public async Task Work_told_to_it_runs_its_binary_for_this_processor_like_the_dotnet_template()
    {
        var row = await TellAndAwaitAsync("handback:shipped it");

        Assert.Equal(MessageTypes.Completed, row.Type);
        var payload = Payload(row);
        Assert.Equal("ti deppihs\ntoken length 8", payload.GetProperty("output").GetString());
        Assert.True(payload.GetProperty("handedBack").GetBoolean());
        Assert.DoesNotContain("abcd1234", row.Payload, StringComparison.Ordinal);

        // Its declared event, under its own id, carrying the output's length.
        var published = (await Services.GetRequiredService<IMessageLog>()
                .ReadAfterAsync(0, [$"plugin.{Id}.done"], int.MaxValue, Ct))
            .Single();
        Assert.Equal(Echo.ToString(), published.Source);
        Assert.Equal("ti deppihs\ntoken length 8".Length, Payload(published).GetProperty("length").GetInt32());

        // And a failure is a failed run, in the plugin's own words on the member.
        var failed = await TellAndAwaitAsync("fail:on purpose");
        Assert.Equal(MessageTypes.Failed, failed.Type);
        Assert.Equal("asked to fail: on purpose", Services.GetRequiredService<ContainerHost>().Find(Echo)!.Snapshot().Failed);
    }

    /// <summary>What the template's README tells an operator to do: build.sh's output (both
    /// binaries, the manifest, the skill) in <c>plugins/sample-echo-go/0.1.0</c>, <c>active</c>
    /// naming it.</summary>
    private static void Install(string dataRoot)
    {
        var version = Path.Combine(dataRoot, "plugins", Id, "0.1.0");
        CopyDirectory(Build.Value, version);
        File.WriteAllText(Path.Combine(dataRoot, "plugins", Id, PluginCatalog.ActiveFile), "0.1.0\n");
    }

    private static string BuildBothBinaries()
    {
        var sample = TemplatePath();
        var go = PathSearch.Find("go") ?? PathSearch.Find("/usr/local/go/bin/go")
            ?? throw new InvalidOperationException("`go` is not on PATH; the Go template's end-to-end test builds it (Go is in the product image).");
        var output = Path.Combine(Path.GetTempPath(), $"harness-sample-echo-go-{Guid.NewGuid():N}");

        foreach (var (arch, platform) in new[] { ("amd64", "linux-x64"), ("arm64", "linux-arm64") })
        {
            var start = new ProcessStartInfo(go)
            {
                WorkingDirectory = sample,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in new[] { "build", "-trimpath", "-ldflags=-s -w", "-o", Path.Combine(output, "bin", platform, Id), "." })
            {
                start.ArgumentList.Add(arg);
            }

            start.Environment["CGO_ENABLED"] = "0";
            start.Environment["GOOS"] = "linux";
            start.Environment["GOARCH"] = arch;

            using var process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync();
            process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException($"go build ({arch}) failed: {stderr.Result}");
        }

        File.Copy(Path.Combine(sample, "plugin.json"), Path.Combine(output, "plugin.json"));
        Directory.CreateDirectory(Path.Combine(output, "skills"));
        File.Copy(Path.Combine(sample, "skills", Id + ".md"), Path.Combine(output, "skills", Id + ".md"));
        return output;
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var directory in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, directory)));
        }

        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            File.Copy(file, target);
            File.SetUnixFileMode(target, File.GetUnixFileMode(file));
        }
    }

    private const ushort ElfMachineX64 = 0x3E;
    private const ushort ElfMachineArm64 = 0xB7;

    /// <summary>A 64-bit little-endian ELF's machine, and whether it names a program interpreter
    /// (a dynamic loader, PT_INTERP): a static binary has none.</summary>
    private static (ushort Machine, bool HasInterpreter) ReadElf(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.AsSpan(0, 4).SequenceEqual("\u007fELF"u8), $"{path} is not an ELF file");
        Assert.Equal(2, bytes[4]); // 64-bit

        var machine = BitConverter.ToUInt16(bytes, 18);
        var programHeaders = (long)BitConverter.ToUInt64(bytes, 32);
        var entrySize = BitConverter.ToUInt16(bytes, 54);
        var count = BitConverter.ToUInt16(bytes, 56);

        const uint interpreter = 3;
        var hasInterpreter = Enumerable.Range(0, count)
            .Any(i => BitConverter.ToUInt32(bytes, (int)(programHeaders + (i * entrySize))) == interpreter);
        return (machine, hasInterpreter);
    }

    private async Task<Message> TellAndAwaitAsync(string instruction)
    {
        var after = (await TerminalRowsAsync()).Select(m => m.Seq).DefaultIfEmpty(0).Max();

        var told = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers/{Echo.Name}/tell", new { instruction }, Ct);
        Assert.True(told.IsSuccessStatusCode, await told.Content.ReadAsStringAsync(Ct));

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if ((await TerminalRowsAsync()).FirstOrDefault(m => m.Seq > after) is { } row) return row;
            await Task.Delay(50, Ct);
        }

        throw new TimeoutException($"{Echo} wrote no terminal row after seq {after}.");
    }

    private async Task<IReadOnlyList<Message>> TerminalRowsAsync() =>
        [.. (await Services.GetRequiredService<IMessageLog>()
                .ReadAfterAsync(0, [MessageTypes.Completed, MessageTypes.Failed], int.MaxValue, Ct))
            .Where(m => m.Source == Echo.ToString())];

    private static JsonElement Payload(Message row) => JsonDocument.Parse(row.Payload).RootElement.Clone();

    private static string TemplatePath([CallerFilePath] string caller = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(caller)!, "..", "..", "templates", "plugin-go"));
}
