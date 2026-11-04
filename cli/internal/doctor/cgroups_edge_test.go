package doctor_test

// Independent edge cases: the doctor cgroups row.

import (
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/doctor"
	"github.com/djlsystems/yawble/cli/internal/engine"
)

func TestCgroupEdgeOnlyPidsMissingFailsNamingPids(t *testing.T) {
	o := healthyObserved()
	o.Cgroups = &engine.Cgroups{Measured: true, Controllers: []string{"cpu", "memory"}, Source: "podman info"}
	row := find(t, doctor.HostChecks(o), "cgroups")
	if row.Verdict != doctor.Fail || !strings.Contains(row.Detail, "reports no pids cgroup controller (it has: cpu, memory)") || row.Fix == "" {
		t.Errorf("%+v", row)
	}
}

func TestCgroupEdgeDockerPartialFailsNamingCpuAndDockerFix(t *testing.T) {
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
func TestCgroupEdgeMissingAndNoLimitsControlIsFail(t *testing.T) {
	o := healthyObserved()
	o.ControlNoLimits = true
	o.Cgroups = &engine.Cgroups{Measured: true, Source: "podman info"}
	if row := find(t, doctor.HostChecks(o), "cgroups"); row.Verdict != doctor.Fail {
		t.Errorf("%+v", row)
	}
}

// Not measured with a control made without its limits warns: the instance runs without them and
// the cause could not be measured. Never ok, and not a plain skip.
func TestTheCgroupsRowWarnsWhenNotMeasuredAndControlHasNoLimits(t *testing.T) {
	o := healthyObserved()
	o.ControlNoLimits = true
	o.Cgroups = &engine.Cgroups{Source: "podman info", Reason: "podman info failed"}
	row := find(t, doctor.HostChecks(o), "cgroups")
	if row.Verdict != doctor.Warn || !strings.Contains(row.Detail, "the instance runs without container limits") || !strings.Contains(row.Detail, "the cause could not be measured: podman info failed") || !strings.Contains(row.Fix, "podman info") {
		t.Errorf("%+v", row)
	}
	o.ControlNoLimits = false
	if row := find(t, doctor.HostChecks(o), "cgroups"); row.Verdict != doctor.Skip {
		t.Errorf("control with its limits, not measured: %+v", row)
	}
}
