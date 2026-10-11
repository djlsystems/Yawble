package engine_test

import (
	"context"
	"errors"
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/engine"
)

const (
	podmanStatsLine = "podman stats --no-stream --format json yawble"
	dockerStatsLine = "docker stats --no-stream --format {{json .}} yawble"
	podmanStatsJSON = `[
 {
  "id": "3f2a",
  "name": "yawble",
  "cpu_time": "1m2s",
  "cpu_percent": "12.34%",
  "avg_cpu": "3.1%",
  "mem_usage": "1.2GB / 12.88GB",
  "mem_percent": "9.32%",
  "net_io": "1.1MB / 2.2MB",
  "block_io": "10MB / 20MB",
  "pids": "412"
 }
]
`
	dockerStatsJSON = `{"BlockIO":"10MB / 20MB","CPUPerc":"250.50%","Container":"yawble","ID":"3f2a","MemPerc":"91.20%","MemUsage":"7.5GiB / 8GiB","Name":"yawble","NetIO":"1.1MB / 2.2MB","PIDs":"37"}` + "\n"
)

func TestPodmanStatsAsksOnceAndReadsItsJSONArray(t *testing.T) {
	s := engine.NewScripted()
	s.On(podmanStatsLine, engine.Result{Stdout: podmanStatsJSON})
	st, err := engine.NewPodman(s).Stats(context.Background(), "yawble")
	if err != nil {
		t.Fatal(err)
	}
	if len(s.Calls) != 1 || s.Calls[0] != podmanStatsLine {
		t.Errorf("calls %q", s.Calls)
	}
	if st.Command != "podman stats" || st.NotRunning || *st.CPUPercent != 12.34 || *st.MemoryPercent != 9.32 || *st.PIDs != 412 {
		t.Errorf("stats %+v", st)
	}
	if st.MemoryUsage != "1.2GB" || st.MemoryLimit != "12.88GB" || *st.MemoryUsageBytes != 1_200_000_000 || *st.MemoryLimitBytes != 12_880_000_000 {
		t.Errorf("memory %+v", st)
	}
	if st.NetIO != "1.1MB / 2.2MB" || st.BlockIO != "10MB / 20MB" {
		t.Errorf("io %q %q", st.NetIO, st.BlockIO)
	}
	if got := st.Summary(); got != "cpu 12.3%  memory 1.2GB / 12.88GB (9%)  pids 412" {
		t.Errorf("summary %q", got)
	}
}

func TestDockerStatsAsksOnceAndReadsItsJSONLine(t *testing.T) {
	s := engine.NewScripted()
	s.On(dockerStatsLine, engine.Result{Stdout: dockerStatsJSON})
	st, err := engine.NewDocker(s).Stats(context.Background(), "yawble")
	if err != nil {
		t.Fatal(err)
	}
	if len(s.Calls) != 1 || s.Calls[0] != dockerStatsLine {
		t.Errorf("calls %q", s.Calls)
	}
	if st.Command != "docker stats" || *st.CPUPercent != 250.5 || *st.MemoryPercent != 91.2 || *st.PIDs != 37 {
		t.Errorf("stats %+v", st)
	}
	if st.MemoryUsage != "7.5GiB" || *st.MemoryUsageBytes != 7.5*(1<<30) || *st.MemoryLimitBytes != 8<<30 {
		t.Errorf("memory %+v", st)
	}
}

// A field the engine did not print, or printed as a placeholder, is not measured, never zero.
func TestAStatsFieldThatIsMissingIsNotMeasured(t *testing.T) {
	s := engine.NewScripted()
	s.On(podmanStatsLine, engine.Result{Stdout: `[{"name":"yawble","cpu_percent":"--","mem_usage":"300MB / 2GB","pids":"7"}]`})
	s.On(dockerStatsLine, engine.Result{Stdout: `{"CPUPerc":"1.00%","MemUsage":"weird","MemPerc":"2.00%"}`})
	st, err := engine.NewPodman(s).Stats(context.Background(), "yawble")
	if err != nil {
		t.Fatal(err)
	}
	if st.CPUPercent != nil || st.MemoryPercent != nil || *st.PIDs != 7 || *st.MemoryUsageBytes != 300_000_000 {
		t.Errorf("podman %+v", st)
	}
	if got := st.Summary(); got != "cpu not measured  memory 300MB / 2GB  pids 7" {
		t.Errorf("summary %q", got)
	}
	st, err = engine.NewDocker(s).Stats(context.Background(), "yawble")
	if err != nil {
		t.Fatal(err)
	}
	if st.PIDs != nil || st.MemoryUsage != "weird" || st.MemoryUsageBytes != nil || st.MemoryLimit != "" || st.NetIO != "" {
		t.Errorf("docker %+v", st)
	}
	if got := st.Summary(); got != "cpu 1.0%  memory weird (2%)  pids not measured" {
		t.Errorf("summary %q", got)
	}
}

// A stopped container is said as such on both engines: Podman refuses or answers an empty list,
// Docker prints a row of placeholders or zeros.
func TestStatsOfAStoppedContainerIsNotRunningNotAnError(t *testing.T) {
	cases := []struct {
		name string
		e    func(engine.Runner) engine.Engine
		line string
		res  engine.Result
	}{
		{"podman refuses", engine.NewPodman, podmanStatsLine, engine.Result{Stderr: "Error: cannot get stats: container is not running", ExitCode: 125}},
		{"podman empty list", engine.NewPodman, podmanStatsLine, engine.Result{Stdout: "[]\n"}},
		{"podman absent", engine.NewPodman, podmanStatsLine, engine.Result{Stderr: "Error: no such container yawble", ExitCode: 125}},
		{"docker dashes", engine.NewDocker, dockerStatsLine, engine.Result{Stdout: `{"CPUPerc":"--","MemUsage":"-- / --","MemPerc":"--","PIDs":"--","Name":"yawble"}`}},
		{"docker zeros", engine.NewDocker, dockerStatsLine, engine.Result{Stdout: `{"CPUPerc":"0.00%","MemUsage":"0B / 0B","MemPerc":"0.00%","PIDs":"0","Name":"yawble"}`}},
		{"docker absent", engine.NewDocker, dockerStatsLine, engine.Result{Stderr: "Error response from daemon: No such container: yawble", ExitCode: 1}},
	}
	for _, c := range cases {
		s := engine.NewScripted()
		s.On(c.line, c.res)
		st, err := c.e(s).Stats(context.Background(), "yawble")
		if err != nil || !st.NotRunning || st.CPUPercent != nil {
			t.Errorf("%s: %+v %v", c.name, st, err)
		}
		if st.Summary() != "the container is not running" {
			t.Errorf("%s: summary %q", c.name, st.Summary())
		}
	}
}

func TestAFailedStatsIsAnErrorCarryingTheEnginesSentence(t *testing.T) {
	s := engine.NewScripted()
	s.On(podmanStatsLine, engine.Result{Stderr: "Error: cgroups v1 is not supported for stats", ExitCode: 125})
	s.On(dockerStatsLine, engine.Result{Stdout: "not json at all"})
	if _, err := engine.NewPodman(s).Stats(context.Background(), "yawble"); err == nil || !strings.Contains(err.Error(), "cgroups v1") {
		t.Errorf("podman err %v", err)
	}
	if _, err := engine.NewDocker(s).Stats(context.Background(), "yawble"); err == nil || !strings.Contains(err.Error(), "docker stats") {
		t.Errorf("docker err %v", err)
	}
	var notRunnable *engine.NotRunnable
	if _, err := engine.NewPodman(absentRunner{}).Stats(context.Background(), "yawble"); !errors.As(err, &notRunnable) {
		t.Errorf("missing podman: %v", err)
	}
}
