package instance_test

import (
	"bytes"
	"context"
	"strings"
	"testing"
	"time"

	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/instance"
)

// A record that says unhealthy while the check, run by hand, passes: the run's answer is taken,
// and the engine's word is kept beside it.
func TestAnUnhealthyRecordWhoseCheckPassesByHandIsHealthy(t *testing.T) {
	s := engine.NewScripted()
	s.OnSequence(podmanHealth+"yawble-worker-1", engine.Result{Stdout: "unhealthy\n"}, engine.Result{Stdout: "healthy\n"})
	h, said, err := instance.CurrentHealth(context.Background(), engine.NewPodman(s), "yawble-worker-1")
	if err != nil || h != engine.HealthHealthy || said != engine.HealthUnhealthy {
		t.Errorf("h %q said %q err %v", h, said, err)
	}
	if n := countPrefix(s.Calls, "podman healthcheck run yawble-worker-1"); n != 1 {
		t.Errorf("check run %d times, want once", n)
	}
}

// A container Podman says has no defined check after all: the engine's record stands, and no
// hand run is claimed.
func TestACheckThatCannotBeRunLeavesTheRecord(t *testing.T) {
	s := engine.NewScripted()
	s.On(podmanHealth+"yawble-worker-1", engine.Result{Stdout: "starting\n"})
	s.On("podman healthcheck run yawble-worker-1", engine.Result{Stderr: "Error: container yawble-worker-1 has no defined healthcheck", ExitCode: 125})
	h, said, err := instance.CurrentHealth(context.Background(), engine.NewPodman(s), "yawble-worker-1")
	if err != nil || h != engine.HealthStarting || said != "" {
		t.Errorf("h %q said %q err %v", h, said, err)
	}
}

// A record that cannot be read is the error, and no check is run on top of it.
func TestARecordThatCannotBeReadRunsNoCheck(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman container inspect --format", engine.Result{Stderr: "Error: cannot connect to Podman", ExitCode: 125})
	_, _, err := instance.CurrentHealth(context.Background(), engine.NewPodman(s), "yawble-worker-1")
	if err == nil {
		t.Error("an unreadable record answered no error")
	}
	if n := countPrefix(s.Calls, "podman healthcheck run"); n != 0 {
		t.Errorf("ran the check %d times", n)
	}
}

// up on Docker with a worker that reads unhealthy until the daemon's next check: it waits for the
// daemon, never runs anything by hand, and reports the worker connected once the record is healthy.
func TestDockerUpWaitsForTheDaemonWithoutRunningTheCheck(t *testing.T) {
	defer instance.SetPollingForTests(time.Millisecond, time.Minute)()
	health := engines[1].health
	s := fresh("docker", health, 0)
	unhealthy := engine.Result{Stdout: "unhealthy\n"}
	s.OnSequence(health+"yawble-worker-1", unhealthy, unhealthy, unhealthy, engine.Result{Stdout: "healthy\n"})
	var out bytes.Buffer
	if err := instance.Up(context.Background(), engine.NewDocker(s), keyed(), healthy, &out); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(out.String(), "yawble-worker-1 is connected to control\n") {
		t.Errorf("out %q", out.String())
	}
	if n := countPrefix(s.Calls, health+"yawble-worker-1"); n < 4 {
		t.Errorf("record read %d times, want once a poll until healthy", n)
	}
	for _, c := range s.Calls {
		if strings.Contains(c, "healthcheck") {
			t.Errorf("docker ran %q", c)
		}
	}
}
