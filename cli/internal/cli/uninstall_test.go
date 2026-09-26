package cli_test

import (
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/remote"
)

func uninstallScript() *engine.Scripted {
	s := engine.NewScripted()
	s.On("podman container inspect --format {{.State.Status}}|{{.ImageName}}|{{index .Config.Labels \"yawble.settings\"}} yawble", engine.Result{Stdout: "running|" + testImage + "|" + currentLabel() + "\n"})
	return s
}

func TestUninstallWithoutDataRemovesEverythingButTheVolume(t *testing.T) {
	s := uninstallScript()
	deps := stubbed(s)
	code, out, errOut := run(t, deps, "uninstall")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	c := strings.Join(s.Calls, "\n")
	for _, want := range []string{"podman rm -f yawble-tunnel", "podman rm -f yawble", "podman pod rm -f yawble", "podman rmi " + testImage} {
		if !strings.Contains(c, want) {
			t.Errorf("missing %q in:\n%s", want, c)
		}
	}
	if strings.Contains(c, "volume rm") {
		t.Errorf("the volume must stay without --data:\n%s", c)
	}
	if !strings.Contains(out, "yawble-data") || !strings.Contains(out, "kept") {
		t.Errorf("out %q", out)
	}
}

// Review Focus 5: --data with no terminal and no --yes keeps the volume and says why.
func TestUninstallDataWithoutATerminalKeepsTheVolumeAndSaysWhy(t *testing.T) {
	s := uninstallScript()
	deps := stubbed(s)
	deps.Interactive = false
	code, out, _ := run(t, deps, "uninstall", "--data")
	if code != 0 {
		t.Fatalf("exit %d: %s", code, out)
	}
	if strings.Contains(strings.Join(s.Calls, "\n"), "volume rm") || !strings.Contains(out, "--yes") {
		t.Errorf("out %q calls %q", out, s.Calls)
	}
}

func TestUninstallDataWithTheTypedWordRemovesTheVolume(t *testing.T) {
	s := uninstallScript()
	deps := stubbed(s)
	deps.Interactive = true
	deps.Stdin = strings.NewReader("delete\n")
	code, out, _ := run(t, deps, "uninstall", "--data")
	if code != 0 || !strings.Contains(strings.Join(s.Calls, "\n"), "podman volume rm yawble-data") {
		t.Errorf("exit %d out %q calls %q", code, out, s.Calls)
	}
}

func TestUninstallDataWithTheWrongWordKeepsTheVolume(t *testing.T) {
	s := uninstallScript()
	deps := stubbed(s)
	deps.Interactive = true
	deps.Stdin = strings.NewReader("yes\n")
	code, out, _ := run(t, deps, "uninstall", "--data")
	if code != 0 || strings.Contains(strings.Join(s.Calls, "\n"), "volume rm") || !strings.Contains(out, "kept") {
		t.Errorf("exit %d out %q calls %q", code, out, s.Calls)
	}
}

func TestUninstallDataYesRemovesTheVolumeWithoutAPrompt(t *testing.T) {
	s := uninstallScript()
	deps := stubbed(s)
	deps.Interactive = false
	dir := t.TempDir()
	deps.ConfigDir = dir
	_ = remote.Save(dir, remote.Config{Provider: "cloudflare"})
	code, out, _ := run(t, deps, "uninstall", "--data", "--yes")
	if code != 0 || !strings.Contains(strings.Join(s.Calls, "\n"), "podman volume rm yawble-data") {
		t.Errorf("exit %d out %q calls %q", code, out, s.Calls)
	}
	if !strings.Contains(out, dir) {
		t.Errorf("the config directory is left and should be named: %q", out)
	}
	if _, err := os.Stat(filepath.Join(dir, "remote.toml")); err != nil {
		t.Error("uninstall must not delete remote.toml; the person was told where the config is")
	}
}

func TestUpOpensTheBoardUnlessToldNotTo(t *testing.T) {
	for _, tc := range []struct {
		goos string
		want string
	}{
		{"linux", "xdg-open http://127.0.0.1:8080"},
		{"darwin", "open http://127.0.0.1:8080"},
		{"windows", "rundll32 url.dll,FileProtocolHandler http://127.0.0.1:8080"},
	} {
		s := upScript()
		if tc.goos != "linux" {
			s.On("podman machine inspect", engine.Result{Stdout: "podman-machine-default|running|false|8192|4\n"})
			s.On("wsl --status", engine.Result{})
			s.On("wsl -d podman-machine-default -e free -m", engine.Result{Stdout: "Mem: 15688 1 1\n"})
		}
		deps := stubbed(s)
		deps.GOOS, deps.LookPath, deps.Interactive = tc.goos, lookPath("podman"), true
		if code, _, errOut := run(t, deps, "up"); code != 0 {
			t.Fatalf("%s: exit %d %s", tc.goos, code, errOut)
		}
		if !strings.Contains(strings.Join(s.Calls, "\n"), tc.want) {
			t.Errorf("%s: browser not opened:\n%s", tc.goos, strings.Join(s.Calls, "\n"))
		}
	}
	s := upScript()
	deps := stubbed(s)
	deps.LookPath, deps.Interactive = lookPath("podman"), true
	_, _, _ = run(t, deps, "up", "--no-browser")
	if strings.Contains(strings.Join(s.Calls, "\n"), "xdg-open") {
		t.Error("--no-browser opened the browser")
	}
	s = upScript()
	deps = stubbed(s)
	deps.LookPath, deps.Interactive = lookPath("podman"), false
	_, _, _ = run(t, deps, "up")
	if strings.Contains(strings.Join(s.Calls, "\n"), "xdg-open") {
		t.Error("a non-interactive up opened the browser")
	}
}
