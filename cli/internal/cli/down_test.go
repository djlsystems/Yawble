package cli_test

import (
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/engine"
)

func indexOf(calls []string, prefix string) int {
	for i, c := range calls {
		if strings.HasPrefix(c, prefix) {
			return i
		}
	}
	return -1
}

// On Podman the port is published by the pod's infra container, not by yawble: stopping only the
// container left 8080 held, and the next `up` offered 8081 for a port that was yawble's own.
func TestDownStopsThePodSoItsPortIsFreed(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman container inspect", engine.Result{Stdout: "running|" + testImage + "|" + currentLabel() + "\n"})
	// Podman's answer for a pod whose container runs; after `podman stop yawble` it is "Degraded".
	s.On("podman pod inspect yawble --format {{.State}}", engine.Result{Stdout: "Degraded\n"})
	s.On("podman pod inspect yawble --format {{.InfraContainerID}}", engine.Result{Stdout: "0a1b2c3d\n"})
	deps := stubbed(s)
	deps.LookPath = lookPath("podman")

	code, out, errOut := run(t, deps, "down")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	stop, infra := indexOf(s.Calls, "podman stop yawble"), indexOf(s.Calls, "podman stop 0a1b2c3d")
	if stop < 0 || infra < 0 || infra < stop {
		t.Errorf("want the container stopped, then the pod's infra container:\n%s", strings.Join(s.Calls, "\n"))
	}
	if !strings.Contains(out, "port 8080 is free") {
		t.Errorf("out %q", out)
	}
}

// A pod left running by an earlier `down` is stopped even though the container already is.
func TestDownStopsALeftoverPodWhenTheContainerIsAlreadyStopped(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman container inspect", engine.Result{Stdout: "exited|" + testImage + "|" + currentLabel() + "\n"})
	s.On("podman pod inspect yawble --format {{.State}}", engine.Result{Stdout: "Degraded\n"})
	s.On("podman pod inspect yawble --format {{.InfraContainerID}}", engine.Result{Stdout: "0a1b2c3d\n"})
	deps := stubbed(s)
	deps.LookPath = lookPath("podman")

	if code, out, errOut := run(t, deps, "down"); code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	// `podman pod stop` leaves the infra running once the member container has exited (measured
	// on Podman 6.0.2): the infra container itself is what must be stopped.
	if indexOf(s.Calls, "podman stop 0a1b2c3d") < 0 {
		t.Errorf("the leftover pod was not stopped:\n%s", strings.Join(s.Calls, "\n"))
	}
}

func TestDownOnDockerHasNoPodToStop(t *testing.T) {
	s := engine.NewScripted()
	s.On("docker container inspect", engine.Result{Stdout: "running|" + testImage + "|" + currentLabel() + "\n"})
	deps := stubbed(s)
	deps.LookPath = lookPath("docker")

	if code, out, errOut := run(t, deps, "down"); code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if indexOf(s.Calls, "docker stop yawble") < 0 || strings.Contains(strings.Join(s.Calls, "\n"), "pod") {
		t.Errorf("calls:\n%s", strings.Join(s.Calls, "\n"))
	}
}

// yawble's own running pod holding the port is not another program: `up` reuses it and asks nothing.
func TestUpTreatsItsOwnRunningPodAsItsOwnPort(t *testing.T) {
	s := upScript()
	s.On("podman pod inspect yawble --format {{.State}}", engine.Result{Stdout: "Running\n"})
	deps := stubbed(s)
	deps.LookPath, deps.Interactive = lookPath("podman"), false
	deps.PortFree = takenBy(8080)

	code, out, errOut := run(t, deps, "up")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if strings.Contains(out, "in use") || strings.Contains(strings.Join(s.Calls, "\n"), "8081") {
		t.Errorf("out %q calls:\n%s", out, strings.Join(s.Calls, "\n"))
	}
}
