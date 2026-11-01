package cli_test

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/djlsystems/yawble/cli/internal/doctor"
	"github.com/djlsystems/yawble/cli/internal/engine"
)

func installingReport(t *testing.T) string {
	t.Helper()
	raw, err := os.ReadFile(filepath.Join("..", "doctor", "testdata", "host-doctor-installs.json"))
	if err != nil {
		t.Fatal(err)
	}
	return string(raw)
}

// A fresh volume: control answers, the worker connects, and its agents are still installing. `up`
// prints their progress in a person's words for a short, bounded time, then says where the board is and
// that `yawble status` shows when they are ready - and exits 0. Still installing is never a failure.
func TestUpReturnsOnceControlAnswersAndSaysTheAgentsAreStillInstalling(t *testing.T) {
	bound, interval := doctor.AgentsWaitBound, doctor.AgentsWaitInterval
	doctor.AgentsWaitBound, doctor.AgentsWaitInterval = 50*time.Millisecond, time.Millisecond
	t.Cleanup(func() { doctor.AgentsWaitBound, doctor.AgentsWaitInterval = bound, interval })

	s := upScript()
	s.On(doctorExec, engine.Result{Stdout: installingReport(t)})
	deps := stubbed(s)
	deps.LookPath, deps.Interactive = lookPath("podman"), false

	started := time.Now()
	code, out, errOut := run(t, deps, "up", "--no-browser")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if time.Since(started) > 10*time.Second {
		t.Errorf("up took %s", time.Since(started))
	}
	for _, want := range []string{
		"worker worker-1: getting agents ready (claude installing, codex failed, copilot done, google-chrome 40%)",
		"; agents are still being installed - `yawble status` shows when they are ready",
	} {
		if !strings.Contains(out, want) {
			t.Errorf("no %q in:\n%s", want, out)
		}
	}
	if strings.Count(out, "getting agents ready") != 1 {
		t.Errorf("the same progress was said more than once:\n%s", out)
	}
	if strings.Contains(out, "a first start installs the agent CLIs") {
		t.Errorf("up still says a first start installs the CLIs before the worker connects:\n%s", out)
	}
}

// `yawble status` shows each agent's install state from control's record, and never says "not
// installed" for one that is installing.
func TestStatusShowsEachAgentsInstallState(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman version", engine.Result{Stdout: "5.4.0\n"})
	s.On("podman container inspect", engine.Result{Stdout: "running|" + testImage + "|" + currentLabel() + "\n"})
	s.On(doctorExec, engine.Result{Stdout: installingReport(t)})
	deps := stubbed(s)
	deps.LookPath = lookPath("podman")

	code, out, errOut := run(t, deps, "status")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if !strings.Contains(out, "agents     claude installing, codex failed, copilot done, google-chrome 40% (still being installed; runs of those agents wait for them)") {
		t.Errorf("status:\n%s", out)
	}
	if strings.Contains(out, "claude not installed") {
		t.Errorf("status says an installing agent is not installed:\n%s", out)
	}

	code, out, _ = run(t, deps, "status", "--json")
	if code != 0 || !strings.Contains(out, `"agents": {`) || !strings.Contains(out, `"state": "installing"`) {
		t.Errorf("status --json:\n%s", out)
	}
}
