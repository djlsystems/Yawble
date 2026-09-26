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

// settingsDir is a config directory as `up`, `secret set` and `remote enable` leave it.
func settingsDir(t *testing.T) string {
	t.Helper()
	dir := t.TempDir()
	for name, body := range map[string]string{
		"config.toml": "engine = \"podman\"\n",
		"env":         "ANTHROPIC_API_KEY=sk-test\n",
	} {
		if err := os.WriteFile(filepath.Join(dir, name), []byte(body), 0o600); err != nil {
			t.Fatal(err)
		}
	}
	if err := remote.Save(dir, remote.Config{Provider: "cloudflare"}); err != nil {
		t.Fatal(err)
	}
	return dir
}

func exists(path string) bool {
	_, err := os.Stat(path)
	return err == nil
}

func TestUninstallWithoutDataRemovesEverythingButTheVolume(t *testing.T) {
	s := uninstallScript()
	deps := stubbed(s)
	code, out, errOut := run(t, deps, "uninstall", "--yes")
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

// The settings folder holds the saved API keys and tunnel credentials: an uninstall that left
// them on disk would not be one.
func TestUninstallRemovesTheSettingsFolderWithItsSecrets(t *testing.T) {
	s := uninstallScript()
	deps := stubbed(s)
	dir := settingsDir(t)
	deps.ConfigDir = dir

	code, out, errOut := run(t, deps, "uninstall", "--yes")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if exists(dir) {
		t.Errorf("the settings folder %s is still there", dir)
	}
	if !strings.Contains(out, "removed yawble's settings") {
		t.Errorf("out %q", out)
	}
}

func TestUninstallKeepSettingsLeavesThemAndNamesTheFolder(t *testing.T) {
	s := uninstallScript()
	deps := stubbed(s)
	dir := settingsDir(t)
	deps.ConfigDir = dir

	code, out, errOut := run(t, deps, "uninstall", "--keep-settings", "--yes")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	for _, name := range []string{"config.toml", "env", remote.FileName} {
		if !exists(filepath.Join(dir, name)) {
			t.Errorf("--keep-settings removed %s", name)
		}
	}
	if !strings.Contains(out, dir) {
		t.Errorf("the kept folder is not named: %q", out)
	}
}

func TestUninstallAsksFirstAndANoRemovesNothing(t *testing.T) {
	s := uninstallScript()
	deps := stubbed(s)
	deps.Interactive = true
	deps.Stdin = strings.NewReader("n\n")
	dir := settingsDir(t)
	deps.ConfigDir = dir

	code, out, _ := run(t, deps, "uninstall")
	if code != 0 {
		t.Fatalf("exit %d: %s", code, out)
	}
	if !strings.Contains(out, "saved API keys") || !strings.Contains(out, "nothing was removed") {
		t.Errorf("out %q", out)
	}
	if strings.Contains(strings.Join(s.Calls, "\n"), "rm ") || !exists(filepath.Join(dir, "env")) {
		t.Errorf("removed after a no: calls %q", s.Calls)
	}
}

func TestUninstallWithoutATerminalOrYesRemovesNothing(t *testing.T) {
	s := uninstallScript()
	deps := stubbed(s)
	deps.Interactive = false
	dir := settingsDir(t)
	deps.ConfigDir = dir

	code, out, errOut := run(t, deps, "uninstall")
	if code == 0 || !strings.Contains(out+errOut, "--yes") {
		t.Errorf("exit %d out %q err %q", code, out, errOut)
	}
	if strings.Contains(strings.Join(s.Calls, "\n"), "rm ") || !exists(filepath.Join(dir, "env")) {
		t.Errorf("removed without a yes: calls %q", s.Calls)
	}
}

// Only the files yawble writes are removed: a folder someone put something else in keeps it,
// and says so, rather than deleting what yawble cannot vouch for.
func TestUninstallLeavesFilesItDidNotWrite(t *testing.T) {
	s := uninstallScript()
	deps := stubbed(s)
	dir := settingsDir(t)
	deps.ConfigDir = dir
	if err := os.WriteFile(filepath.Join(dir, "notes.txt"), []byte("mine"), 0o600); err != nil {
		t.Fatal(err)
	}

	code, out, errOut := run(t, deps, "uninstall", "--yes")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if exists(filepath.Join(dir, "env")) || exists(filepath.Join(dir, "config.toml")) {
		t.Error("yawble's own files were left")
	}
	if !exists(filepath.Join(dir, "notes.txt")) || !strings.Contains(out, "notes.txt") {
		t.Errorf("a file yawble did not write was touched or not named: %q", out)
	}
}

func TestUninstallSaysHowToRemoveTheProgramItself(t *testing.T) {
	for _, tc := range []struct{ goos, want string }{
		{"linux", "rm "},
		{"darwin", "rm "},
		{"windows", "Remove-Item"},
	} {
		s := uninstallScript()
		deps := stubbed(s)
		deps.GOOS = tc.goos
		if tc.goos != "linux" {
			s.On("podman machine inspect", engine.Result{Stdout: "podman-machine-default|running|false|8192|4\n"})
			s.On("wsl", engine.Result{})
		}
		_, out, _ := run(t, deps, "uninstall", "--yes")
		if !strings.Contains(out, "To remove the yawble program itself") || !strings.Contains(out, tc.want) {
			t.Errorf("%s: out %q", tc.goos, out)
		}
	}
}

func TestUninstallDataWithTheTypedWordRemovesTheVolume(t *testing.T) {
	s := uninstallScript()
	deps := stubbed(s)
	deps.Interactive = true
	deps.Stdin = strings.NewReader("y\ndelete\n")
	code, out, _ := run(t, deps, "uninstall", "--data")
	if code != 0 || !strings.Contains(strings.Join(s.Calls, "\n"), "podman volume rm yawble-data") {
		t.Errorf("exit %d out %q calls %q", code, out, s.Calls)
	}
}

func TestUninstallDataWithTheWrongWordKeepsTheVolume(t *testing.T) {
	s := uninstallScript()
	deps := stubbed(s)
	deps.Interactive = true
	deps.Stdin = strings.NewReader("y\nyes\n")
	code, out, _ := run(t, deps, "uninstall", "--data")
	if code != 0 || strings.Contains(strings.Join(s.Calls, "\n"), "volume rm") || !strings.Contains(out, "kept") {
		t.Errorf("exit %d out %q calls %q", code, out, s.Calls)
	}
}

func TestUninstallDataYesRemovesTheVolumeAndTheSettingsWithoutAPrompt(t *testing.T) {
	s := uninstallScript()
	deps := stubbed(s)
	deps.Interactive = false
	dir := settingsDir(t)
	deps.ConfigDir = dir
	code, out, _ := run(t, deps, "uninstall", "--data", "--yes")
	if code != 0 || !strings.Contains(strings.Join(s.Calls, "\n"), "podman volume rm yawble-data") {
		t.Errorf("exit %d out %q calls %q", code, out, s.Calls)
	}
	if exists(dir) {
		t.Errorf("the settings folder %s is still there", dir)
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
