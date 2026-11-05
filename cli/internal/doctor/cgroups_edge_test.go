package doctor_test

// Independent edge cases: the doctor cgroups row.

import (
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/doctor"
	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/instance"
	"github.com/djlsystems/yawble/cli/internal/machine"
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

// Missing controllers AND a no-limits control: while that control runs and answers, the
// row is a warning with the same fix - the instance works without its limits, and doctor exits 0.
// Before it answers - stopped, absent, or not healthy - it is still a FAIL.
func TestCgroupEdgeMissingAndNoLimitsControlRunningHealthyWarnsElseFails(t *testing.T) {
	o := healthyObserved()
	o.ControlNoLimits = true
	o.Cgroups = &engine.Cgroups{Measured: true, Source: "podman info"}
	row := find(t, doctor.HostChecks(o), "cgroups")
	if row.Verdict != doctor.Warn || row.Fix != instance.LimitsFix("podman") || !strings.Contains(row.Detail, "the instance runs and answers WITHOUT the containers' limits") || !strings.Contains(row.Detail, "doctor exits 0") {
		t.Errorf("running healthy: %+v", row)
	}
	unhealthy, stopped, absent, limited := o, o, o, o
	unhealthy.Healthy = boolp(false)
	stopped.Container = engine.StateStopped
	absent.Container = engine.StateAbsent
	limited.ControlNoLimits = false
	for name, c := range map[string]doctor.Observed{"not answering": unhealthy, "stopped": stopped, "not made": absent, "made with limits": limited} {
		if row := find(t, doctor.HostChecks(c), "cgroups"); row.Verdict != doctor.Fail || row.Fix != instance.LimitsFix("podman") {
			t.Errorf("%s: %+v", name, row)
		}
	}
}

// Under cgroupfs on a Podman machine the warning keeps the cgroupfs fix, word for word.
func TestTheCgroupsWarningUnderCgroupfsKeepsTheCgroupfsFix(t *testing.T) {
	o := healthyObserved()
	o.ControlNoLimits = true
	o.Machine = machine.Info{Applies: true, Exists: true, Running: true}
	o.Cgroups = &engine.Cgroups{Measured: true, Source: "podman info", Manager: "cgroupfs"}
	if row := find(t, doctor.HostChecks(o), "cgroups"); row.Verdict != doctor.Warn || row.Fix != doctor.CgroupfsFix {
		t.Errorf("%+v", row)
	}
	o.Healthy = boolp(false)
	if row := find(t, doctor.HostChecks(o), "cgroups"); row.Verdict != doctor.Fail || row.Fix != doctor.CgroupfsFix {
		t.Errorf("not answering: %+v", row)
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
