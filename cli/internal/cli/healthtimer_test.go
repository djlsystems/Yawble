package cli_test

import (
	"strings"
	"testing"
	"time"

	"github.com/djlsystems/yawble/cli/internal/engine"
)

var timerNow = time.Date(2026, 11, 4, 12, 0, 0, 0, time.UTC)

// inspectDoc is a worker's whole inspect document: started 15 minutes ago, the worker image's
// check (start period 10m, interval 30s), and its last check lastAgo ago (never when 0).
func inspectDoc(lastAgo time.Duration) string {
	log := "null"
	if lastAgo > 0 {
		log = `[{"Start":"` + timerNow.Add(-lastAgo).Format(time.RFC3339Nano) + `"}]`
	}
	return `[{"State":{"StartedAt":"` + timerNow.Add(-15*time.Minute).Format(time.RFC3339Nano) + `","Health":{"Log":` + log + `}},"Config":{"Healthcheck":{"StartPeriod":600000000000,"Interval":30000000000}}}]`
}

// The engine's timer never fires: worker-1's record stays starting until doctor's own run of the
// check flips it. doctor shows the worker as its check says, warns that the engine is not running
// checks here, names lingering as the fix, and says up works around it.
func TestDoctorWarnsWhenTheEnginesHealthCheckTimerDoesNotFire(t *testing.T) {
	// Registered before runningScript's own lines: on equal prefixes the first registered answers.
	s := engine.NewScripted()
	s.OnSequence(healthLine("podman", "yawble-worker-1"), engine.Result{Stdout: "starting\n"}, engine.Result{Stdout: "healthy\n"})
	withRunning(s)
	s.On(doctorExec, engine.Result{Stdout: doctorStdout})
	s.On("podman container inspect yawble-worker-1", engine.Result{Stdout: inspectDoc(0)})
	s.On("id -un", engine.Result{Stdout: "agent\n"})
	s.On("loginctl show-user agent --property=Linger --value", engine.Result{Stdout: "no\n"})
	deps := stubbed(s)
	deps.Now = func() time.Time { return timerNow }
	_, out, _ := run(t, deps, "doctor")
	for _, want := range []string{
		"health checks",
		"the engine is not running health checks on its own here: yawble-worker-1 still read starting 15m0s after it started (its start period is 10m0s), and its check, run by hand, passes",
		"lingering is off for agent on this computer",
		"`sudo loginctl enable-linger agent`",
		"`yawble up` runs the checks itself and is not held up",
	} {
		if !strings.Contains(out, want) {
			t.Errorf("output lacks %q:\n%s", want, out)
		}
	}
	if !strings.Contains(strings.Join(s.Calls, "\n"), "podman healthcheck run yawble-worker-1") {
		t.Error("the check was not run")
	}
	if strings.Contains(out, "starting") && !strings.Contains(out, "still read starting") {
		t.Errorf("a worker shown as starting:\n%s", out)
	}
}

// The timer fires: the record is healthy and checked 10s ago. No check is run by hand, no
// lingering is asked, and the row is ok - on Podman and on Docker alike.
func TestDoctorHealthChecksRowIsOKWhenTheTimerFires(t *testing.T) {
	for _, program := range []string{"podman", "docker"} {
		s := runningScript()
		if program == "docker" {
			s = engine.NewScripted()
			s.On("docker version", engine.Result{Stdout: "27.0.0\n"})
			s.On("docker container inspect", engine.Result{Stdout: "running|" + testImage + "|" + currentLabel() + "\n"})
			scriptWorker(s, "docker", "running", settingsForImage(testImage), 1)
		}
		s.On(program+" container inspect yawble-worker-1", engine.Result{Stdout: inspectDoc(10 * time.Second)})
		deps := stubbed(s)
		deps.Env = func(k string) string {
			return map[string]string{"YAWBLE_IMAGE": testImage, "YAWBLE_ENGINE": program}[k]
		}
		deps.Now = func() time.Time { return timerNow }
		_, out, _ := run(t, deps, "doctor", "--details")
		if !strings.Contains(out, "ok    health checks  the engine runs each container's health check on its own") {
			t.Errorf("%s: output lacks the ok row:\n%s", program, out)
		}
		calls := strings.Join(s.Calls, "\n")
		if strings.Contains(calls, "healthcheck run") || strings.Contains(calls, "loginctl") {
			t.Errorf("%s: calls:\n%s", program, calls)
		}
	}
}

// withRunning adds runningScript's lines to s, after what s already holds.
func withRunning(s *engine.Scripted) {
	s.On("podman version", engine.Result{Stdout: "6.0.2\n"})
	s.On("podman image exists", engine.Result{})
	s.On("podman volume exists", engine.Result{})
	s.On("podman pod exists", engine.Result{})
	s.On("podman container inspect", engine.Result{Stdout: "running|" + testImage + "|" + currentLabel() + "\n"})
	scriptWorker(s, "podman", "running", settingsForImage(testImage), 1)
}
