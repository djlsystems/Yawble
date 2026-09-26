package cli_test

import (
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/engine"
)

const tunnelInspect = "podman container inspect --format {{.State.Status}}|{{.ImageName}}|{{index .Config.Labels \"yawble.settings\"}} yawble-tunnel"

func tunnelScript() *engine.Scripted {
	s := runningScript()
	// Absent before the run, running once started (the URL wait checks the sidecar is alive).
	s.OnSequence(tunnelInspect,
		engine.Result{Stderr: "no such container", ExitCode: 125},
		engine.Result{Stdout: "running|docker.io/cloudflare/cloudflared:latest|\n"},
	)
	s.On("podman logs --tail 200 yawble-tunnel", engine.Result{Stdout: "INF |  https://quiet-owl.trycloudflare.com  |\n"})
	return s
}

func TestRemoteEnableCloudflarePrintsTheURLAndRemembersTheProvider(t *testing.T) {
	s := tunnelScript()
	deps := stubbed(s)
	dir := t.TempDir()
	deps.ConfigDir = dir
	code, out, errOut := run(t, deps, "remote", "enable", "cloudflare")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if !strings.Contains(out, "https://quiet-owl.trycloudflare.com") || !strings.Contains(out, "quick tunnel") {
		t.Errorf("out %q", out)
	}
	if _, err := os.Stat(filepath.Join(dir, "remote.toml")); err != nil {
		t.Errorf("remote.toml not written: %v", err)
	}
}

func TestRemoteEnableTailscaleNeedsAnAuthKey(t *testing.T) {
	deps := stubbed(tunnelScript())
	deps.Interactive = false
	code, _, errOut := run(t, deps, "remote", "enable", "tailscale")
	if code != 2 || !strings.Contains(errOut, "--token") {
		t.Errorf("exit %d stderr %q", code, errOut)
	}
}

func TestRemoteEnableAnUnknownProviderIsAUsageError(t *testing.T) {
	code, _, errOut := run(t, stubbed(tunnelScript()), "remote", "enable", "pigeon")
	if code != 2 || !strings.Contains(errOut, "cloudflare, tailscale, ngrok") {
		t.Errorf("exit %d stderr %q", code, errOut)
	}
}

func TestRemoteStatusWithNothingEnabledSaysSo(t *testing.T) {
	code, out, _ := run(t, stubbed(engine.NewScripted()), "remote", "status")
	if code != 0 || !strings.Contains(out, "not enabled") {
		t.Errorf("exit %d out %q", code, out)
	}
}

func TestRemoteStatusAfterEnableShowsProviderStateAndURL(t *testing.T) {
	s := tunnelScript()
	deps := stubbed(s)
	dir := t.TempDir()
	deps.ConfigDir = dir
	if code, _, errOut := run(t, deps, "remote", "enable", "cloudflare"); code != 0 {
		t.Fatalf("enable: %d %s", code, errOut)
	}
	s2 := engine.NewScripted()
	s2.On(tunnelInspect, engine.Result{Stdout: "running|docker.io/cloudflare/cloudflared:latest|\n"})
	s2.On("podman logs", engine.Result{Stdout: "https://quiet-owl.trycloudflare.com\n"})
	deps = stubbed(s2)
	deps.ConfigDir = dir
	code, out, _ := run(t, deps, "remote", "status", "--json")
	if code != 0 || !strings.Contains(out, `"provider": "cloudflare"`) || !strings.Contains(out, `"url": "https://quiet-owl.trycloudflare.com"`) || !strings.Contains(out, `"state": "running"`) {
		t.Errorf("exit %d out %s", code, out)
	}
}

func TestRemoteDisableForgetRemovesTheSidecarAndTheFile(t *testing.T) {
	s := tunnelScript()
	deps := stubbed(s)
	dir := t.TempDir()
	deps.ConfigDir = dir
	if code, _, errOut := run(t, deps, "remote", "enable", "cloudflare"); code != 0 {
		t.Fatalf("enable: %d %s", code, errOut)
	}
	s2 := engine.NewScripted()
	s2.On(tunnelInspect, engine.Result{Stdout: "running|docker.io/cloudflare/cloudflared:latest|\n"})
	deps = stubbed(s2)
	deps.ConfigDir = dir
	code, out, _ := run(t, deps, "remote", "disable", "--forget")
	if code != 0 || !strings.Contains(strings.Join(s2.Calls, "\n"), "podman rm -f yawble-tunnel") || !strings.Contains(out, "forgotten") {
		t.Errorf("exit %d out %q calls %q", code, out, s2.Calls)
	}
	if _, err := os.Stat(filepath.Join(dir, "remote.toml")); !os.IsNotExist(err) {
		t.Error("remote.toml should be gone")
	}
}
