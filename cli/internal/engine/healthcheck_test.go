package engine_test

import (
	"context"
	"strings"
	"testing"
	"time"

	"github.com/djlsystems/yawble/cli/internal/engine"
)

// Podman runs the check by hand and reads its record after: a pass is healthy at once. A failing
// run is what Podman printed (starting inside the start period), else unhealthy; no check is none.
func TestPodmanRunHealthCheckRunsItAndReadsTheRecord(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman container inspect --format {{.State.Health.Status}} w", engine.Result{Stdout: "healthy\n"})
	h, err := engine.NewPodman(s).RunHealthCheck(context.Background(), "w")
	if err != nil || h != engine.HealthHealthy {
		t.Fatalf("h %q err %v", h, err)
	}
	if got := strings.Join(s.Calls, "\n"); got != "podman healthcheck run w\npodman container inspect --format {{.State.Health.Status}} w" {
		t.Errorf("calls:\n%s", got)
	}
	for _, c := range []struct {
		res  engine.Result
		want engine.Health
	}{
		{engine.Result{Stdout: "starting\n", ExitCode: 1}, engine.HealthStarting},
		{engine.Result{Stdout: "unhealthy\n", ExitCode: 1}, engine.HealthUnhealthy},
		{engine.Result{ExitCode: 1}, engine.HealthUnhealthy},
		{engine.Result{Stderr: "Error: container w has no defined healthcheck", ExitCode: 125}, engine.HealthNone},
		{engine.Result{Stderr: "Error: no such container w", ExitCode: 125}, engine.HealthNone},
	} {
		s := engine.NewScripted()
		s.On("podman healthcheck run w", c.res)
		if h, err := engine.NewPodman(s).RunHealthCheck(context.Background(), "w"); err != nil || h != c.want {
			t.Errorf("%+v: h %q err %v, want %q", c.res, h, err, c.want)
		}
	}
}

// Docker runs its checks in the daemon: the record is read, nothing is run.
func TestDockerRunHealthCheckIsTheDaemonsRecord(t *testing.T) {
	s := engine.NewScripted()
	s.On("docker container inspect --format {{if .State.Health}}{{.State.Health.Status}}{{end}} w", engine.Result{Stdout: "starting\n"})
	if h, err := engine.NewDocker(s).RunHealthCheck(context.Background(), "w"); err != nil || h != engine.HealthStarting {
		t.Errorf("h %q err %v", h, err)
	}
	if len(s.Calls) != 1 || strings.Contains(s.Calls[0], "healthcheck") {
		t.Errorf("calls %q", s.Calls)
	}
}

// The inspect document has the same shape on both engines; an older Podman names the log
// State.Healthcheck. Durations are nanoseconds; no log is a check never run.
func TestHealthTimesReadsTheInspectDocumentOnBothEngines(t *testing.T) {
	doc := `[{"State":{"StartedAt":"2026-11-04T11:45:00.123456789Z","Health":{"Status":"healthy","Log":[{"Start":"2026-11-04T11:50:00.5Z"},{"Start":"2026-11-04T11:59:30Z"}]}},"Config":{"Healthcheck":{"Test":["CMD-SHELL","x"],"StartPeriod":600000000000,"Interval":30000000000}}}]`
	old := `[{"State":{"StartedAt":"2026-11-04T11:45:00Z","Healthcheck":{"Status":"starting","Log":null}},"Config":{"Healthcheck":{"StartPeriod":60000000000}}}]`
	for _, c := range []struct {
		program string
		make    func(engine.Runner) engine.Engine
	}{{"podman", engine.NewPodman}, {"docker", engine.NewDocker}} {
		s := engine.NewScripted()
		s.On(c.program+" container inspect w", engine.Result{Stdout: doc})
		s.On(c.program+" container inspect old", engine.Result{Stdout: old})
		e := c.make(s)
		got, err := e.HealthTimes(context.Background(), "w")
		if err != nil {
			t.Fatal(err)
		}
		if !got.StartedAt.Equal(time.Date(2026, 11, 4, 11, 45, 0, 123456789, time.UTC)) || got.StartPeriod != 10*time.Minute || got.Interval != 30*time.Second || !got.LastCheck.Equal(time.Date(2026, 11, 4, 11, 59, 30, 0, time.UTC)) {
			t.Errorf("%s: %+v", c.program, got)
		}
		got, err = e.HealthTimes(context.Background(), "old")
		if err != nil || !got.LastCheck.IsZero() || got.StartPeriod != time.Minute || got.Every() != 30*time.Second {
			t.Errorf("%s old: %+v %v", c.program, got, err)
		}
	}
}
