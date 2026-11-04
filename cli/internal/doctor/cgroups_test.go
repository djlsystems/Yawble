package doctor_test

import (
	"context"
	"os"
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/doctor"
	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/instance"
)

// cgroupsObserved is the doctor's cgroups row for an engine whose `info --format json` answers
// with the fixture: the engine's own report, read the way Observe reads it.
func cgroupsObserved(t *testing.T, make func(engine.Runner) engine.Engine, fixture string) doctor.Check {
	t.Helper()
	b, err := os.ReadFile("../engine/testdata/" + fixture)
	if err != nil {
		t.Fatal(err)
	}
	s := engine.NewScripted()
	e := make(s)
	s.On(e.Name()+" info --format json", engine.Result{Stdout: string(b)})
	o := doctor.Observe(context.Background(), e, instance.Settings{Port: 8080, Image: "img"}, doctor.Probes{Health: func(string) bool { return false }, PortFree: func(int) bool { return true }, GOOS: "linux"}, "", "")
	return find(t, doctor.HostChecks(o), "cgroups")
}

// The rootless WSL Podman machine as measured: cgroupControllers is empty. The row FAILs in plain
// words, naming what is missing, that up starts without the limits, and the fix.
func TestTheCgroupsRowFailsWhenPodmanReportsNoControllers(t *testing.T) {
	row := cgroupsObserved(t, engine.NewPodman, "podman-info-wsl-no-controllers.json")
	if row.Verdict != doctor.Fail {
		t.Fatalf("%+v", row)
	}
	for _, want := range []string{"podman info reports no memory, cpu, pids cgroup controller", "it has: none", "cannot apply a container's memory, CPU and process (pids) limits", "starts the instance WITHOUT the containers' limits"} {
		if !strings.Contains(row.Detail, want) {
			t.Errorf("detail lacks %q: %s", want, row.Detail)
		}
	}
	if !strings.Contains(row.Fix, "cgroup.subtree_control") || !strings.Contains(row.Fix, "cgroupControllers") || !strings.Contains(row.Fix, "yawble up") {
		t.Errorf("fix: %s", row.Fix)
	}
	for _, os := range []string{"Windows", "WSL", "macOS"} {
		if strings.Contains(row.Detail+row.Fix, os) {
			t.Errorf("names an OS it did not detect (%s): %+v", os, row)
		}
	}
}

func TestTheCgroupsRowIsOkWhenPodmanReportsTheControllers(t *testing.T) {
	row := cgroupsObserved(t, engine.NewPodman, "podman-info-delegated.json")
	if row.Verdict != doctor.OK || !strings.Contains(row.Detail, "memory, cpu and pids are there (podman info)") {
		t.Errorf("%+v", row)
	}
}

// Docker's equivalent: docker info's MemoryLimit, CpuCfsQuota and PidsLimit.
func TestTheCgroupsRowReadsDockerInfo(t *testing.T) {
	if row := cgroupsObserved(t, engine.NewDocker, "docker-info-limits.json"); row.Verdict != doctor.OK || !strings.Contains(row.Detail, "(docker info)") {
		t.Errorf("limits: %+v", row)
	}
	row := cgroupsObserved(t, engine.NewDocker, "docker-info-no-limits.json")
	if row.Verdict != doctor.Fail || !strings.Contains(row.Detail, "docker info reports no memory, cpu, pids") || !strings.Contains(row.Fix, "docker info") || strings.Contains(row.Fix, "podman") {
		t.Errorf("no limits: %+v", row)
	}
}

// Not measured stays not measured: never ok, never a failure.
func TestTheCgroupsRowNotMeasuredIsSkip(t *testing.T) {
	o := healthyObserved()
	o.Cgroups = &engine.Cgroups{Source: "docker info", Reason: "docker info does not say whether it can apply memory, CPU and process limits"}
	if row := find(t, doctor.HostChecks(o), "cgroups"); row.Verdict != doctor.Skip || !strings.HasPrefix(row.Detail, "not measured: docker info does not say") {
		t.Errorf("%+v", row)
	}
	o.Cgroups = nil
	if row := find(t, doctor.HostChecks(o), "cgroups"); row.Verdict != doctor.Skip {
		t.Errorf("%+v", row)
	}
}

// Delegated now, but control was made without its limits: warn, and up makes it again.
func TestTheCgroupsRowWarnsWhenTheInstanceRunsWithoutLimitsItCouldHave(t *testing.T) {
	o := healthyObserved()
	o.ControlNoLimits = true
	if row := find(t, doctor.HostChecks(o), "cgroups"); row.Verdict != doctor.Warn || !strings.Contains(row.Detail, "started without its memory and CPU limits") || !strings.HasPrefix(row.Fix, "yawble up") {
		t.Errorf("%+v", row)
	}
}

// The cgroups row is a key row of the short list, ok or not.
func TestTheCgroupsRowIsAlwaysOnTheShortList(t *testing.T) {
	shown, _ := doctor.Short(doctor.HostChecks(healthyObserved()))
	for _, c := range shown {
		if c.Name == "cgroups" {
			return
		}
	}
	t.Errorf("not shown: %+v", shown)
}
