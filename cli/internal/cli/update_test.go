package cli_test

import (
	"errors"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/djlsystems/yawble/cli/internal/buildinfo"
	"github.com/djlsystems/yawble/cli/internal/config"
	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/instance"
)

const pinned = "ghcr.io/djlsystems/yawble:2026.09.25.1"

func pinBuild(t *testing.T) {
	t.Helper()
	buildinfo.Version, buildinfo.ImageTag = "v0.2.0", "2026.09.25.1"
	t.Cleanup(func() { buildinfo.Version, buildinfo.ImageTag = "dev", "" })
}

func labelFor(image string) string {
	s, _ := instance.Defaults(config.Config{Image: image}, instance.Measure(), "")
	return instance.SettingsLabel(s)
}

func TestUpdateOnThePinnedImageSaysSoAndChangesNothing(t *testing.T) {
	pinBuild(t)
	s := engine.NewScripted()
	s.On("podman container inspect", engine.Result{Stdout: "running|" + pinned + "|" + labelFor(pinned) + "\n"})
	deps := stubbed(s)
	deps.Env = func(string) string { return "" } // no YAWBLE_IMAGE: the pin is what counts
	code, out, errOut := run(t, deps, "update")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if !strings.Contains(out, "already") || !strings.Contains(out, pinned) {
		t.Errorf("out %q", out)
	}
	for _, c := range s.Calls {
		if strings.Contains(c, "podman run") || strings.Contains(c, "podman rm") || strings.Contains(c, "podman pull") {
			t.Errorf("changed something: %q", c)
		}
	}
}

func TestUpdateMovesARunningInstanceToThePinnedImageOnTheSameVolume(t *testing.T) {
	pinBuild(t)
	old := "ghcr.io/djlsystems/yawble:2026.09.24.1"
	s := engine.NewScripted()
	s.On("podman container inspect", engine.Result{Stdout: "running|" + old + "|" + labelFor(old) + "\n"})
	s.On("podman image exists", engine.Result{ExitCode: 1})
	deps := stubbed(s)
	deps.Env = func(string) string { return "" }
	code, out, errOut := run(t, deps, "update")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	c := strings.Join(s.Calls, "\n")
	for _, want := range []string{"podman rm -f yawble", "podman pull " + pinned, "podman run -d --name yawble"} {
		if !strings.Contains(c, want) {
			t.Errorf("missing %q in:\n%s", want, c)
		}
	}
	if strings.Contains(c, "volume rm") || strings.Contains(c, "pod rm") {
		t.Errorf("the volume and the pod must stay:\n%s", c)
	}
	if !strings.Contains(out, "image "+old+" -> "+pinned) {
		t.Errorf("out %q", out)
	}
}

func TestUpdateWithNoPinAndNoImageRefuses(t *testing.T) {
	s := engine.NewScripted()
	deps := stubbed(s)
	deps.Env = func(string) string { return "" }
	code, _, errOut := run(t, deps, "update")
	if code != 1 || !strings.Contains(errOut, "pins no") || len(s.Calls) != 0 {
		t.Errorf("exit %d stderr %q calls %q", code, errOut, s.Calls)
	}
}

func TestUpdateCliReplacesThisBinaryFromTheLatestRelease(t *testing.T) {
	buildinfo.Version = "v0.1.0"
	t.Cleanup(func() { buildinfo.Version = "dev" })
	server := fakeReleaseServer(t, "v0.2.0", []byte("the new yawble"))
	dir := t.TempDir()
	exe := filepath.Join(dir, "yawble")
	_ = os.WriteFile(exe, []byte("the old yawble"), 0o755)
	deps := stubbed(engine.NewScripted())
	deps.HTTP = server.Client()
	deps.ReleaseBaseURL = server.URL
	deps.Executable = func() (string, error) { return exe, nil }
	deps.GOOS = "linux"
	code, out, errOut := run(t, deps, "update", "--cli")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	got, _ := os.ReadFile(exe)
	if string(got) != "the new yawble" || !strings.Contains(out, "v0.1.0 -> v0.2.0") {
		t.Errorf("binary %q out %q", got, out)
	}
}

// The command's HTTP client has a short timeout for health polls. A download must not inherit
// it, or every real binary fails on any link slower than the timeout allows.
func TestUpdateCliDownloadsAreNotBoundByTheHealthClientsTimeout(t *testing.T) {
	buildinfo.Version = "v0.1.0"
	t.Cleanup(func() { buildinfo.Version = "dev" })
	server := slowReleaseServer(t, "v0.2.0", []byte("the new yawble"), 300*time.Millisecond)
	dir := t.TempDir()
	exe := filepath.Join(dir, "yawble")
	_ = os.WriteFile(exe, []byte("old"), 0o755)
	deps := stubbed(engine.NewScripted())
	client := server.Client()
	client.Timeout = 50 * time.Millisecond
	deps.HTTP = client
	deps.ReleaseBaseURL = server.URL
	deps.Executable = func() (string, error) { return exe, nil }
	deps.GOOS = "linux"
	code, out, errOut := run(t, deps, "update", "--cli")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if got, _ := os.ReadFile(exe); string(got) != "the new yawble" {
		t.Errorf("binary %q", got)
	}
}

func TestUpdateCliOnTheLatestVersionDoesNothing(t *testing.T) {
	buildinfo.Version = "v0.2.0"
	t.Cleanup(func() { buildinfo.Version = "dev" })
	server := fakeReleaseServer(t, "v0.2.0", []byte("same"))
	dir := t.TempDir()
	exe := filepath.Join(dir, "yawble")
	_ = os.WriteFile(exe, []byte("current"), 0o755)
	deps := stubbed(engine.NewScripted())
	deps.HTTP = server.Client()
	deps.ReleaseBaseURL = server.URL
	deps.Executable = func() (string, error) { return exe, nil }
	deps.GOOS = "linux"
	code, out, _ := run(t, deps, "update", "--cli")
	got, _ := os.ReadFile(exe)
	if code != 0 || string(got) != "current" || !strings.Contains(out, "already") {
		t.Errorf("exit %d binary %q out %q", code, got, out)
	}
}

// A binary knows only the image it pins, so a plain `update` replaces yawble when a newer release exists and hands the instance to the new
// binary, which knows the new image.
func TestUpdateReplacesYawbleThenHandsTheInstanceToTheNewBinary(t *testing.T) {
	buildinfo.Version = "v0.1.0"
	t.Cleanup(func() { buildinfo.Version = "dev" })
	server := fakeReleaseServer(t, "v0.2.0", []byte("the new yawble"))
	exe := filepath.Join(t.TempDir(), "yawble")
	_ = os.WriteFile(exe, []byte("the old yawble"), 0o755)
	s := engine.NewScripted()
	deps := stubbed(s)
	deps.HTTP, deps.ReleaseBaseURL, deps.GOOS = server.Client(), server.URL, "linux"
	deps.Executable = func() (string, error) { return exe, nil }
	var ran []string
	deps.RunBinary = func(path string, args ...string) (int, error) {
		ran = append([]string{path}, args...)
		return 0, nil
	}
	code, out, errOut := run(t, deps, "update")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if got, _ := os.ReadFile(exe); string(got) != "the new yawble" {
		t.Errorf("binary %q", got)
	}
	if strings.Join(ran, " ") != exe+" update --instance" {
		t.Errorf("the new binary must move the instance: ran %q", ran)
	}
	if len(s.Calls) != 0 {
		t.Errorf("the old binary must leave the instance to the new one: %q", s.Calls)
	}
	if !strings.Contains(out, "v0.1.0 -> v0.2.0") || strings.Contains(out, "run: yawble update") {
		t.Errorf("out %q", out)
	}
}

// The new binary's own exit code is update's: a failed move is not reported as a success.
func TestUpdatePassesOnTheNewBinarysFailure(t *testing.T) {
	buildinfo.Version = "v0.1.0"
	t.Cleanup(func() { buildinfo.Version = "dev" })
	server := fakeReleaseServer(t, "v0.2.0", []byte("new"))
	exe := filepath.Join(t.TempDir(), "yawble")
	_ = os.WriteFile(exe, []byte("old"), 0o755)
	deps := stubbed(engine.NewScripted())
	deps.HTTP, deps.ReleaseBaseURL, deps.GOOS = server.Client(), server.URL, "linux"
	deps.Executable = func() (string, error) { return exe, nil }
	deps.RunBinary = func(string, ...string) (int, error) { return 1, nil }
	if code, _, _ := run(t, deps, "update"); code != 1 {
		t.Errorf("exit %d, want the new binary's 1", code)
	}
}

// Already the latest yawble: update moves the instance itself, as before.
func TestUpdateOnTheLatestYawbleMovesTheInstanceItself(t *testing.T) {
	pinBuild(t) // v0.2.0 pinning 2026.09.25.1
	server := fakeReleaseServer(t, "v0.2.0", []byte("same"))
	old := "ghcr.io/djlsystems/yawble:2026.09.24.1"
	s := engine.NewScripted()
	s.On("podman container inspect", engine.Result{Stdout: "running|" + old + "|" + labelFor(old) + "\n"})
	deps := stubbed(s)
	deps.Env = func(string) string { return "" }
	deps.HTTP, deps.ReleaseBaseURL = server.Client(), server.URL
	deps.RunBinary = func(string, ...string) (int, error) { t.Error("nothing to hand over to"); return 1, nil }
	code, out, errOut := run(t, deps, "update")
	if code != 0 || !strings.Contains(out, "image "+old+" -> "+pinned) {
		t.Errorf("exit %d out %q err %q", code, out, errOut)
	}
}

// A release that cannot be read (offline; private with no token) never blocks the instance.
func TestUpdateStillMovesTheInstanceWhenTheReleaseCannotBeRead(t *testing.T) {
	pinBuild(t)
	old := "ghcr.io/djlsystems/yawble:2026.09.24.1"
	s := engine.NewScripted()
	s.On("podman container inspect", engine.Result{Stdout: "running|" + old + "|" + labelFor(old) + "\n"})
	// The release lookup answers 404 (a private repository with no token looks like that); the
	// instance's health checks keep answering 200.
	missing := httptest.NewServer(http.NotFoundHandler())
	t.Cleanup(missing.Close)
	deps := stubbed(s)
	deps.Env = func(string) string { return "" }
	deps.ReleaseBaseURL = missing.URL
	code, out, errOut := run(t, deps, "update")
	if code != 0 || !strings.Contains(out, "image "+old+" -> "+pinned) || !strings.Contains(errOut, "newer yawble") {
		t.Errorf("exit %d out %q err %q", code, out, errOut)
	}
}

// --instance is what the new binary is run with: it never looks for a release.
func TestUpdateInstanceNeverLooksForARelease(t *testing.T) {
	pinBuild(t)
	s := engine.NewScripted()
	s.On("podman container inspect", engine.Result{Stdout: "running|" + pinned + "|" + labelFor(pinned) + "\n"})
	deps := stubbed(s)
	deps.Env = func(string) string { return "" }
	deps.HTTP = &http.Client{Transport: roundTripFunc(func(*http.Request) (*http.Response, error) {
		t.Error("update --instance asked the network for a release")
		return nil, errors.New("no")
	})}
	if code, out, _ := run(t, deps, "update", "--instance"); code != 0 || !strings.Contains(out, "already") {
		t.Errorf("exit %d out %q", code, out)
	}
}

type roundTripFunc func(*http.Request) (*http.Response, error)

func (f roundTripFunc) RoundTrip(r *http.Request) (*http.Response, error) { return f(r) }

// The stable channel, from config or YAWBLE_CHANNEL, skips pre-releases; with only pre-releases
// published it says so and replaces nothing.
func TestUpdateCliOnTheStableChannelSkipsPreReleases(t *testing.T) {
	buildinfo.Version = "v0.1.0"
	t.Cleanup(func() { buildinfo.Version = "dev" })
	mux := http.NewServeMux()
	mux.HandleFunc("/repos/djlsystems/Yawble/releases", func(w http.ResponseWriter, r *http.Request) {
		_, _ = w.Write([]byte(`[{"tag_name":"v0.2.0","prerelease":true,"assets":[]}]`))
	})
	server := httptest.NewServer(mux)
	t.Cleanup(server.Close)
	dir := t.TempDir()
	exe := filepath.Join(dir, "yawble")
	_ = os.WriteFile(exe, []byte("the old yawble"), 0o755)
	deps := stubbed(engine.NewScripted())
	deps.HTTP, deps.ReleaseBaseURL, deps.GOOS = server.Client(), server.URL, "linux"
	deps.Executable = func() (string, error) { return exe, nil }
	env := map[string]string{"YAWBLE_IMAGE": testImage, "YAWBLE_CHANNEL": "stable"}
	deps.Env = func(k string) string { return env[k] }

	code, out, errOut := run(t, deps, "update", "--cli")
	if code == 0 || !strings.Contains(out+errOut, "no regular release") {
		t.Errorf("exit %d out %q err %q", code, out, errOut)
	}
	if got, _ := os.ReadFile(exe); string(got) != "the old yawble" {
		t.Errorf("the binary was replaced: %q", got)
	}
}
