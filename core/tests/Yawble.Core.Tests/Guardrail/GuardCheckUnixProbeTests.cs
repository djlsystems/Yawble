using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Tests.Gateway;
using Xunit;

namespace Yawble.Core.Tests.Guardrail;

/// <summary>
/// B035 verify (card 162): the real yawble-guardcheck probe's "unix" attempts, outside any sandbox. A live socket in a
/// visible folder is what the old code gave a sandbox (systemd-resolved's folder bound read-only, no mask): the probe
/// must say ALLOWED there, or net.dns-varlink could never catch the escape. An empty folder (what a mask leaves) and a
/// missing folder must say BLOCKED. No real system program: the test's own socket in its own temp folder.
/// </summary>
public class GuardCheckUnixProbeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ALiveSocketIsAllowedAndAMaskedOrMissingFolderIsBlocked()
    {
        var root = Path.Combine(Path.GetTempPath(), "ux" + Guid.NewGuid().ToString("N")[..8]);
        var live = Path.Combine(root, "s");
        // sun_path holds about 108 bytes; a long temp folder (a Windows agent sandbox) cannot hold a socket at all.
        Assert.SkipWhen(Encoding.UTF8.GetByteCount(live) > 100, $"the temp folder is too long for a Unix socket path: {live}");
        Directory.CreateDirectory(Path.Combine(root, "masked"));
        try
        {
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(live));
            listener.Listen(8);

            var unix = new JsonArray(
                new JsonObject { ["id"] = "u.live", ["category"] = "network", ["path"] = live },
                new JsonObject { ["id"] = "u.masked", ["category"] = "network", ["path"] = Path.Combine(root, "masked", "s") },
                new JsonObject { ["id"] = "u.missing", ["category"] = "network", ["path"] = Path.Combine(root, "nodir", "s") });
            var plan = new JsonObject
            {
                ["v"] = 1, ["nonce"] = "n", ["reads"] = new JsonArray(), ["writes"] = new JsonArray(), ["tcp"] = new JsonArray(),
                ["dns"] = new JsonArray(), ["unix"] = unix, ["gateway"] = null, ["hostPid"] = null,
                ["scanDirs"] = new JsonArray(), ["keychainServices"] = new JsonArray(),
            };
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(plan.ToJsonString())).TrimEnd('=').Replace('+', '-').Replace('/', '_');

            var psi = Binaries.GuardCheck();
            psi.ArgumentList.Add("--plan");
            psi.ArgumentList.Add(b64);
            using var p = Process.Start(psi)!;
            p.StandardInput.Close();
            var stdout = p.StandardOutput.ReadToEndAsync(Ct);
            var stderr = p.StandardError.ReadToEndAsync(Ct);
            await p.WaitForExitAsync(Ct);
            Assert.True(p.ExitCode == 0, $"exit {p.ExitCode}: {await stderr}");

            var results = (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => JsonNode.Parse(l)!.AsObject())
                .Where(o => o["id"]!.GetValue<string>().StartsWith("u.", StringComparison.Ordinal))
                .ToDictionary(o => o["id"]!.GetValue<string>(), o => (Outcome: o["outcome"]!.GetValue<string>(), Evidence: o["evidence"]!.GetValue<string>()));

            Assert.Equal("allowed", results["u.live"].Outcome);
            Assert.Equal("blocked", results["u.masked"].Outcome);
            Assert.Equal("blocked", results["u.missing"].Outcome);
            Assert.Contains("not visible", results["u.missing"].Evidence);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
