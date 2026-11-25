using System.Text.Json.Nodes;
using Yawble.Core.Launch;
using Yawble.Core.Policy;
using Yawble.Core.Sandboxing;
using Yawble.Core.Tests.Policy;
using Xunit;

namespace Yawble.Core.Tests.Launch;

/// <summary>
/// No name lookup inside a Linux sandbox reaches the host's resolver. The host resolver's sockets and the
/// name-service files present on the host are denied (masked) in every Linux request. Fakes only: the host's disk is a
/// dictionary of real paths.
/// </summary>
public class ResolverMaskTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Fedora 43: systemd-resolved, D-Bus, Avahi; /etc/resolv.conf links into resolved's folder.</summary>
    private static readonly Dictionary<string, string> Fedora = new()
    {
        ["/run/systemd/resolve"] = "/run/systemd/resolve",
        ["/run/dbus"] = "/run/dbus",
        ["/run/avahi-daemon"] = "/run/avahi-daemon",
        ["/etc/nsswitch.conf"] = "/etc/nsswitch.conf",
        ["/etc/resolv.conf"] = "/run/systemd/resolve/stub-resolv.conf",
    };

    private static List<string> Denied(JsonObject request) =>
        request["filesystem"]!["deniedPaths"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();

    [Fact]
    public void OnFedoraTheResolvedFolderTheBusAvahiAndNsswitchAreMaskedAndResolvConfIsAlreadyInsideOne()
    {
        var masks = LinuxResolverMasks.Present(p => Fedora.GetValueOrDefault(p));
        Assert.Equal(["/run/systemd/resolve", "/run/dbus", "/run/avahi-daemon", "/etc/nsswitch.conf"], masks);
    }

    [Fact]
    public void APlainResolvConfIsMaskedAndAMissingOrDanglingPathIsNot()
    {
        // Debian without systemd-resolved: a plain /etc/resolv.conf, nscd running; /run/dbus a dangling link (null).
        var debian = new Dictionary<string, string?>
        {
            ["/run/nscd"] = "/run/nscd", ["/etc/nsswitch.conf"] = "/etc/nsswitch.conf", ["/etc/resolv.conf"] = "/etc/resolv.conf", ["/run/dbus"] = null,
        };
        Assert.Equal(["/run/nscd", "/etc/nsswitch.conf", "/etc/resolv.conf"], LinuxResolverMasks.Present(p => debian.GetValueOrDefault(p)));

        // Ubuntu with resolvconf: the link's target is outside every mask, so the link itself is denied (the sandbox masks its target).
        var ubuntu = new Dictionary<string, string> { ["/etc/resolv.conf"] = "/run/resolvconf/resolv.conf" };
        Assert.Equal(["/etc/resolv.conf"], LinuxResolverMasks.Present(p => ubuntu.GetValueOrDefault(p)));

        Assert.Empty(LinuxResolverMasks.Present(_ => null));
    }

    [Fact]
    public void EverySocketTheGuardrailCheckTriesLiesInsideAMaskedFolder()
    {
        Assert.Contains(LinuxResolverMasks.Sockets, s => s is { Id: "net.dns-varlink", Path: "/run/systemd/resolve/io.systemd.Resolve" });
        foreach (var (_, path) in LinuxResolverMasks.Sockets)
            Assert.Contains(LinuxResolverMasks.Candidates, c => path.StartsWith(c + "/", StringComparison.Ordinal));
    }

    [Fact]
    public void ALinuxRequestDeniesTheMasksAndTheInvariantsRequireThem()
    {
        var run = PolicyFixtures.Run(TargetOs.Linux, CredentialMode.ClaudeSubscription);
        var masks = LinuxResolverMasks.Present(p => Fedora.GetValueOrDefault(p));
        run = run with { Layout = run.Layout with { ResolverMasks = masks } };
        var compiled = SandboxRequestCompiler.Compile(PolicyFixtures.Global, null, null, run);
        var denied = Denied(compiled.Request);
        foreach (var m in masks) Assert.Contains(m, denied);
        Assert.Empty(SandboxRequestInvariants.Check(compiled, run));

        // A request that lost one is refused.
        var request = (JsonObject)compiled.Request.DeepClone();
        var list = request["filesystem"]!["deniedPaths"]!.AsArray();
        list.Remove(list.First(n => n!.GetValue<string>() == "/run/systemd/resolve"));
        Assert.Contains(SandboxRequestInvariants.CheckRequest(request, run), p => p.Contains("/run/systemd/resolve"));
    }

    [Fact]
    public void OnlyALinuxLayoutCarriesTheMasks()
    {
        foreach (var os in new[] { TargetOs.Windows, TargetOs.MacOS })
        {
            var run = PolicyFixtures.Run(os, CredentialMode.ClaudeSubscription);
            var layout = run.Layout with { ResolverMasks = ["/run/systemd/resolve"] };
            Assert.DoesNotContain("/run/systemd/resolve", layout.ImplicitDenied);
        }
    }

    [Fact]
    public async Task TheLauncherMasksWhatIsOnTheHostAtEveryLinuxLaunch()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the launcher builds a layout for the host's own OS; this asserts a Linux host's");
        await using var kit = await LaunchKit.StartAsync(realPath: p => Fedora.GetValueOrDefault(p));
        kit.Backend.Script = (_, p) => { p.Exit(0); return Task.CompletedTask; };
        await kit.Bus.PersonTellAsync("alpha", "dev1", "hello", ct: Ct);
        await kit.OutcomeAsync("dev1");

        string json;
        lock (kit.Backend.Requests) json = kit.Backend.Requests.Single().Json;
        // The request's deniedPaths are canonicalized on the real host (HostPathCanonicalizer): on Fedora and RHEL
        // /etc/nsswitch.conf is a symlink to /etc/authselect/nsswitch.conf, so compare canonical forms.
        var denied = Denied(JsonNode.Parse(json)!.AsObject());
        static string C(string p) => HostPathCanonicalizer.Canonicalize(p);
        foreach (var m in new[] { "/run/systemd/resolve", "/run/dbus", "/run/avahi-daemon", "/etc/nsswitch.conf" }) Assert.Contains(C(m), denied);
        Assert.DoesNotContain("/etc/resolv.conf", denied);
        Assert.DoesNotContain(C("/etc/resolv.conf"), denied);
        Assert.DoesNotContain("/run/nscd", denied);
    }
}
