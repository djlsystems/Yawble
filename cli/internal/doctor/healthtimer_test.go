package doctor_test

import (
	"strings"
	"testing"
	"time"

	"github.com/djlsystems/yawble/cli/internal/doctor"
	"github.com/djlsystems/yawble/cli/internal/engine"
)

var hcNow = time.Date(2026, 11, 4, 12, 0, 0, 0, time.UTC)

// The worker image's check: interval 30s, start period 10m.
func workerTimes(startedAgo, lastAgo time.Duration) *engine.HealthTimes {
	t := &engine.HealthTimes{StartedAt: hcNow.Add(-startedAgo), StartPeriod: 10 * time.Minute, Interval: 30 * time.Second}
	if lastAgo > 0 {
		t.LastCheck = hcNow.Add(-lastAgo)
	}
	return t
}

// The engine's timer never fires: the record still says starting past the start period, and the
// check run by hand passes. doctor warns in plain words, names the fix found, and says up works
// around it.
func TestHealthTimerWarnsWhenTheEngineNeverRunsTheCheck(t *testing.T) {
	obs := []doctor.HealthObserved{{Name: "yawble-worker-1", Engine: engine.HealthStarting, Ran: engine.HealthHealthy, Times: workerTimes(15*time.Minute, 0)}}
	fix := doctor.LingerFix("user", false, true, "podman-machine-default")
	c, ok := doctor.HealthTimerCheck(obs, fix, hcNow)
	if !ok || c.Verdict != doctor.Warn {
		t.Fatalf("check %+v ok %v", c, ok)
	}
	for _, want := range []string{
		"the engine is not running health checks on its own here",
		"yawble-worker-1 still read starting 15m0s after it started (its start period is 10m0s), and its check, run by hand, passes",
	} {
		if !strings.Contains(c.Detail, want) {
			t.Errorf("detail %q lacks %q", c.Detail, want)
		}
	}
	for _, want := range []string{
		"lingering is off for user in the Podman machine podman-machine-default",
		"`podman machine ssh podman-machine-default sudo loginctl enable-linger user`",
		"`yawble up` runs the checks itself and is not held up",
	} {
		if !strings.Contains(c.Fix, want) {
			t.Errorf("fix %q lacks %q", c.Fix, want)
		}
	}

	// No fix found: it says so, and still that up works around it.
	c, _ = doctor.HealthTimerCheck(obs, doctor.LingerFix("user", true, true, "podman-machine-default"), hcNow)
	if c.Verdict != doctor.Warn || !strings.Contains(c.Fix, "no fix was found from here") || !strings.Contains(c.Fix, "`yawble up` runs the checks itself") {
		t.Errorf("check %+v", c)
	}
}

// After up ran the check by hand the record says healthy, but the engine has not run it since:
// still the timer, and still said.
func TestHealthTimerWarnsWhenAHealthyRecordIsNoLongerChecked(t *testing.T) {
	obs := []doctor.HealthObserved{{Name: "yawble", Engine: engine.HealthHealthy, Ran: engine.HealthHealthy, Times: workerTimes(20*time.Minute, 18*time.Minute)}}
	c, ok := doctor.HealthTimerCheck(obs, "", hcNow)
	if !ok || c.Verdict != doctor.Warn || !strings.Contains(c.Detail, "yawble's check was last run 18m0s ago, although it is due every 30s, and run by hand it passes") {
		t.Errorf("check %+v", c)
	}
}

// The timer fires: the record is healthy and was checked within an interval. ok, never warn.
func TestHealthTimerIsOKWhenTheEngineRunsTheCheck(t *testing.T) {
	obs := []doctor.HealthObserved{
		{Name: "yawble", Engine: engine.HealthHealthy, Times: workerTimes(20*time.Minute, 10*time.Second)},
		{Name: "yawble-worker-1", Engine: engine.HealthHealthy, Times: workerTimes(20*time.Minute, 25*time.Second)},
	}
	c, ok := doctor.HealthTimerCheck(obs, "", hcNow)
	if !ok || c.Verdict != doctor.OK || c.Detail != "the engine runs each container's health check on its own" {
		t.Errorf("check %+v ok %v", c, ok)
	}
}

// Not the timer: still inside the start period (the check has not come round), a check that
// really fails, or times that could not be read.
func TestHealthTimerDoesNotWarnWithoutBothSigns(t *testing.T) {
	cases := map[string]doctor.HealthObserved{
		"inside the start period": {Name: "w", Engine: engine.HealthStarting, Ran: engine.HealthHealthy, Times: workerTimes(5*time.Minute, 0)},
		"the check really fails":  {Name: "w", Engine: engine.HealthStarting, Ran: engine.HealthUnhealthy, Times: workerTimes(15*time.Minute, 0)},
	}
	for name, o := range cases {
		if c, ok := doctor.HealthTimerCheck([]doctor.HealthObserved{o}, "", hcNow); !ok || c.Verdict != doctor.OK {
			t.Errorf("%s: check %+v ok %v", name, c, ok)
		}
	}
	if _, ok := doctor.HealthTimerCheck([]doctor.HealthObserved{{Name: "w", Engine: engine.HealthStarting, Ran: engine.HealthHealthy}}, "", hcNow); ok {
		t.Error("a row without any times measured")
	}
}

func TestLingerFixOnlyWhenLingeringIsMeasuredOff(t *testing.T) {
	if got := doctor.LingerFix("agent", false, true, ""); !strings.Contains(got, "lingering is off for agent on this computer") || !strings.Contains(got, "`sudo loginctl enable-linger agent`") {
		t.Errorf("local fix %q", got)
	}
	for _, got := range []string{doctor.LingerFix("u", true, true, "m"), doctor.LingerFix("u", false, false, "m"), doctor.LingerFix("root", false, true, "")} {
		if got != "" {
			t.Errorf("fix %q, want none", got)
		}
	}
}
