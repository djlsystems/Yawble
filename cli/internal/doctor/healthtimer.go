package doctor

import (
	"context"
	"fmt"
	"strings"
	"time"

	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/instance"
)

// HealthObserved is one running container's health check as doctor found it: what the engine's
// record said before anything was run, the answer of the check run by hand (empty when it was
// not run), and the engine's times (nil when they could not be read).
type HealthObserved struct {
	Name   string
	Engine engine.Health
	Ran    engine.Health
	Times  *engine.HealthTimes
}

// ObserveHealth measures control's and each running worker's health check. A worker whose record
// said starting or unhealthy has already had its check run (instance.Workers); one whose record
// says healthy while the engine has not run its check for three intervals has it run here, because
// that record is only as fresh as the last check.
func ObserveHealth(ctx context.Context, e engine.Engine, controlRunning bool, workers []instance.WorkerStatus, now time.Time) []HealthObserved {
	var out []HealthObserved
	add := func(name string, h, said engine.Health) {
		o := HealthObserved{Name: name, Engine: h}
		if said != "" {
			o.Engine, o.Ran = said, h
		}
		if t, err := e.HealthTimes(ctx, name); err == nil {
			o.Times = &t
			if o.Ran == "" && o.Engine == engine.HealthHealthy && checkOverdue(t, now) {
				if ran, err := e.RunHealthCheck(ctx, name); err == nil {
					o.Ran = ran
				}
			}
		}
		out = append(out, o)
	}
	if controlRunning {
		if h, said, err := instance.CurrentHealth(ctx, e, instance.ContainerName); err == nil && h != engine.HealthNone {
			add(instance.ContainerName, h, said)
		}
	}
	for _, w := range workers {
		if w.Container == engine.StateRunning && w.Health != "" && w.Health != engine.HealthNone {
			add(w.Name, w.Health, w.EngineHealth)
		}
	}
	return out
}

// checkOverdue is the engine not having run a check for three of its intervals, counted from the
// later of the container's start and the last check.
func checkOverdue(t engine.HealthTimes, now time.Time) bool {
	from := t.StartedAt
	if t.LastCheck.After(from) {
		from = t.LastCheck
	}
	return !from.IsZero() && now.Sub(from) > 3*t.Every()
}

// timerIdle says why a container shows the engine is not running its health check on its own,
// or "" when it does not. Both signs need the check, run by hand, to pass: a check that fails is
// the container's own fault, said elsewhere. One: the record still says starting past the start
// period and one interval. Two: the record says healthy, and the engine has run no check for
// three intervals.
func timerIdle(o HealthObserved, now time.Time) string {
	if o.Times == nil || o.Ran != engine.HealthHealthy || o.Times.StartedAt.IsZero() {
		return ""
	}
	up := now.Sub(o.Times.StartedAt)
	switch {
	case o.Engine == engine.HealthStarting && up > o.Times.StartPeriod+o.Times.Every():
		return fmt.Sprintf("%s still read starting %s after it started (its start period is %s), and its check, run by hand, passes", o.Name, roundMinute(up), roundMinute(o.Times.StartPeriod))
	case o.Engine == engine.HealthHealthy && checkOverdue(*o.Times, now):
		last := "has never been run"
		if !o.Times.LastCheck.IsZero() {
			last = "was last run " + roundMinute(now.Sub(o.Times.LastCheck)) + " ago"
		}
		return fmt.Sprintf("%s's check %s, although it is due every %s, and run by hand it passes", o.Name, last, o.Times.Every())
	}
	return ""
}

func roundMinute(d time.Duration) string {
	if d >= time.Minute {
		return d.Round(time.Minute).String()
	}
	return d.Round(time.Second).String()
}

// HealthTimerCheck is the doctor's row for the engine's own health checks. It warns when any
// container shows the engine is not running them (timerIdle), names fix when one was found, and
// says `yawble up` works around it. It is ok when every check is run on time, and absent (false)
// when no container's check could be measured.
func HealthTimerCheck(observed []HealthObserved, fix string, now time.Time) (Check, bool) {
	measured := false
	var idle []string
	for _, o := range observed {
		if o.Times != nil {
			measured = true
		}
		if why := timerIdle(o, now); why != "" {
			idle = append(idle, why)
		}
	}
	if !measured {
		return Check{}, false
	}
	if len(idle) == 0 {
		return Check{"health checks", OK, "the engine runs each container's health check on its own", ""}, true
	}
	advice := "no fix was found from here: the engine runs rootless Podman's checks from systemd --user timers, so the user's systemd session is the place to look"
	if fix != "" {
		advice = fix
	}
	return Check{"health checks", Warn,
		"the engine is not running health checks on its own here: " + strings.Join(idle, "; "),
		advice + ". Meanwhile `yawble up` runs the checks itself and is not held up, and `yawble status` and `yawble doctor` do the same"}, true
}

// LingerFix is the fix for a user whose systemd manager is not kept running (lingering off), in
// the Podman machine named machineName or, with none, on this computer. "" when lingering is on
// or could not be read: then no fix is named.
func LingerFix(user string, linger, measured bool, machineName string) string {
	if !measured || linger || user == "root" {
		return ""
	}
	cmd := "sudo loginctl enable-linger " + user
	where := "on this computer"
	if machineName != "" {
		cmd = "podman machine ssh " + machineName + " " + cmd
		where = "in the Podman machine " + machineName
	}
	return fmt.Sprintf("lingering is off for %s %s, so the systemd --user manager that runs Podman's health check timers is not kept running: run `%s`, then `yawble down` and `yawble up` so the containers' timers are made again", user, where, cmd)
}
