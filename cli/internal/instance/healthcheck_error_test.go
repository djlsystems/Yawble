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

// D1: a hand run that errors (the worker stopped between the record read and the run, so Podman
// answers exit 125 "is not running") must not be fatal: the engine's record stands.
func TestARunHealthCheckErrorKeepsTheEnginesRecord(t *testing.T) {
	s := engine.NewScripted()
	s.On(podmanHealth+"yawble-worker-1", engine.Result{Stdout: "starting\n"})
	s.On("podman healthcheck run yawble-worker-1", engine.Result{Stderr: "Error: container yawble-worker-1 is not running", ExitCode: 125})
	h, said, err := instance.CurrentHealth(context.Background(), engine.NewPodman(s), "yawble-worker-1")
	if err != nil || h != engine.HealthStarting || said != "" {
		t.Errorf("h %q said %q err %v: want the record (starting), no error", h, said, err)
	}
}

// D1 through status: one worker's hand run erroring fails the whole of Workers (so GetStatus,
// `yawble status`, and doctor's Observe, which then drops ContainerKnown).
func TestStatusSurvivesAHandRunThatErrors(t *testing.T) {
	s := engine.NewScripted()
	s.On(engines[0].inspect+"yawble-worker-1", engine.Result{Stdout: "running|x|\n"})
	s.On(podmanHealth+"yawble-worker-1", engine.Result{Stdout: "starting\n"})
	s.On("podman healthcheck run yawble-worker-1", engine.Result{Stderr: "Error: container yawble-worker-1 is not running", ExitCode: 125})
	st := keyed()
	st.Workers = 1
	if _, err := instance.Workers(context.Background(), engine.NewPodman(s), st); err != nil {
		t.Errorf("Workers failed on a hand run error: %v", err)
	}
}

// D1 through up: a hand run that errors once, then passes, must not abort up with Podman's raw
// error; up keeps polling and reports the worker connected.
func TestUpKeepsWaitingThroughAHandRunThatErrorsOnce(t *testing.T) {
	defer instance.SetPollingForTests(time.Millisecond, time.Minute)()
	s := fresh("podman", podmanHealth, 0)
	starting := engine.Result{Stdout: "starting\n"}
	s.OnSequence(podmanHealth+"yawble-worker-1", starting, starting, engine.Result{Stdout: "healthy\n"})
	s.OnSequence("podman healthcheck run yawble-worker-1",
		engine.Result{Stderr: "Error: container yawble-worker-1 is not running", ExitCode: 125},
		engine.Result{Stdout: "healthy\n"})
	var out bytes.Buffer
	if err := instance.Up(context.Background(), engine.NewPodman(s), keyed(), healthy, &out); err != nil {
		t.Fatalf("up aborted: %v", err)
	}
	if !strings.Contains(out.String(), "yawble-worker-1 is connected to control\n") {
		t.Errorf("out %q", out.String())
	}
}

// D2: Docker's status of an unhealthy or starting worker is the daemon's record: nothing is run, and
// EngineHealth stays empty because no check was run here.
func TestDockerStatusIsTheDaemonsRecordAndRunsNothing(t *testing.T) {
	for _, rec := range []string{"starting", "unhealthy"} {
		s := engine.NewScripted()
		s.On(engines[1].inspect+"yawble-worker-1", engine.Result{Stdout: "running|x|\n"})
		s.On(engines[1].health+"yawble-worker-1", engine.Result{Stdout: rec + "\n"})
		st := keyed()
		st.Workers = 1
		ws, err := instance.Workers(context.Background(), engine.NewDocker(s), st)
		if err != nil || len(ws) != 1 {
			t.Fatalf("%s: workers %+v err %v", rec, ws, err)
		}
		if string(ws[0].Health) != rec || ws[0].EngineHealth != "" {
			t.Errorf("%s: worker %+v", rec, ws[0])
		}
		for _, c := range s.Calls {
			if strings.Contains(c, "healthcheck") {
				t.Errorf("%s: docker ran %q", rec, c)
			}
		}
	}
}

// D1, the stopped worker: Podman's record of a worker that has exited still says starting, and
// `podman healthcheck run` on it answers exit 125 "is not running". up must still say today's
// stopped sentence, not Podman's raw error.
func TestAWorkerThatStopsStillGetsTheStoppedSentence(t *testing.T) {
	defer instance.SetPollingForTests(time.Millisecond, time.Minute)()
	s := fresh("podman", podmanHealth, 0)
	s.On(podmanHealth+"yawble-worker-1", engine.Result{Stdout: "starting\n"})
	s.On("podman healthcheck run yawble-worker-1", engine.Result{Stderr: "Error: container yawble-worker-1 is not running", ExitCode: 125})
	s.On(engines[0].inspect+"yawble-worker-1", engine.Result{Stdout: "exited|x|\n"})
	s.On("podman container inspect --format {{.State.Status}} yawble-worker-1", engine.Result{Stdout: "exited\n"})
	err := instance.Up(context.Background(), engine.NewPodman(s), keyed(), healthy, &bytes.Buffer{})
	want := "yawble-worker-1 stopped before it connected to control; the reason is in `yawble logs worker-1`, and `yawble up` starts it again"
	if err == nil || err.Error() != want {
		t.Errorf("err %v, want %q", err, want)
	}
}
