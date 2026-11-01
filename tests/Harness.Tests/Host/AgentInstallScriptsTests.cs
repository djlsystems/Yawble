using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;

namespace Harness.Tests.Host;

/// <summary>
/// <c>scripts/ensure-agent-clis.sh</c> and <c>scripts/install-google-chrome.sh</c>, RUN, against a scratch data
/// root (HARNESS_DATA_ROOT) and fake vendors first on PATH: an <c>npm</c>, a <c>curl</c> and an <c>npx</c> that
/// each take a second, log when they start and end, and can be told one vendor is unreachable. Nothing here
/// reaches the network or the instance's own volume.
/// </summary>
public sealed class AgentInstallScriptsTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("agent-install-scripts-").FullName;
    private string Data => Path.Combine(_work, "data");
    private string Fakes => Path.Combine(_work, "fakes");
    private string Log => Path.Combine(_work, "vendors.log");

    public AgentInstallScriptsTests()
    {
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Fakes);
        Fake("npm", """
            [ "$1" = install ] || exit 0
            pkg="$3"; base=${pkg%@latest}
            echo "start $base $(date +%s.%N) $pkg" >> "$VENDOR_LOG"
            sleep 1
            case "$base" in @anthropic-ai/claude-code) n=claude;; @openai/codex) n=codex;; @github/copilot) n=copilot;; *) exit 3;; esac
            case " ${UNREACHABLE:-} " in *" $n "*) echo "npm ERR! network request to registry.npmjs.org failed: unreachable" >&2; exit 1;; esac
            mkdir -p "$NPM_CONFIG_PREFIX/bin" && printf '#!/bin/sh\necho "%s 1.0.0"\n' "$n" > "$NPM_CONFIG_PREFIX/bin/$n" && chmod 755 "$NPM_CONFIG_PREFIX/bin/$n"
            echo "end $base $(date +%s.%N)" >> "$VENDOR_LOG"
            """);
        Fake("curl", """
            case "$*" in *x.ai*) ;; *) exec /usr/bin/curl "$@";; esac
            echo "start grok $(date +%s.%N)" >> "$VENDOR_LOG"
            out=""; while [ $# -gt 0 ]; do [ "$1" = -o ] && { out=$2; shift; }; shift; done
            sleep 1
            case " ${UNREACHABLE:-} " in *" grok "*) exit 6;; esac
            printf 'mkdir -p "$HOME/.grok/bin"; printf "#!/bin/sh\\necho grok 1.0.0\\n" > "$HOME/.grok/bin/grok"; chmod 755 "$HOME/.grok/bin/grok"\n' > "$out"
            echo "end grok $(date +%s.%N)" >> "$VENDOR_LOG"
            """);
        Fake("npx", """
            echo "start chromium $(date +%s.%N)" >> "$VENDOR_LOG"
            sleep 1
            mkdir -p "$PLAYWRIGHT_BROWSERS_PATH/chromium_headless_shell-1"
            echo "end chromium $(date +%s.%N)" >> "$VENDOR_LOG"
            """);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void Fake(string name, string body)
    {
        var path = Path.Combine(Fakes, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private (int Code, string Output) Run(string script, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? more = null)
    {
        var start = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(PrepareVolumeTests.RepoRoot(), "scripts", script));
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment.Clear();
        start.Environment["PATH"] = $"{Fakes}:{AgentCliIsolation.UpdateRoot}:/usr/bin:/bin";
        start.Environment["HOME"] = Path.Combine(Data, "agent-home");
        start.Environment["HARNESS_DATA_ROOT"] = Data;
        start.Environment["VENDOR_LOG"] = Log;
        foreach (var (key, value) in more ?? new Dictionary<string, string>()) start.Environment[key] = value;
        AgentCliIsolation.Guard(start.Environment);

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(60_000), "the script did not end within a minute");
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    private List<(string Kind, string Name, double At)> Vendors() =>
        File.Exists(Log)
            ? [.. File.ReadAllLines(Log).Select(l => l.Split(' ')).Select(p => (p[0], p[1], double.Parse(p[2], CultureInfo.InvariantCulture)))]
            : [];

    private void Installed(params string[] names)
    {
        foreach (var name in names)
        {
            var bin = Directory.CreateDirectory(name == "grok" ? Path.Combine(Data, "agent-home", ".grok", "bin") : Path.Combine(Data, "npm-global", "bin")).FullName;
            File.WriteAllText(Path.Combine(bin, name), $"#!/bin/sh\necho '{name} 0.9.0'\n");
            File.SetUnixFileMode(Path.Combine(bin, name), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void All_of_them_download_at_the_same_time_and_an_unreachable_vendor_fails_alone()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The scripts run on Linux.");

        var watch = Stopwatch.StartNew();
        var (code, output) = Run("ensure-agent-clis.sh", [], new Dictionary<string, string> { ["UNREACHABLE"] = "codex" });
        watch.Stop();

        Assert.Equal(1, code);
        var vendors = Vendors();
        Assert.Equal(["chromium", "claude-code", "codex", "copilot", "grok"], vendors.Where(v => v.Kind == "start").Select(v => v.Name.Split('/')[^1]).Order());
        // Every one started before any ended: in parallel, about one second in all and not five.
        Assert.True(vendors.Where(v => v.Kind == "start").Max(v => v.At) < vendors.Where(v => v.Kind == "end").Min(v => v.At), output);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"took {watch.Elapsed}\n{output}");

        // Each reported on its own; codex failed and held no other.
        Assert.Contains("agent cli: FAILED to install codex; the other agents are not held by it.", output);
        Assert.Contains("agent cli: codex did not install or update (exit 1); the others were not held by it", output);
        foreach (var name in new[] { "claude", "copilot" })
        {
            Assert.Contains($"agent cli: {name} installed", output);
            Assert.True(File.Exists(Path.Combine(Data, "npm-global", "bin", name)), name);
        }
        Assert.Contains("agent cli: grok installed", output);
        Assert.Contains("agent cli: headless chromium installed", output);
        Assert.False(File.Exists(Path.Combine(Data, "npm-global", "bin", "codex")));
        Assert.Contains("\"codex\":null", File.ReadAllText(Path.Combine(Data, "cli-versions.jsonl")));
    }

    [Fact]
    public void One_named_cli_is_installed_alone_and_its_failure_is_the_exit_code()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The scripts run on Linux.");

        var (code, output) = Run("ensure-agent-clis.sh", ["claude"], new Dictionary<string, string> { ["HARNESS_UPDATE_AGENTS"] = "0" });
        Assert.Equal(0, code);
        Assert.Equal(["claude-code"], Vendors().Where(v => v.Kind == "start").Select(v => v.Name.Split('/')[^1]));
        Assert.Contains("agent cli: claude installed", output);

        var (failed, said) = Run("ensure-agent-clis.sh", ["grok"], new Dictionary<string, string> { ["UNREACHABLE"] = "grok" });
        Assert.Equal(1, failed);
        Assert.Contains("agent cli: FAILED to install grok", said);

        Assert.Equal(2, Run("ensure-agent-clis.sh", ["not-a-cli"]).Code);
    }

    [Fact]
    public void An_installed_cli_with_updates_off_runs_no_npm_and_a_reinstall_downloads_it_again()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The scripts run on Linux.");
        Installed("claude", "codex", "copilot", "grok");

        foreach (var name in new[] { "claude", "codex", "copilot", "grok" })
        {
            var (code, output) = Run("ensure-agent-clis.sh", [name], new Dictionary<string, string> { ["HARNESS_UPDATE_AGENTS"] = "0" });
            Assert.Equal(0, code);
            Assert.Contains($"agent cli: {name} is already installed", output);
        }
        Assert.Empty(Vendors());

        var (again, _) = Run("ensure-agent-clis.sh", ["claude"], new Dictionary<string, string> { ["HARNESS_UPDATE_AGENTS"] = "0", ["HARNESS_REINSTALL_AGENTS"] = "1" });
        Assert.Equal(0, again);
        Assert.Equal("@anthropic-ai/claude-code", Assert.Single(Vendors(), v => v.Kind == "start").Name);
    }

    [Fact]
    public void Google_chrome_reports_its_state_in_its_status_file_for_control()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The scripts run on Linux.");
        var chrome = Directory.CreateDirectory(Path.Combine(Data, "google-chrome")).FullName;
        var status = Path.Combine(chrome, ".status");

        // Google unreachable, nothing installed: failed, in a sentence.
        var (code, _) = Run("install-google-chrome.sh", [], new Dictionary<string, string> { ["HARNESS_CHROME_INDEX"] = "file:///nonexistent/Packages" });
        Assert.Equal(0, code);
        Assert.StartsWith("failed ", File.ReadAllText(status));
        Assert.Equal("644", Convert.ToString((int)File.GetUnixFileMode(status) & 0x1ff, 8));

        // The newest stable already there: ready, with its version.
        var deb = Path.Combine(_work, "chrome.deb");
        File.WriteAllText(deb, "not really a deb");
        var sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(deb)));
        var index = Path.Combine(_work, "Packages");
        File.WriteAllText(index, $"Package: google-chrome-stable\nVersion: 131.0.6778.85-1\nFilename: pool/main/g/chrome.deb\nSize: 16\nSHA256: {sha}\n\n");
        var version = Directory.CreateDirectory(Path.Combine(chrome, "131.0.6778.85")).FullName;
        File.WriteAllText(Path.Combine(version, "chrome"), "#!/bin/sh\n");
        File.SetUnixFileMode(Path.Combine(version, "chrome"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Run("install-google-chrome.sh", [], new Dictionary<string, string> { ["HARNESS_CHROME_INDEX"] = "file://" + index });
        Assert.Equal("ready 131.0.6778.85\n", File.ReadAllText(status));

        // A newer one that fails to download keeps the one there, and says it is ready.
        File.WriteAllText(index, $"Package: google-chrome-stable\nVersion: 132.0.1-1\nFilename: pool/main/g/chrome.deb\nSize: 16\nSHA256: {sha}\n\n");
        Run("install-google-chrome.sh", [], new Dictionary<string, string>
        {
            ["HARNESS_CHROME_INDEX"] = "file://" + index, ["HARNESS_CHROME_POOL"] = "file:///nonexistent",
        });
        Assert.Equal("ready 131.0.6778.85\n", File.ReadAllText(status));
    }
}
