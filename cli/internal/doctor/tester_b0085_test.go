package doctor_test

// Independent edge cases for B0085 (tester): the doctor cgroups row.

import (
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/doctor"
	"github.com/djlsystems/yawble/cli/internal/engine"
)

func TestB0085OnlyPidsMissingFailsNamingPids(t *testing.T) {
	o := healthyObserved()
	o.Cgroups = &engine.Cgroups{Measured: true, Controllers: []string{"cpu", "memory"}, Source: "podman info"}
	row := find(t, doctor.HostChecks(o), "cgroups")
	if row.Verdict != doctor.Fail || !strings.Contains(row.Detail, "reports no pids cgroup controller (it has: cpu, memory)") || row.Fix == "" {
		t.Errorf("%+v", row)
	}
}

func TestB0085DockerPartialFailsNamingCpuAndDockerFix(t *testing.T) {
	o := healthyObserved()
	o.EngineName = "docker"
	o.Cgroups = &engine.Cgroups{Measured: true, Controllers: []string{"memory", "pids"}, Source: "docker info"}
	row := find(t, doctor.HostChecks(o), "cgroups")
	if row.Verdict != doctor.Fail || !strings.Contains(row.Detail, "docker info reports no cpu cgroup controller") || strings.Contains(strings.ToLower(row.Fix), "podman") {
		t.Errorf("%+v", row)
	}
	for _, os := range []string{"Windows", "WSL", "macOS", "Linux"} {
		if strings.Contains(row.Detail+row.Fix, os) {
			t.Errorf("names %s: %+v", os, row)
		}
	}
}

// Missing controllers AND a no-limits control: still FAIL (the fail outranks the warn).
func TestB0085MissingAndNoLimitsControlIsFail(t *testing.T) {
	o := healthyObserved()
	o.ControlNoLimits = true
	o.Cgroups = &engine.Cgroups{Measured: true, Source: "podman info"}
	if row := find(t, doctor.HostChecks(o), "cgroups"); row.Verdict != doctor.Fail {
		t.Errorf("%+v", row)
	}
}

// Observation: not measured with a no-limits control is plain skip; the row does not say
// the instance runs without limits.
func TestB0085NotMeasuredWithNoLimitsControlIsSkip(t *testing.T) {
	o := healthyObserved()
	o.ControlNoLimits = true
	o.Cgroups = &engine.Cgroups{Source: "podman info", Reason: "podman info failed"}
	row := find(t, doctor.HostChecks(o), "cgroups")
	if row.Verdict != doctor.Skip {
		t.Errorf("%+v", row)
	}
	t.Logf("%+v", row)
}
