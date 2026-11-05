package engine_test

import (
	"context"
	"slices"
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/engine"
)

// Measured live on rootless WSL Podman 6.1.4 with cgroupControllers=[]: podman stats printed
// "0B / 58.97GB" and pids 0 for a worker that was running agent runs. With no memory controller
// there is no memory.current to read, and with no pids controller no pids.current: those zeros are
// not readings. Stats.Without says them "not measured", on Podman and Docker alike; on an engine
// that reports its controllers, the same zeros are zeros.

// statsWith is the engine's stats of the fixture, read with the engine's own report of its
// controllers from the info fixture, the way status and doctor read them.
func statsWith(t *testing.T, make func(engine.Runner) engine.Engine, statsFixture, infoFixture string) engine.Stats {
	t.Helper()
	s := engine.NewScripted()
	e := make(s)
	s.On(e.Name()+" stats", engine.Result{Stdout: fixture(t, statsFixture)})
	s.On(e.Name()+" info --format json", engine.Result{Stdout: fixture(t, infoFixture)})
	st, err := e.Stats(context.Background(), "yawble-worker-1")
	if err != nil {
		t.Fatal(err)
	}
	return st.Without(e.Cgroups(context.Background()))
}

func TestStatsZerosTheMissingControllersLeaveUnreadableAreNotMeasured(t *testing.T) {
	cases := []struct {
		name, stats, info string
		make              func(engine.Runner) engine.Engine
		limit             string
	}{
		{"podman", "podman-stats-no-controllers.json", "podman-info-wsl-no-controllers.json", engine.NewPodman, "58.97GB"},
		{"docker", "docker-stats-no-controllers.json", "docker-info-no-limits.json", engine.NewDocker, "58.97GiB"},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			st := statsWith(t, c.make, c.stats, c.info)
			if st.MemoryUsageBytes != nil || st.MemoryUsage != "" || st.MemoryPercent != nil || st.PIDs != nil {
				t.Errorf("a zero the missing controllers leave unreadable is still a figure: %+v", st)
			}
			if !slices.Equal(st.Unreadable, []string{"memory", "pids"}) {
				t.Errorf("unreadable %v", st.Unreadable)
			}
			// CPU comes from cpu.stat, which every cgroup has: it is still the engine's figure.
			if st.CPUPercent == nil || *st.CPUPercent != 38.21 {
				t.Errorf("cpu %v", st.CPUPercent)
			}
			sum := st.Summary()
			for _, want := range []string{
				"memory not measured (no memory cgroup controller) / " + c.limit,
				"pids not measured (no pids cgroup controller)",
			} {
				if !strings.Contains(sum, want) {
					t.Errorf("summary lacks %q: %s", want, sum)
				}
			}
			for _, never := range []string{"0B /", "pids 0", "(0%)"} {
				if strings.Contains(sum, never) {
					t.Errorf("summary says %q: %s", never, sum)
				}
			}
		})
	}
}

func TestStatsARealZeroOnAnEngineThatReportsItsControllersStays(t *testing.T) {
	cases := []struct {
		name, stats, info string
		make              func(engine.Runner) engine.Engine
	}{
		{"podman", "podman-stats-zero.json", "podman-info-delegated.json", engine.NewPodman},
		{"docker", "docker-stats-zero.json", "docker-info-limits.json", engine.NewDocker},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			st := statsWith(t, c.make, c.stats, c.info)
			if st.MemoryUsageBytes == nil || *st.MemoryUsageBytes != 0 || st.PIDs == nil || *st.PIDs != 0 || len(st.Unreadable) != 0 {
				t.Errorf("a real zero changed: %+v", st)
			}
			if sum := st.Summary(); !strings.Contains(sum, "memory 0B /") || !strings.Contains(sum, "pids 0") || strings.Contains(sum, "not measured") {
				t.Errorf("summary %s", sum)
			}
		})
	}
}

// Without a report of the controllers nothing is known to be missing, so nothing is cleared; and a
// figure that is not zero was read, controller or not.
func TestStatsAreClearedOnlyForAMeasuredMissingControllerAndAZero(t *testing.T) {
	zero, used := int64(0), int64(1_200_000_000)
	pids0, pids9 := 0, 9
	notMeasured := engine.Cgroups{Source: "podman info", Reason: "podman info failed"}
	none := engine.Cgroups{Measured: true, Source: "podman info"}

	st := engine.Stats{Command: "podman stats", MemoryUsage: "0B", MemoryUsageBytes: &zero, PIDs: &pids0}
	if got := st.Without(notMeasured); got.MemoryUsageBytes == nil || got.PIDs == nil || len(got.Unreadable) != 0 {
		t.Errorf("not measured cleared figures: %+v", got)
	}
	st = engine.Stats{Command: "podman stats", MemoryUsage: "1.2GB", MemoryUsageBytes: &used, PIDs: &pids9}
	if got := st.Without(none); got.MemoryUsageBytes == nil || got.PIDs == nil || len(got.Unreadable) != 0 {
		t.Errorf("a figure that was read was cleared: %+v", got)
	}
	// Only the controller that is missing: pids there, memory not.
	st = engine.Stats{Command: "podman stats", MemoryUsage: "0B", MemoryUsageBytes: &zero, PIDs: &pids0}
	got := st.Without(engine.Cgroups{Measured: true, Controllers: []string{"cpu", "pids"}, Source: "podman info"})
	if got.MemoryUsageBytes != nil || got.PIDs == nil || !slices.Equal(got.Unreadable, []string{"memory"}) {
		t.Errorf("%+v", got)
	}
}
