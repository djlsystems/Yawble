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

// A running worker whose hand run errors on every poll (the engine keeps refusing it) is not
// fatal and never surfaces the engine's raw error: the record stands, up polls on, and it ends
// with today's timeout sentence.
func TestAHandRunThatAlwaysErrorsStillTimesOutWithTheSameSentence(t *testing.T) {
	defer instance.SetPollingForTests(time.Millisecond, 50*time.Millisecond)()
	s := fresh("podman", podmanHealth, 0)
	s.On(podmanHealth+"yawble-worker-1", engine.Result{Stdout: "starting\n"})
	s.On("podman healthcheck run yawble-worker-1", engine.Result{Stderr: "Error: OCI runtime error", ExitCode: 125})
	s.On(engines[0].inspect+"yawble-worker-1", engine.Result{Stdout: "running|x|\n"})
	err := instance.Up(context.Background(), engine.NewPodman(s), keyed(), healthy, &bytes.Buffer{})
	want := "yawble-worker-1 did not connect to control within 50ms; read `yawble logs worker-1`"
	if err == nil || err.Error() != want {
		t.Fatalf("err %v, want %q", err, want)
	}
	n := 0
	for _, c := range s.Calls {
		if strings.HasPrefix(c, "podman healthcheck run yawble-worker-1") {
			n++
		}
	}
	if n < 2 {
		t.Errorf("check run %d times: up stopped polling after the first error", n)
	}
}

// An unhealthy record whose hand run errors stays unhealthy in status (never upgraded, never
// an error), and says no hand run happened.
func TestAnUnhealthyRecordWhoseHandRunErrorsStaysUnhealthy(t *testing.T) {
	s := engine.NewScripted()
	s.On(engines[0].inspect+"yawble-worker-1", engine.Result{Stdout: "running|x|\n"})
	s.On(podmanHealth+"yawble-worker-1", engine.Result{Stdout: "unhealthy\n"})
	s.On("podman healthcheck run yawble-worker-1", engine.Result{Stderr: "Error: boom", ExitCode: 125})
	st := keyed()
	st.Workers = 1
	ws, err := instance.Workers(context.Background(), engine.NewPodman(s), st)
	if err != nil || len(ws) != 1 {
		t.Fatalf("workers %+v err %v", ws, err)
	}
	if ws[0].Health != engine.HealthUnhealthy || ws[0].EngineHealth != "" {
		t.Errorf("worker %+v: want the record (unhealthy) and no hand run said", ws[0])
	}
}
