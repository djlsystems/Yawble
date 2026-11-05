package cli_test

import (
	"encoding/json"
	"os"
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/cli"
	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/instance"
)

// B0088. A person ran a CLI from main 47edb1a5 on rootless WSL Podman 6.1.4 with
// cgroupControllers=[]: up made control and worker-1 without their limits and both ran healthy.
// Three side effects are pinned here, each over fixtures of the engines' own answers:
//   - doctor's running limit is the Host's, from a measured CPU count (13 on 14 CPUs), and names
//     where the count came from; 3 only when nothing was measured;
//   - the engine's stats zeros the missing controllers leave unreadable say "not measured", and
//     that row is not ok;
//   - the cgroups row warns, with the same fix, once the instance runs and answers without its
//     limits, and doctor exits 0; before then it FAILs and doctor exits 1.

func readFixture(t *testing.T, path string) string {
	t.Helper()
	b, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	return string(b)
}

// noLimitsSettings are the settings a no-limits up stamped on control and worker-1.
func noLimitsSettings() instance.Settings {
	s := settingsForImage(testImage)
	s.NoLimits = true
	return s
}

// withoutLimitsOn is an instance made without its limits on an engine reporting no controllers,
// control's health answering with codes, its Host's report the fixture, and every container's stats
// the engine's no-controllers answer.
func withoutLimitsOn(t *testing.T, program, hostReport string, codes ...int) (*engine.Scripted, cli.Deps) {
	t.Helper()
	return withoutLimitsControl(t, program, "running", hostReport, codes...)
}

// withoutLimitsControl is withoutLimitsOn with control in state: "running", "exited", or "absent"
// (the containers not made yet).
func withoutLimitsControl(t *testing.T, program, state, hostReport string, codes ...int) (*engine.Scripted, cli.Deps) {
	t.Helper()
	s := engine.NewScripted()
	info, stats := "podman-info-wsl-no-controllers.json", "podman-stats-no-controllers.json"
	version := "6.1.4\n"
	if program == "docker" {
		info, stats, version = "docker-info-no-limits.json", "docker-stats-no-controllers.json", "29.8.0\n"
	}
	s.On(program+" version", engine.Result{Stdout: version})
	s.On(program+" image exists", engine.Result{})
	s.On(program+" volume exists", engine.Result{})
	s.On(program+" pod exists", engine.Result{})
	s.On(program+" info --format json", engine.Result{Stdout: readFixture(t, "../engine/testdata/"+info)})
	st := noLimitsSettings()
	if state == "absent" {
		s.On(program+" container inspect", engine.Result{Stderr: "Error: no such container", ExitCode: 125})
	} else {
		s.On(program+" container inspect", engine.Result{Stdout: state + "|" + testImage + "|" + instance.SettingsLabel(st) + "\n"})
		scriptWorker(s, program, state, st, 1)
	}
	if program == "docker" {
		scriptWorkers(s, program, 1)
	}
	statsLine := program + " stats --no-stream --format json "
	if program == "docker" {
		statsLine = program + " stats --no-stream --format {{json .}} "
	}
	answer := engine.Result{Stdout: readFixture(t, "../engine/testdata/"+stats)}
	s.On(statsLine+instance.ContainerName, answer)
	s.On(statsLine+instance.WorkerName(1), answer)
	if hostReport != "" {
		s.On(program+" exec -e HARNESS_WORKER_KEY= yawble dotnet /app/Harness.Host.dll --doctor", engine.Result{Stdout: readFixture(t, "../doctor/testdata/"+hostReport)})
	}
	deps := stubbed(s, codes...)
	env := map[string]string{"YAWBLE_IMAGE": testImage, "YAWBLE_ENGINE": program}
	deps.Env = func(k string) string { return env[k] }
	deps.LookPath = lookPath("podman", "docker")
	return s, deps
}

func doctorJSONOf(t *testing.T, deps cli.Deps) (int, doctorJSON) {
	t.Helper()
	code, out, errOut := run(t, deps, "doctor", "--json")
	if strings.TrimSpace(out) == "" {
		t.Fatalf("no output: exit %d %s", code, errOut)
	}
	return code, parseDoctor(t, out)
}

// 1. With no cpu controller on a 14-CPU engine the running limit is 13, and the reason names where
// the count came from. 3 shows only when no CPU count was measured, and says so.
func TestDoctorRunningLimitWithoutTheCpuControllerIsFromTheMeasuredCpus(t *testing.T) {
	for _, program := range []string{"podman", "docker"} {
		_, deps := withoutLimitsOn(t, program, "host-doctor-no-cpu-controller.json")
		_, out, _ := run(t, deps, "doctor", "--details")
		want := "ok    running limit  13, from the workers bound (the Host's answer: sum of 1 worker's bound: worker-1 13 (CPU bound 13 = 14 CPUs reported by the engine - 1, as the container's cgroup does not say its CPUs; the container has no memory limit))"
		if !strings.Contains(out, want) {
			t.Errorf("%s: lacks %q:\n%s", program, want, out)
		}

		_, deps = withoutLimitsOn(t, program, "host-doctor-no-cpu-count.json")
		_, got := doctorJSONOf(t, deps)
		_, detail, _ := verdict(t, got, "running limit")
		if !strings.HasPrefix(detail, "3, from the workers bound") || !strings.Contains(detail, "CPU bound 3, as no CPU count could be measured: not the container's cgroup, the engine's figure or the processors the worker sees") {
			t.Errorf("%s: nothing measured: %s", program, detail)
		}
	}
}

// doctor's capacity row says where the running limit's CPU count came from when the container's
// cgroup does not say its CPUs: the 14 is the engine's figure, not a limit the container got.
func TestDoctorCapacitySaysTheContainerGotNoCpuLimitWhenTheCountIsNotFromItsCgroup(t *testing.T) {
	for _, program := range []string{"podman", "docker"} {
		for _, c := range []struct{ fixture, want string }{
			{"host-doctor-no-cpu-controller.json", "the container got no memory limit and no CPU limit its cgroup says (the running limit counts 14 CPUs reported by the engine)"},
			{"host-doctor-no-cpu-count.json", "the container got no memory limit, CPUs not known (the Host's cgroup reading)"},
		} {
			_, deps := withoutLimitsOn(t, program, c.fixture)
			_, out, _ := run(t, deps, "doctor", "--details")
			if !strings.Contains(out, c.want) {
				t.Errorf("%s %s: lacks %q:\n%s", program, c.fixture, c.want, out)
			}
			if strings.Contains(out, "14 CPUs (the Host's cgroup reading)") {
				t.Errorf("%s %s: says the container got the engine's CPUs:\n%s", program, c.fixture, out)
			}
		}
	}
}

// up hands control and every worker the engine's measured CPUs, the Host's second source; an engine
// that could not be measured hands nothing, so nothing estimated reaches the Host.
func TestUpHandsTheEnginesMeasuredCpusToControlAndEachWorker(t *testing.T) {
	s, deps := dockerUp("linux")
	deps.ConfigDir = t.TempDir()
	if code, out, errOut := run(t, deps, "up", "--yes"); code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	runs := 0
	for _, call := range s.Calls {
		if strings.HasPrefix(call, "docker run ") {
			runs++
			if !strings.Contains(call, "-e "+instance.EngineCPUsVariable+"=10 ") {
				t.Errorf("a container is not handed the engine's 10 CPUs: %s", call)
			}
		}
	}
	if runs < 2 {
		t.Fatalf("control and worker-1 not both run: %d\n%s", runs, calls(s))
	}

	// A fresh script: the first registered prefix wins a tie, so dockerUp's answer cannot be replaced.
	unmeasured := engine.NewScripted()
	unmeasured.On("docker info", engine.Result{Stderr: "Cannot connect to the Docker daemon", ExitCode: 1})
	unmeasured.On("docker container inspect", engine.Result{Stderr: "Error: No such container: yawble", ExitCode: 1})
	unmeasured.On("docker image inspect", engine.Result{})
	deps = stubbed(unmeasured)
	deps.GOOS, deps.LookPath, deps.ConfigDir = "linux", lookPath("docker"), t.TempDir()
	run(t, deps, "up", "--yes")
	if c := calls(unmeasured); !strings.Contains(c, "docker run ") || strings.Contains(c, instance.EngineCPUsVariable) {
		t.Errorf("an unmeasured engine's CPUs were handed in:\n%s", c)
	}
}

// 2. The engine's stats zeros with no memory and pids controller read "not measured", in status and
// in doctor, and doctor's rows for them are not ok.
func TestStatusAndDoctorSayTheMissingControllersZerosAreNotMeasured(t *testing.T) {
	for _, program := range []string{"podman", "docker"} {
		_, deps := withoutLimitsOn(t, program, "host-doctor-no-cpu-controller.json")
		code, out, errOut := run(t, deps, "status")
		if code != 0 {
			t.Fatalf("%s: status exit %d %s", program, code, errOut)
		}
		for _, want := range []string{"memory not measured (no memory cgroup controller) /", "pids not measured (no pids cgroup controller)"} {
			if strings.Count(out, want) != 2 {
				t.Errorf("%s: status should say %q for control and worker-1:\n%s", program, want, out)
			}
		}
		if strings.Contains(out, "memory 0B") || strings.Contains(out, "pids 0 ") {
			t.Errorf("%s: status shows a zero:\n%s", program, out)
		}

		_, out, _ = run(t, deps, "status", "--json")
		var doc struct {
			Stats   *engine.Stats `json:"stats"`
			Workers []struct {
				Stats *engine.Stats `json:"stats"`
			} `json:"workers"`
		}
		if err := json.Unmarshal([]byte(out), &doc); err != nil || doc.Stats == nil || len(doc.Workers) != 1 || doc.Workers[0].Stats == nil {
			t.Fatalf("%s: %v %s", program, err, out)
		}
		for _, st := range []*engine.Stats{doc.Stats, doc.Workers[0].Stats} {
			if st.MemoryUsageBytes != nil || st.PIDs != nil || st.MemoryPercent != nil || strings.Join(st.Unreadable, ",") != "memory,pids" {
				t.Errorf("%s: json carries a zero: %+v", program, st)
			}
		}

		_, got := doctorJSONOf(t, deps)
		for _, row := range []string{"stats", "worker-1"} {
			v, detail, fix := verdict(t, got, row)
			if v == "ok" || !strings.Contains(detail, "memory not measured (no memory cgroup controller)") || !strings.Contains(detail, "pids not measured (no pids cgroup controller)") || fix == "" {
				t.Errorf("%s: %s row: %s %q fix %q", program, row, v, detail, fix)
			}
		}
	}
}

// 3. Running and answering without its limits, the cgroups row warns with the same fix and doctor
// exits 0.
func TestDoctorWarnsAndExitsZeroWhileTheInstanceRunsHealthyWithoutLimits(t *testing.T) {
	for _, program := range []string{"podman", "docker"} {
		_, deps := withoutLimitsOn(t, program, "host-doctor-no-cpu-controller.json")
		code, got := doctorJSONOf(t, deps)
		v, detail, fix := verdict(t, got, "cgroups")
		if v != "warn" || !strings.Contains(detail, "the instance runs and answers WITHOUT the containers' limits") || fix != instance.LimitsFix(program) {
			t.Errorf("%s: cgroups %s %q fix %q", program, v, detail, fix)
		}
		if code != 0 {
			t.Errorf("%s: doctor exits %d for a working instance without limits; failed: %+v", program, code, failed(got))
		}
	}
}

// 3. Before the containers are made, or when control will not start or answer, the cgroups row is
// still a FAIL and doctor exits 1.
func TestDoctorStillFailsCgroupsBeforeTheInstanceRunsWithoutLimits(t *testing.T) {
	for _, program := range []string{"podman", "docker"} {
		_, deps := withoutLimitsControl(t, program, "absent", "")
		_, stoppedDeps := withoutLimitsControl(t, program, "exited", "")
		_, notAnswering := withoutLimitsOn(t, program, "host-doctor-no-cpu-controller.json", 503)
		for name, d := range map[string]cli.Deps{"not made": deps, "stopped": stoppedDeps, "not answering": notAnswering} {
			code, got := doctorJSONOf(t, d)
			if v, _, fix := verdict(t, got, "cgroups"); v != "FAIL" || fix != instance.LimitsFix(program) {
				t.Errorf("%s %s: cgroups %s fix %q", program, name, v, fix)
			}
			if code != 1 {
				t.Errorf("%s %s: exit %d", program, name, code)
			}
		}
	}
}

func failed(got doctorJSON) []string {
	var names []string
	for _, c := range got.Checks {
		if c.Verdict == "FAIL" {
			names = append(names, c.Name+": "+c.Detail)
		}
	}
	return names
}
