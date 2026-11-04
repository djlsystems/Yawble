package instance_test

// Independent edge cases: degrade only when the controllers are missing,
// older labels, partial support, port sentence kept for a real port error.

import (
	"bytes"
	"context"
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/instance"
)

func TestCgroupEdgeRealPortInUseStillGetsThePortSentence(t *testing.T) {
	s := fresh("podman", healthPrefix, 1)
	s.On("podman run -d --name yawble --pod", engine.Result{Stderr: "Error: rootlessport listen tcp 0.0.0.0:8080: bind: address already in use", ExitCode: 126})
	err := instance.Up(context.Background(), engine.NewPodman(s), keyed(), healthy, &bytes.Buffer{})
	if err == nil || !strings.Contains(err.Error(), "port 8080 is already in use") || strings.Contains(err.Error(), "cgroup") {
		t.Errorf("%v", err)
	}
	if n := len(runsOf(s, "podman run -d --name yawble --pod")); n != 1 {
		t.Errorf("a port error must not trigger the no-limits retry: %d runs", n)
	}
}

func TestCgroupEdgeOnlyPidsMissingDegrades(t *testing.T) {
	s := fresh("podman", healthPrefix, 1)
	s.On("podman info --format json", engine.Result{Stdout: `{"host":{"cgroupControllers":["cpu","memory"]}}`})
	var out bytes.Buffer
	if err := instance.Up(context.Background(), engine.NewPodman(s), keyed(), healthy, &out); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(out.String(), "reports no pids cgroup controller") {
		t.Errorf("output:\n%s", out.String())
	}
	for _, r := range runsOf(s, "podman run -d") {
		if strings.Contains(r, "--memory") || strings.Contains(r, "--pids-limit") {
			t.Errorf("run %s", r)
		}
	}
	t.Logf("only pids missing:\n%s", out.String())
}

func TestCgroupEdgeDockerPartialSupportDegrades(t *testing.T) {
	s := fresh("docker", engines[1].health, 1)
	s.On("docker info --format json", engine.Result{Stdout: `{"MemoryLimit":true,"CpuCfsQuota":false,"PidsLimit":true}`})
	var out bytes.Buffer
	if err := instance.Up(context.Background(), engine.NewDocker(s), keyed(), healthy, &out); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(out.String(), "docker info reports no cpu cgroup controller") || strings.Contains(out.String(), "podman") {
		t.Errorf("output:\n%s", out.String())
	}
	for _, r := range runsOf(s, "docker run -d") {
		if strings.Contains(r, "--memory") || strings.Contains(r, "--cpus") {
			t.Errorf("run %s", r)
		}
	}
}

// An instance from before the cgroups check: its label has no noLimits. Running: nothing asked, nothing made.
func TestCgroupEdgeOlderRunningLabelIsLeftAlone(t *testing.T) {
	st := keyed()
	old := strings.Replace(label(st), `,"noLimits":false`, "", 1)
	s := engine.NewScripted()
	s.On(inspect, engine.Result{Stdout: "running|" + img + "|" + old + "\n"})
	workerAs(s, "running", st, 1)
	var out bytes.Buffer
	if err := instance.Up(context.Background(), engine.NewPodman(s), st, healthy, &out); err != nil {
		t.Fatal(err)
	}
	if len(runsOf(s, "podman run -d")) != 0 || len(runsOf(s, "podman info")) != 0 || strings.Contains(out.String(), "changed") {
		t.Errorf("calls %q\n%s", s.Calls, out.String())
	}
}

// Older stopped label, engine report not measured: started as it is, no recreate.
func TestCgroupEdgeOlderStoppedLabelNotMeasuredJustStarts(t *testing.T) {
	st := keyed()
	s := engine.NewScripted()
	s.On(inspect, engine.Result{Stdout: "exited|" + img + "|" + label(st) + "\n"})
	workerAs(s, "running", st, 1)
	s.On("podman info --format json", engine.Result{Stderr: "Error: cannot connect", ExitCode: 125})
	var out bytes.Buffer
	if err := instance.Up(context.Background(), engine.NewPodman(s), st, healthy, &out); err != nil {
		t.Fatal(err)
	}
	starts := 0
	for _, c := range s.Calls {
		if c == "podman start yawble" {
			starts++
		}
	}
	if len(runsOf(s, "podman run -d")) != 0 || starts != 1 {
		t.Errorf("calls %q", s.Calls)
	}
}

// Control running without limits, engine report not measured: kept without limits, and up says
// the instance runs without container limits and the cause could not be measured.
func TestUpWarnsWhenTheReportCannotBeReadAndControlHasNoLimits(t *testing.T) {
	st := keyed()
	was := st
	was.NoLimits = true
	s := engine.NewScripted()
	s.On(inspect, engine.Result{Stdout: "running|" + img + "|" + label(was) + "\n"})
	workerAs(s, "running", was, 1)
	s.On("podman info --format json", engine.Result{Stderr: "Error: cannot connect", ExitCode: 125})
	var out bytes.Buffer
	if err := instance.Up(context.Background(), engine.NewPodman(s), st, healthy, &out); err != nil {
		t.Fatal(err)
	}
	if len(runsOf(s, "podman run -d")) != 0 {
		t.Errorf("remade: %q", s.Calls)
	}
	text := out.String()
	if !strings.Contains(text, "warning: the instance runs without container limits") || !strings.Contains(text, "the cause could not be measured: podman info --format: Error: cannot connect (exit 125)") {
		t.Errorf("output:\n%s", text)
	}
}

// Report not measured but control made with its limits: nothing to warn about.
func TestUpSaysNothingOfLimitsWhenTheReportCannotBeReadAndControlHasThem(t *testing.T) {
	st := keyed()
	s := engine.NewScripted()
	s.On(inspect, engine.Result{Stdout: "exited|" + img + "|" + label(st) + "\n"})
	workerAs(s, "running", st, 1)
	s.On("podman info --format json", engine.Result{Stderr: "Error: cannot connect", ExitCode: 125})
	var out bytes.Buffer
	if err := instance.Up(context.Background(), engine.NewPodman(s), st, healthy, &out); err != nil {
		t.Fatal(err)
	}
	if strings.Contains(out.String(), "without container limits") || strings.Contains(out.String(), "WITHOUT") {
		t.Errorf("output:\n%s", out.String())
	}
}

// A worker made without limits beside a limited control by an older yawble (its label records
// no report) is made again once; the worker refused again then records the report.
func TestCgroupEdgeWorkerFromAnOlderLabelBesideALimitedControlIsRemadeOnce(t *testing.T) {
	st := keyed()
	w := st
	w.NoLimits = true
	s := engine.NewScripted()
	s.On(inspect, engine.Result{Stdout: "running|" + img + "|" + label(st) + "\n"})
	workerAs(s, "running", w, 1)
	s.OnSequence("podman run -d --name yawble-worker-1", crunMemoryMax, engine.Result{})
	var out bytes.Buffer
	if err := instance.Up(context.Background(), engine.NewPodman(s), st, healthy, &out); err != nil {
		t.Fatal(err)
	}
	runs := runsOf(s, "podman run -d --name yawble-worker-1")
	if !strings.Contains(out.String(), "memory/CPU limits can be applied now") || len(runs) != 2 || !strings.Contains(runs[1], `"cgroups":"not measured"`) {
		t.Errorf("runs %q\n%s", runs, out.String())
	}
}

// Docker's own runc wording for a missing cgroup file degrades the same way.
func TestCgroupEdgeDockerRuncCgroupErrorDegrades(t *testing.T) {
	s := fresh("docker", engines[1].health, 1)
	s.OnSequence("docker run -d --name yawble --network", engine.Result{Stderr: "docker: Error response from daemon: failed to create task for container: runc create failed: unable to start container process: error during container init: open /sys/fs/cgroup/memory.max: no such file or directory", ExitCode: 126}, engine.Result{})
	var out bytes.Buffer
	if err := instance.Up(context.Background(), engine.NewDocker(s), keyed(), healthy, &out); err != nil {
		t.Fatal(err)
	}
	c := runsOf(s, "docker run -d --name yawble --network")
	if len(c) != 2 || strings.Contains(c[1], "--memory") || strings.Contains(out.String(), "usual cause is port") {
		t.Errorf("%q\n%s", c, out.String())
	}
}
