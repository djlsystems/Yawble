package engine

import (
	"context"
	"encoding/json"
	"fmt"
	"strings"
	"time"
)

// HealthTimes is what the engine records about a container's health check over time: when the
// container started, the check's start period and interval, and when the check last ran (zero
// when it never has). It is how doctor tells a check the engine never runs on its own from one
// that simply has not come round yet.
type HealthTimes struct {
	StartedAt   time.Time
	StartPeriod time.Duration
	Interval    time.Duration
	LastCheck   time.Time
}

// DefaultHealthInterval is the interval both engines use for a HEALTHCHECK that names none.
const DefaultHealthInterval = 30 * time.Second

// Every answers the check's interval, the engines' default when the image names none.
func (t HealthTimes) Every() time.Duration {
	if t.Interval <= 0 {
		return DefaultHealthInterval
	}
	return t.Interval
}

// RunHealthCheck on Podman runs the check now (`podman healthcheck run`) and answers the engine's
// record after it, which the run has just written: a passing run is healthy at once. Rootless
// Podman otherwise runs a check only from a systemd --user timer, and that timer does not fire
// everywhere. A failing run answers what Podman printed (starting inside the start period,
// unhealthy after it); a container with no check, or none at all, is none.
func (p podman) RunHealthCheck(ctx context.Context, name string) (Health, error) {
	res, err := p.r.Run(ctx, "podman", "healthcheck", "run", name)
	if err != nil {
		return HealthNone, &NotRunnable{Program: "podman", Err: err}
	}
	if res.ExitCode == 0 {
		return p.Health(ctx, name)
	}
	lower := strings.ToLower(res.Stderr)
	switch {
	case strings.Contains(lower, "no such"), strings.Contains(lower, "no defined healthcheck"), strings.Contains(lower, "has no healthcheck"):
		return HealthNone, nil
	case res.ExitCode == 1:
		if h := healthOf(res.Stdout); h == HealthStarting {
			return h, nil
		}
		return HealthUnhealthy, nil
	}
	return HealthNone, fmt.Errorf("podman healthcheck run %s: %s (exit %d)", name, strings.TrimSpace(res.Stderr), res.ExitCode)
}

// RunHealthCheck on Docker runs nothing and answers none, "cannot be run here": Docker runs every
// check in its daemon, on time, and has no verb to run one by hand, so the daemon's record is the
// answer and nobody may say a check was run by hand.
func (d docker) RunHealthCheck(ctx context.Context, name string) (Health, error) {
	return HealthNone, nil
}

func (p podman) HealthTimes(ctx context.Context, name string) (HealthTimes, error) {
	return healthTimes(ctx, p.r, "podman", name)
}

func (d docker) HealthTimes(ctx context.Context, name string) (HealthTimes, error) {
	return healthTimes(ctx, d.r, "docker", name)
}

// healthTimes reads the whole inspect document, the same shape on both engines: State.StartedAt,
// Config.Healthcheck's StartPeriod and Interval in nanoseconds, and State.Health.Log (an older
// Podman names it State.Healthcheck).
func healthTimes(ctx context.Context, r Runner, program, name string) (HealthTimes, error) {
	res, err := r.Run(ctx, program, "container", "inspect", name)
	if err != nil {
		return HealthTimes{}, &NotRunnable{Program: program, Err: err}
	}
	if res.ExitCode != 0 {
		return HealthTimes{}, fmt.Errorf("%s container inspect %s: %s (exit %d)", program, name, strings.TrimSpace(res.Stderr), res.ExitCode)
	}
	return healthTimesOf(res.Stdout)
}

type healthLog struct {
	Log []struct {
		Start string `json:"Start"`
	} `json:"Log"`
}

func healthTimesOf(stdout string) (HealthTimes, error) {
	var docs []struct {
		State struct {
			StartedAt   string     `json:"StartedAt"`
			Health      *healthLog `json:"Health"`
			Healthcheck *healthLog `json:"Healthcheck"`
		} `json:"State"`
		Config struct {
			Healthcheck *struct {
				StartPeriod int64 `json:"StartPeriod"`
				Interval    int64 `json:"Interval"`
			} `json:"Healthcheck"`
		} `json:"Config"`
	}
	if err := json.Unmarshal([]byte(stdout), &docs); err != nil || len(docs) == 0 {
		return HealthTimes{}, fmt.Errorf("the engine's inspect answer could not be read")
	}
	doc := docs[0]
	var t HealthTimes
	t.StartedAt, _ = time.Parse(time.RFC3339Nano, doc.State.StartedAt)
	if hc := doc.Config.Healthcheck; hc != nil {
		t.StartPeriod, t.Interval = time.Duration(hc.StartPeriod), time.Duration(hc.Interval)
	}
	log := doc.State.Health
	if log == nil {
		log = doc.State.Healthcheck
	}
	if log != nil {
		for _, entry := range log.Log {
			if at, err := time.Parse(time.RFC3339Nano, entry.Start); err == nil && at.After(t.LastCheck) {
				t.LastCheck = at
			}
		}
	}
	return t, nil
}
