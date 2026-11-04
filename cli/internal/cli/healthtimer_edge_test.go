package cli_test

import (
	"strings"
	"testing"
	"time"

	"github.com/djlsystems/yawble/cli/internal/engine"
)

// timerIdleScript is a Podman instance whose worker-1 record says starting until doctor runs its
// check by hand, 15 minutes after it started: the warning's first sign.
func timerIdleScript() *engine.Scripted {
	s := engine.NewScripted()
	s.OnSequence(healthLine("podman", "yawble-worker-1"), engine.Result{Stdout: "starting\n"}, engine.Result{Stdout: "healthy\n"})
	withRunning(s)
	s.On(doctorExec, engine.Result{Stdout: doctorStdout})
	s.On("podman container inspect yawble-worker-1", engine.Result{Stdout: inspectDoc(0)})
	return s
}

// Lingering cannot be read (id fails, or loginctl fails, or answers something else): no fix is
// named, the row says none was found, and still that up works around it.
func TestDoctorNamesNoFixWhenLingeringCannotBeRead(t *testing.T) {
	cases := map[string]func(*engine.Scripted){
		"id fails": func(s *engine.Scripted) {
			s.On("id -un", engine.Result{Stderr: "id: cannot find name", ExitCode: 1})
		},
		"loginctl fails": func(s *engine.Scripted) {
			s.On("id -un", engine.Result{Stdout: "agent\n"})
			s.On("loginctl", engine.Result{Stderr: "Failed to connect to bus", ExitCode: 1})
		},
		"loginctl answers something else": func(s *engine.Scripted) {
			s.On("id -un", engine.Result{Stdout: "agent\n"})
			s.On("loginctl", engine.Result{Stdout: "\n"})
		},
	}
	for name, script := range cases {
		s := timerIdleScript()
		script(s)
		deps := stubbed(s)
		deps.Now = func() time.Time { return timerNow }
		_, out, _ := run(t, deps, "doctor")
		for _, want := range []string{
			"the engine is not running health checks on its own here: yawble-worker-1 still read starting",
			"no fix was found from here",
			"`yawble up` runs the checks itself and is not held up",
		} {
			if !strings.Contains(out, want) {
				t.Errorf("%s: output lacks %q:\n%s", name, want, out)
			}
		}
		if strings.Contains(out, "enable-linger") {
			t.Errorf("%s: a fix named from an unread answer:\n%s", name, out)
		}
	}
}

// Lingering is on: the timer is still idle, so the row warns, but names no lingering fix.
func TestDoctorNamesNoLingerFixWhenLingeringIsOn(t *testing.T) {
	s := timerIdleScript()
	s.On("id -un", engine.Result{Stdout: "agent\n"})
	s.On("loginctl show-user agent --property=Linger --value", engine.Result{Stdout: "yes\n"})
	deps := stubbed(s)
	deps.Now = func() time.Time { return timerNow }
	_, out, _ := run(t, deps, "doctor")
	if !strings.Contains(out, "no fix was found from here") || strings.Contains(out, "enable-linger") {
		t.Errorf("output:\n%s", out)
	}
}

// The check run by hand really fails, 15 minutes in: not the timer, so no warning and no
// lingering asked; the worker's own row says what is wrong.
func TestDoctorDoesNotBlameTheTimerForACheckThatReallyFails(t *testing.T) {
	s := engine.NewScripted()
	s.On(healthLine("podman", "yawble-worker-1"), engine.Result{Stdout: "starting\n"})
	s.On("podman healthcheck run yawble-worker-1", engine.Result{Stdout: "unhealthy\n", ExitCode: 1})
	withRunning(s)
	s.On(doctorExec, engine.Result{Stdout: doctorStdout})
	s.On("podman container inspect yawble-worker-1", engine.Result{Stdout: inspectDoc(0)})
	deps := stubbed(s)
	deps.Now = func() time.Time { return timerNow }
	_, out, _ := run(t, deps, "doctor", "--details")
	if strings.Contains(out, "not running health checks on its own") {
		t.Errorf("warned about the timer for a failing check:\n%s", out)
	}
	if strings.Contains(strings.Join(s.Calls, "\n"), "loginctl") {
		t.Error("lingering was asked without a warning")
	}
}

// Control's own record stays starting while its check passes by hand: control is named too.
func TestDoctorWarnsForControlWhoseTimerDoesNotFire(t *testing.T) {
	s := engine.NewScripted()
	s.OnSequence(healthLine("podman", "yawble"), engine.Result{Stdout: "starting\n"}, engine.Result{Stdout: "healthy\n"})
	withRunning(s)
	s.On(doctorExec, engine.Result{Stdout: doctorStdout})
	s.On("podman container inspect yawble", engine.Result{Stdout: inspectDoc(0)})
	s.On("podman container inspect yawble-worker-1", engine.Result{Stdout: inspectDoc(10 * time.Second)})
	s.On("id -un", engine.Result{Stdout: "agent\n"})
	s.On("loginctl show-user agent --property=Linger --value", engine.Result{Stdout: "no\n"})
	deps := stubbed(s)
	deps.Now = func() time.Time { return timerNow }
	_, out, _ := run(t, deps, "doctor")
	if !strings.Contains(out, "yawble still read starting 15m0s after it started") || !strings.Contains(out, "`sudo loginctl enable-linger agent`") {
		t.Errorf("output:\n%s", out)
	}
}
