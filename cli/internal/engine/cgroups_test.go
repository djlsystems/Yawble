package engine_test

import (
	"context"
	"os"
	"slices"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/engine"
)

func fixture(t *testing.T, name string) string {
	t.Helper()
	b, err := os.ReadFile("testdata/" + name)
	if err != nil {
		t.Fatal(err)
	}
	return string(b)
}

// The engine's own report decides: podman info's host.cgroupControllers, docker info's three
// limit flags. Missing is what a container's limits need and the report lacks.
func TestCgroupsReadsTheEnginesOwnReport(t *testing.T) {
	cases := []struct {
		name, fixture string
		make          func(engine.Runner) engine.Engine
		missing       []string
		manager       string
	}{
		{"podman on a WSL machine with nothing delegated", "podman-info-wsl-no-controllers.json", engine.NewPodman, []string{"memory", "cpu", "pids"}, "cgroupfs"},
		{"podman with the controllers delegated", "podman-info-delegated.json", engine.NewPodman, nil, "systemd"},
		{"docker that can apply every limit", "docker-info-limits.json", engine.NewDocker, nil, ""},
		{"docker that can apply none", "docker-info-no-limits.json", engine.NewDocker, []string{"memory", "cpu", "pids"}, ""},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			s := engine.NewScripted()
			e := c.make(s)
			s.On(e.Name()+" info --format json", engine.Result{Stdout: fixture(t, c.fixture)})
			got := e.Cgroups(context.Background())
			if !got.Measured || got.Source != e.Name()+" info" {
				t.Fatalf("not measured: %+v", got)
			}
			if !slices.Equal(got.Missing(), c.missing) {
				t.Errorf("missing %q, want %q", got.Missing(), c.missing)
			}
			if got.Manager != c.manager || got.UnderCgroupfs() != (c.manager == "cgroupfs") {
				t.Errorf("manager %q, want %q", got.Manager, c.manager)
			}
			if len(s.Calls) != 1 || s.Calls[0] != e.Name()+" info --format json" {
				t.Errorf("calls %q", s.Calls)
			}
		})
	}
}

// An engine that cannot say is not measured, and nothing is missing: never read as ok or as
// a fault.
func TestCgroupsThatCannotBeReadAreNotMeasured(t *testing.T) {
	cases := []struct {
		name string
		make func(engine.Runner) engine.Engine
		res  engine.Result
	}{
		{"podman info fails", engine.NewPodman, engine.Result{Stderr: "Error: cannot connect", ExitCode: 125}},
		{"podman info without the field", engine.NewPodman, engine.Result{Stdout: `{"host":{"cgroupVersion":"v2"}}`}},
		{"podman info not json", engine.NewPodman, engine.Result{Stdout: "host:\n  cgroupControllers: []\n"}},
		{"podman info empty", engine.NewPodman, engine.Result{}},
		{"docker info without the flags", engine.NewDocker, engine.Result{Stdout: `{"NCPU":8}`}},
		{"docker info fails", engine.NewDocker, engine.Result{Stderr: "Cannot connect to the Docker daemon", ExitCode: 1}},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			s := engine.NewScripted()
			e := c.make(s)
			s.On(e.Name()+" info --format json", c.res)
			got := e.Cgroups(context.Background())
			if got.Measured || got.Reason == "" || got.Missing() != nil {
				t.Errorf("%+v", got)
			}
		})
	}
}
