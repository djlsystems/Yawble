package instance_test

// Independent edge cases: several controllers at once, other quotings of the runtimes'
// "controller not available" words, and a worker kept without limits whose settings change.

import (
	"bytes"
	"context"
	"errors"
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/instance"
)

// The report missing several controllers names exactly those, and the limits they carry.
func TestCgroupEdgeSeveralControllersMissingNamesExactlyThose(t *testing.T) {
	for _, c := range []struct{ have, want string }{
		{`["cpu"]`, "reports no memory, pids cgroup controller, so it cannot apply the containers' memory and process (pids) limits;"},
		{`[]`, "reports no memory, cpu, pids cgroup controller, so it cannot apply the containers' memory, CPU and process (pids) limits;"},
		{`["memory","pids"]`, "reports no cpu cgroup controller, so it cannot apply the containers' CPU limit;"},
	} {
		s := fresh("podman", healthPrefix, 1)
		s.On("podman info --format json", engine.Result{Stdout: `{"host":{"cgroupControllers":` + c.have + `}}`})
		var out bytes.Buffer
		if err := instance.Up(context.Background(), engine.NewPodman(s), keyed(), healthy, &out); err != nil {
			t.Fatal(err)
		}
		if !strings.Contains(out.String(), c.want) {
			t.Errorf("have %s:\n%s", c.have, out.String())
		}
	}
}

// One refusal naming two controllers (a cgroup file and a controller) names both, in order.
func TestCgroupEdgeARefusalNamingTwoControllersNamesBoth(t *testing.T) {
	s := fresh("podman", healthPrefix, 1)
	s.OnSequence("podman run -d --name yawble --pod", engine.Result{Stderr: "Error: crun: controller `pids` is not available under /sys/fs/cgroup/cgroup.controllers; open `memory.max` for writing: No such file or directory", ExitCode: 126}, engine.Result{})
	var out bytes.Buffer
	if err := instance.Up(context.Background(), engine.NewPodman(s), keyed(), healthy, &out); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(out.String(), "the engine refused the containers' memory and process (pids) limits: no memory, pids cgroup controller is delegated to it") {
		t.Errorf("output:\n%s", out.String())
	}
}

// Other quotings and wordings of the same runtime refusal are cgroup errors, and up names the
// controller from them.
func TestCgroupEdgeControllerNotAvailableInOtherQuotings(t *testing.T) {
	for _, text := range []string{
		"crun: controller 'pids' is not available",
		"crun: controller pids is not available",
		"runc: controller \"memory\" is not enabled",
		"runc create failed: controller \"cpu\" not supported",
		"Controller `PIDS` Is Not Available",
		"runc create failed: unable to set memory limit to 67108864 (current usage: 0, peak usage: 0)",
		"runc create failed: open /sys/fs/cgroup/user.slice/pids.max: no such file or directory",
	} {
		if !instance.CgroupError(errors.New(text)) {
			t.Errorf("not read as a cgroup error: %s", text)
		}
	}
}

// Docker's CLI prints the OCI runtime's error with its double quotes escaped; it is still a
// cgroup error.
func TestCgroupEdgeRuncEscapedQuotesIsACgroupError(t *testing.T) {
	text := `docker: Error response from daemon: failed to create task for container: failed to create shim task: OCI runtime create failed: runc create failed: unable to start container process: unable to apply cgroup configuration: controller \"pids\" not available: unknown.`
	if !instance.CgroupError(errors.New(text)) {
		t.Fatalf("not read as a cgroup error: %s", text)
	}
}

// keptWorker scripts control running with its limits beside worker 1 kept without them, as
// recorded with the report seen.
func keptWorker(control, worker instance.Settings, seen string) *engine.Scripted {
	w := worker
	w.NoLimits, w.CgroupsSeen = true, seen
	s := engine.NewScripted()
	s.On(inspect, engine.Result{Stdout: "running|" + img + "|" + label(control) + "\n"})
	workerAs(s, "running", w, 1)
	return s
}

// A kept worker whose settings change is made again with its limits (the change said); refused
// again on its own, it is made once more without them and records the report; a further up with
// those settings and that report leaves it alone.
func TestCgroupEdgeAKeptWorkerWhoseSettingsChangeIsMadeAgainOnce(t *testing.T) {
	old := keyed()
	st := keyed()
	st.Memory = "8192m"
	full := engine.Result{Stdout: `{"host":{"cgroupControllers":["cpu","memory","pids"]}}`}
	// control already matches the new settings, to isolate the worker.
	s := keptWorker(st, old, "has: memory,cpu,pids")
	s.On("podman info --format json", full)
	s.OnSequence("podman run -d --name yawble-worker-1", crunMemoryMax, engine.Result{})
	var out bytes.Buffer
	if err := instance.Up(context.Background(), engine.NewPodman(s), st, healthy, &out); err != nil {
		t.Fatal(err)
	}
	runs := runsOf(s, "podman run -d --name yawble-worker-1")
	if len(runs) != 2 || !strings.Contains(runs[0], "--memory 8192m") || strings.Contains(runs[1], "--memory") || !strings.Contains(out.String(), "memory 12288m -> 8192m") {
		t.Fatalf("runs %q\n%s", runs, out.String())
	}
	if strings.Contains(out.String(), "it is kept as it is") {
		t.Errorf("said kept though its settings changed:\n%s", out.String())
	}
	newLabel := settingsLabelOf(t, runs[1])
	if !strings.Contains(newLabel, `"cgroups":"has: memory,cpu,pids"`) || !strings.Contains(newLabel, `"memory":"8192m"`) {
		t.Errorf("label %s", newLabel)
	}

	ref, _ := st.WorkerRef()
	again := engine.NewScripted()
	again.On(inspect+"-worker-1", engine.Result{Stdout: "running|" + ref + "|" + newLabel + "\n"})
	again.On(healthPrefix+"yawble-worker-1", engine.Result{Stdout: "healthy\n"})
	again.On(inspect, engine.Result{Stdout: "running|" + img + "|" + label(st) + "\n"})
	again.On("podman info --format json", full)
	out.Reset()
	if err := instance.Up(context.Background(), engine.NewPodman(again), st, healthy, &out); err != nil {
		t.Fatal(err)
	}
	if len(runsOf(again, "podman run -d")) != 0 || len(runsOf(again, "podman rm")) != 0 {
		t.Errorf("remade on the next up: %q\n%s", again.Calls, out.String())
	}
}

// A kept worker beside a measured report that has not changed is left alone, on any number of
// ups.
func TestCgroupEdgeAKeptWorkerWithAMeasuredReportIsLeftAloneUntilItChanges(t *testing.T) {
	st := keyed()
	for i := 0; i < 3; i++ {
		s := keptWorker(st, st, "has: memory,cpu,pids")
		s.On("podman info --format json", engine.Result{Stdout: `{"host":{"cgroupControllers":["memory","cpu","pids"]}}`})
		var out bytes.Buffer
		if err := instance.Up(context.Background(), engine.NewPodman(s), st, healthy, &out); err != nil {
			t.Fatal(err)
		}
		if len(runsOf(s, "podman run -d")) != 0 || len(runsOf(s, "podman rm")) != 0 || !strings.Contains(out.String(), "it is kept as it is") {
			t.Fatalf("up %d: %q\n%s", i, s.Calls, out.String())
		}
	}
}
