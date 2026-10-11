package cli_test

import (
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/engine"
)

// takenBy answers a port probe: every port in taken is in use, every other one is free.
func takenBy(taken ...int) func(int) bool {
	return func(port int) bool {
		for _, t := range taken {
			if t == port {
				return false
			}
		}
		return true
	}
}

func TestUpOffersTheNextFreePortWhenAnotherProgramHoldsIt(t *testing.T) {
	s := upScript()
	deps := stubbed(s)
	deps.LookPath, deps.Interactive = lookPath("podman"), true
	deps.PortFree = takenBy(8080, 8081)
	deps.Stdin = strings.NewReader("y\n")
	dir := t.TempDir()
	deps.ConfigDir = dir

	code, out, errOut := run(t, deps, "up", "--no-browser")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if !strings.Contains(out, "Port 8080 is in use by another program. Use port 8082 instead?") {
		t.Errorf("out %q", out)
	}
	if !strings.Contains(calls(s), "-p 0.0.0.0:8082:8080") {
		t.Errorf("not published on 8082:\n%s", calls(s))
	}
	saved, _ := os.ReadFile(filepath.Join(dir, "config.toml"))
	if !strings.Contains(string(saved), "8082") {
		t.Errorf("the port was not saved: %q", saved)
	}
}

func TestUpWithYesTakesTheNextFreePortWithoutAsking(t *testing.T) {
	s := upScript()
	deps := stubbed(s)
	deps.LookPath = lookPath("podman")
	deps.PortFree = takenBy(8080)

	code, out, errOut := run(t, deps, "up", "--yes", "--no-browser")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if !strings.Contains(calls(s), "-p 0.0.0.0:8081:8080") || !strings.Contains(out, "using port 8081") {
		t.Errorf("out %q calls:\n%s", out, calls(s))
	}
}

func TestUpRefusesATakenPortWithoutAYesAndCreatesNothing(t *testing.T) {
	s := upScript()
	deps := stubbed(s)
	deps.LookPath, deps.Interactive = lookPath("podman"), false
	deps.PortFree = takenBy(8080)

	code, out, errOut := run(t, deps, "up")
	if code == 0 {
		t.Fatalf("a taken port was accepted: %s", out)
	}
	if !strings.Contains(errOut+out, "8080") || !strings.Contains(errOut+out, "--yes") {
		t.Errorf("out %q err %q", out, errOut)
	}
	for _, verb := range []string{"pod create", "volume create", "podman run", "podman start"} {
		if strings.Contains(calls(s), verb) {
			t.Errorf("%q ran before the port was settled:\n%s", verb, calls(s))
		}
	}
}

func TestUpDeclinedPortChangeCreatesNothingAndNamesTheSetting(t *testing.T) {
	s := upScript()
	deps := stubbed(s)
	deps.LookPath, deps.Interactive = lookPath("podman"), true
	deps.PortFree = takenBy(8080)
	deps.Stdin = strings.NewReader("n\n")

	code, out, errOut := run(t, deps, "up")
	if code == 0 || !strings.Contains(errOut+out, "yawble config set port") {
		t.Errorf("exit %d out %q err %q", code, out, errOut)
	}
	if strings.Contains(calls(s), "pod create") || strings.Contains(calls(s), "podman start") {
		t.Errorf("created after a no:\n%s", calls(s))
	}
}

// Yawble's own running instance answers on its port: that is not a conflict to ask about.
func TestUpDoesNotMistakeItsOwnRunningInstanceForAnotherProgram(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman container inspect", engine.Result{Stdout: "running|" + testImage + "|" + currentLabel() + "\n"})
	deps := stubbed(s)
	deps.LookPath, deps.Interactive = lookPath("podman"), false
	deps.PortFree = takenBy(8080)

	code, out, errOut := run(t, deps, "up")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if strings.Contains(out, "in use") || strings.Contains(calls(s), "8081") {
		t.Errorf("out %q calls:\n%s", out, calls(s))
	}
}
