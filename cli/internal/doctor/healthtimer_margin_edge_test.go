package doctor_test

import (
	"testing"
	"time"

	"github.com/djlsystems/yawble/cli/internal/doctor"
	"github.com/djlsystems/yawble/cli/internal/engine"
)

// ClockSkewMargin widens both signs by exactly its own length and no more: a second inside the
// widened wait is ok, a second past it warns. So a VM clock up to three minutes behind never
// warns, and a timer that has really stopped is still caught a margin later.
func TestHealthTimerMarginIsExactOnBothSigns(t *testing.T) {
	startingAfter := 10*time.Minute + 30*time.Second + doctor.ClockSkewMargin // start period + interval + margin
	overdueAfter := 3*30*time.Second + doctor.ClockSkewMargin                 // three intervals + margin
	cases := []struct {
		name string
		o    doctor.HealthObserved
		want doctor.Verdict
	}{
		{"starting, a second inside the margin", doctor.HealthObserved{Name: "w", Engine: engine.HealthStarting, Ran: engine.HealthHealthy, Times: workerTimes(startingAfter-time.Second, 0)}, doctor.OK},
		{"starting, a second past the margin", doctor.HealthObserved{Name: "w", Engine: engine.HealthStarting, Ran: engine.HealthHealthy, Times: workerTimes(startingAfter+time.Second, 0)}, doctor.Warn},
		{"starting, an hour in", doctor.HealthObserved{Name: "w", Engine: engine.HealthStarting, Ran: engine.HealthHealthy, Times: workerTimes(time.Hour, 0)}, doctor.Warn},
		{"healthy, last check a second inside the margin", doctor.HealthObserved{Name: "yawble", Engine: engine.HealthHealthy, Ran: engine.HealthHealthy, Times: workerTimes(time.Hour, overdueAfter-time.Second)}, doctor.OK},
		{"healthy, never checked since it started a second past the margin", doctor.HealthObserved{Name: "yawble", Engine: engine.HealthHealthy, Ran: engine.HealthHealthy, Times: workerTimes(overdueAfter+time.Second, 0)}, doctor.Warn},
		{"starting an hour in, but the check really fails", doctor.HealthObserved{Name: "w", Engine: engine.HealthStarting, Ran: engine.HealthUnhealthy, Times: workerTimes(time.Hour, 0)}, doctor.OK},
	}
	for _, tc := range cases {
		c, ok := doctor.HealthTimerCheck([]doctor.HealthObserved{tc.o}, "", hcNow)
		if !ok || c.Verdict != tc.want {
			t.Errorf("%s: check %+v ok %v, want %v", tc.name, c, ok, tc.want)
		}
	}
}

// An engine clock ahead of this computer's gives timestamps in the future: never overdue, never
// past the start period, so never a warning (and never a negative duration in a sentence).
func TestHealthTimerWithAnEngineClockAheadIsOK(t *testing.T) {
	for _, o := range []doctor.HealthObserved{
		{Name: "w", Engine: engine.HealthStarting, Ran: engine.HealthHealthy, Times: workerTimes(-5*time.Minute, 0)},
		{Name: "yawble", Engine: engine.HealthHealthy, Ran: engine.HealthHealthy, Times: &engine.HealthTimes{StartedAt: hcNow.Add(-time.Hour), LastCheck: hcNow.Add(5 * time.Minute), StartPeriod: 10 * time.Minute, Interval: 30 * time.Second}},
	} {
		if c, ok := doctor.HealthTimerCheck([]doctor.HealthObserved{o}, "", hcNow); !ok || c.Verdict != doctor.OK {
			t.Errorf("%s: check %+v ok %v", o.Name, c, ok)
		}
	}
}
