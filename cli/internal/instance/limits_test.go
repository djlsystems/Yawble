package instance_test

import (
	"bytes"
	"context"
	"errors"
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/instance"
)

// crunMemoryMax is the engine's own words on a rootless WSL Podman machine whose cgroup
// controllers are not delegated (measured: yawble 2026.11.02.1, podman 6.1.4).
var crunMemoryMax = engine.Result{Stderr: "Error: crun: open `memory.max` for writing: No such file or directory: OCI runtime attempted to invoke a command that was not found", ExitCode: 127}

// The engine's whole error line, as the user saw it.
const crunLine = "podman run -d: Error: crun: open `memory.max` for writing: No such file or directory: OCI runtime attempted to invoke a command that was not found (exit 127)"

func runsOf(s *engine.Scripted, prefix string) []string {
	var runs []string
	for _, c := range s.Calls {
		if strings.HasPrefix(c, prefix) {
			runs = append(runs, c)
		}
	}
	return runs
}

func TestAnErrorNamingACgroupFileOrTheControllersIsACgroupErrorAndAPortErrorIsNot(t *testing.T) {
	for _, text := range []string{
		crunLine,
		"Error: crun: open `cpu.max` for writing: No such file or directory",
		"Error: crun: write `pids.max`: No such file or directory",
		"Error: OCI runtime error: the requested cgroup controller `memory` is not available",
		"docker: Error response from daemon: Your kernel does not support memory limit capabilities or the cgroup is not mounted. Limitation discarded.",
		"docker: Error response from daemon: NanoCPUs can not be set, as your kernel does not support CPU CFS scheduler or the cgroup is not mounted.",
	} {
		if !instance.CgroupError(errors.New(text)) {
			t.Errorf("not read as a cgroup error: %s", text)
		}
	}
	for _, text := range []string{
		"podman run -d: Error: starting some containers: internal libpod error (exit 126)",
		"Error: rootlessport listen tcp 0.0.0.0:8080: bind: address already in use",
		"podman pull: Error: initializing source: manifest unknown",
	} {
		if instance.CgroupError(errors.New(text)) {
			t.Errorf("read as a cgroup error: %s", text)
		}
	}
}

// The engine's error for a limit it cannot apply never blames the port: `up` says the cgroup
// controllers are not delegated and gives the fix, beside the engine's own sentence.
func TestUpNeverBlamesThePortForTheMemoryMaxError(t *testing.T) {
	s := fresh("podman", healthPrefix, 1)
	s.On("podman run -d --name yawble --pod", crunMemoryMax)
	var out bytes.Buffer
	err := instance.Up(context.Background(), engine.NewPodman(s), keyed(), healthy, &out)
	if err == nil {
		t.Fatal("up succeeded although every run was refused")
	}
	msg := err.Error()
	if !strings.Contains(msg, crunLine) {
		t.Errorf("the engine's own sentence is missing:\n%s", msg)
	}
	for _, want := range []string{"cannot apply the container's memory/CPU limit because the cgroup controllers (memory, cpu, pids) are not delegated", "this is not the port", "cgroup.subtree_control", "cgroupControllers"} {
		if !strings.Contains(msg, want) {
			t.Errorf("error lacks %q:\n%s", want, msg)
		}
	}
	for _, refused := range []string{"usual cause is port", "port 8080", "config set port", "preview ports could not be published"} {
		if strings.Contains(msg, refused) || (refused != "port 8080" && strings.Contains(out.String(), refused)) {
			t.Errorf("the port is blamed (%q):\n%s\n%s", refused, msg, out.String())
		}
	}
}

// A port error keeps the port sentence.
func TestUpStillOffersThePortForAnErrorAboutThePort(t *testing.T) {
	s := fresh("podman", healthPrefix, 1)
	s.On("podman run -d --name yawble --pod", engine.Result{Stderr: "Error: starting some containers: internal libpod error", ExitCode: 126})
	err := instance.Up(context.Background(), engine.NewPodman(s), keyed(), healthy, &bytes.Buffer{})
	if err == nil || !strings.Contains(err.Error(), "The usual cause is port 8080") || strings.Contains(err.Error(), "cgroup") {
		t.Errorf("%v", err)
	}
}

// Degrade: an engine that refuses the limits gets the instance without them, said clearly, and
// never the crun error. The preview ports are not blamed either: they stay published.
func TestUpRefusedTheLimitsStartsWithoutThemAndWarnsAndKeepsThePreviewPorts(t *testing.T) {
	st := previewed()
	st.EnvFile, st.EnvFileHash = "/c/yawble/env", "e1"
	st.KeyFile, st.KeyHash = "/c/yawble/worker.env", "0123456789ab"
	s := fresh("podman", healthPrefix, 1)
	s.OnSequence("podman run -d --name yawble --pod", crunMemoryMax, engine.Result{})
	var out bytes.Buffer
	if err := instance.Up(context.Background(), engine.NewPodman(s), st, healthy, &out); err != nil {
		t.Fatal(err)
	}
	text := out.String()
	if strings.Contains(text, "preview ports could not be published") {
		t.Errorf("the preview ports are blamed:\n%s", text)
	}
	if !strings.Contains(text, "starting WITHOUT them: no memory or CPU limit is applied") || !strings.Contains(text, "NO memory or CPU limit") || !strings.Contains(text, "Yawble is up at") {
		t.Errorf("output:\n%s", text)
	}
	control := runsOf(s, "podman run -d --name yawble --pod")
	if len(control) != 2 || !strings.Contains(control[0], "--memory 1536m --cpus 2") {
		t.Fatalf("control runs %q", control)
	}
	if strings.Contains(control[1], "--memory") || strings.Contains(control[1], "--cpus") || !strings.Contains(control[1], `"noLimits":true`) || !strings.Contains(control[1], "HARNESS_PUBLISHED_PREVIEW_PORTS=28081,28082,28083") {
		t.Errorf("control's second run %q", control[1])
	}
	worker := runsOf(s, "podman run -d --name yawble-worker-1")
	if len(worker) != 1 || strings.Contains(worker[0], "--memory") || strings.Contains(worker[0], "--cpus") {
		t.Errorf("the worker after a control without limits is made without them: %q", worker)
	}
}

// Degrade from the engine's own report: podman info with no controllers, the containers are
// made without limits from the start, and up says what is missing.
func TestUpOnAnEngineReportingNoControllersMakesTheContainersWithoutLimits(t *testing.T) {
	s := fresh("podman", healthPrefix, 1)
	s.On("podman info --format json", engine.Result{Stdout: `{"host":{"cgroupVersion":"v2","cgroupManager":"cgroupfs","cgroupControllers":[]}}`})
	var out bytes.Buffer
	if err := instance.Up(context.Background(), engine.NewPodman(s), keyed(), healthy, &out); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(out.String(), "podman info reports no memory, cpu, pids cgroup controller") {
		t.Errorf("output:\n%s", out.String())
	}
	for _, run := range runsOf(s, "podman run -d") {
		if strings.Contains(run, "--memory") || strings.Contains(run, "--cpus") {
			t.Errorf("made with limits: %s", run)
		}
	}
	if len(runsOf(s, "podman run -d")) != 2 {
		t.Errorf("runs %q", runsOf(s, "podman run -d"))
	}
}

// The controllers delegated: the limits apply, and nothing is said about them.
func TestUpOnAnEngineReportingTheControllersAppliesTheLimits(t *testing.T) {
	s := fresh("podman", healthPrefix, 1)
	s.On("podman info --format json", engine.Result{Stdout: `{"host":{"cgroupControllers":["cpuset","cpu","io","memory","pids"]}}`})
	var out bytes.Buffer
	if err := instance.Up(context.Background(), engine.NewPodman(s), keyed(), healthy, &out); err != nil {
		t.Fatal(err)
	}
	if strings.Contains(out.String(), "WITHOUT") {
		t.Errorf("output:\n%s", out.String())
	}
	if c := runsOf(s, "podman run -d --name yawble --pod"); len(c) != 1 || !strings.Contains(c[0], "--memory 1536m --cpus 2") {
		t.Errorf("control %q", c)
	}
}

// A control made without limits is made again with them once the engine reports the
// controllers delegated.
func TestAControlMadeWithoutLimitsIsReplacedOnceTheControllersAreDelegated(t *testing.T) {
	st := keyed()
	was := st
	was.NoLimits = true
	s := engine.NewScripted()
	s.OnSequence(inspect, engine.Result{Stdout: "running|" + img + "|" + label(was) + "\n"}, engine.Result{Stderr: "no such container", ExitCode: 125})
	workerAs(s, "running", st, 1)
	s.On("podman info --format json", engine.Result{Stdout: `{"host":{"cgroupControllers":["cpu","memory","pids"]}}`})
	var out bytes.Buffer
	if err := instance.Up(context.Background(), engine.NewPodman(s), st, healthy, &out); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(out.String(), "memory/CPU limits can be applied now") {
		t.Errorf("output:\n%s", out.String())
	}
	if c := runsOf(s, "podman run -d --name yawble --pod"); len(c) != 1 || !strings.Contains(c[0], "--memory 1536m") {
		t.Errorf("control %q", c)
	}
}

// A stopped control made with limits the engine can no longer apply is made again without them.
func TestAStoppedControlTheEngineCannotStartForTheLimitsIsMadeAgainWithoutThem(t *testing.T) {
	st := keyed()
	s := engine.NewScripted()
	s.OnSequence(inspect, engine.Result{Stdout: "exited|" + img + "|" + label(st) + "\n"})
	workerAs(s, "running", st, 1)
	s.On("podman start yawble", crunMemoryMax)
	var out bytes.Buffer
	if err := instance.Up(context.Background(), engine.NewPodman(s), st, healthy, &out); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(out.String(), "starting WITHOUT them") {
		t.Errorf("output:\n%s", out.String())
	}
	if c := runsOf(s, "podman run -d --name yawble --pod"); len(c) != 1 || strings.Contains(c[0], "--memory") {
		t.Errorf("control %q", c)
	}
	if len(runsOf(s, "podman rm -f yawble")) == 0 {
		t.Errorf("the stopped control was not removed: %q", s.Calls)
	}
}

// A worker the engine refuses for the limits, beside a control that has them, is made again
// without them, said.
func TestAWorkerRefusedForTheLimitsIsMadeWithoutThem(t *testing.T) {
	s := fresh("podman", healthPrefix, 1)
	s.OnSequence("podman run -d --name yawble-worker-1", crunMemoryMax, engine.Result{})
	var out bytes.Buffer
	if err := instance.Up(context.Background(), engine.NewPodman(s), keyed(), healthy, &out); err != nil {
		t.Fatal(err)
	}
	w := runsOf(s, "podman run -d --name yawble-worker-1")
	if len(w) != 2 || !strings.Contains(w[0], "--memory 12288m") || strings.Contains(w[1], "--memory") || !strings.Contains(out.String(), "starting WITHOUT them") {
		t.Errorf("worker runs %q\n%s", w, out.String())
	}
}

// The preview ports' failure is worded the same way: with previews configured and the limits
// refused on every run, the error is the cgroup controllers', never the preview ports' or the port's.
func TestWithPreviewPortsTheMemoryMaxErrorIsStillTheControllers(t *testing.T) {
	st := previewed()
	st.KeyFile, st.KeyHash = "/c/yawble/worker.env", "0123456789ab"
	s := fresh("podman", healthPrefix, 1)
	s.On("podman run -d --name yawble --pod", crunMemoryMax)
	var out bytes.Buffer
	err := instance.Up(context.Background(), engine.NewPodman(s), st, healthy, &out)
	if err == nil || !strings.Contains(err.Error(), "cgroup controllers (memory, cpu, pids) are not delegated") || strings.Contains(err.Error(), "usual cause is port") {
		t.Errorf("%v", err)
	}
	if strings.Contains(out.String(), "preview ports could not be published") {
		t.Errorf("the preview ports are blamed:\n%s", out.String())
	}
}
