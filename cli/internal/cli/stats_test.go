package cli_test

import (
	"encoding/json"
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/engine"
)

const (
	podmanStats     = "podman stats --no-stream --format json yawble"
	dockerStats     = "docker stats --no-stream --format {{json .}} yawble"
	podmanStatsJSON = `[{"id":"3f2a","name":"yawble","cpu_percent":"12.34%","mem_usage":"1.2GB / 12.88GB","mem_percent":"9.32%","net_io":"1kB / 2kB","block_io":"0B / 0B","pids":"412"}]`
	dockerStatsJSON = `{"BlockIO":"0B / 0B","CPUPerc":"250.50%","Container":"yawble","MemPerc":"93.75%","MemUsage":"7.5GiB / 8GiB","Name":"yawble","NetIO":"1kB / 2kB","PIDs":"37"}`
)

// runningOn is a running, healthy instance on one engine, configured as that engine.
func runningOn(t *testing.T, name string) (*engine.Scripted, func(args ...string) (int, string, string)) {
	t.Helper()
	s := engine.NewScripted()
	if name == "docker" {
		s.On("docker version", engine.Result{Stdout: "29.8.0\n"})
		s.On("docker container inspect", engine.Result{Stdout: "running|" + testImage + "|" + currentLabel() + "\n"})
		s.On("docker exec -e HARNESS_WORKER_KEY= yawble dotnet /app/Harness.Host.dll --doctor", engine.Result{Stdout: doctorStdout})
	} else {
		s = runningScript()
		s.On(doctorExec, engine.Result{Stdout: doctorStdout})
	}
	deps := stubbed(s)
	env := map[string]string{"YAWBLE_IMAGE": testImage, "YAWBLE_ENGINE": name}
	deps.Env = func(k string) string { return env[k] }
	deps.LookPath = lookPath("podman", "docker")
	return s, func(args ...string) (int, string, string) { return run(t, deps, args...) }
}

func TestStatusShowsTheEnginesOwnStatsOnBothEngines(t *testing.T) {
	cases := []struct{ engine, line, answer, want string }{
		{"podman", podmanStats, podmanStatsJSON, "stats      cpu 12.3%  memory 1.2GB / 12.88GB (9%)  pids 412  (podman stats)\n"},
		{"docker", dockerStats, dockerStatsJSON, "stats      cpu 250.5%  memory 7.5GiB / 8GiB (94%)  pids 37  (docker stats)\n"},
	}
	for _, c := range cases {
		s, yawble := runningOn(t, c.engine)
		s.On(c.line, engine.Result{Stdout: c.answer})
		code, out, errOut := yawble("status")
		if code != 0 || !strings.Contains(out, c.want) {
			t.Errorf("%s: exit %d %s\n%s", c.engine, code, errOut, out)
		}
		if !strings.Contains(strings.Join(s.Calls, "\n"), c.line) {
			t.Errorf("%s: stats not asked: %q", c.engine, s.Calls)
		}
		other := map[string]string{"podman": "docker ", "docker": "podman "}[c.engine]
		for _, call := range s.Calls {
			if strings.HasPrefix(call, other) {
				t.Errorf("%s: asked the other engine: %q", c.engine, call)
			}
		}

		code, out, _ = yawble("status", "--json")
		var got struct {
			Container string        `json:"container"`
			Stats     *engine.Stats `json:"stats"`
		}
		if err := json.Unmarshal([]byte(out), &got); err != nil || code != 0 {
			t.Fatalf("%s: %v %s", c.engine, err, out)
		}
		if got.Container != "running" || got.Stats == nil || got.Stats.Command != c.engine+" stats" || got.Stats.CPUPercent == nil || got.Stats.MemoryLimitBytes == nil {
			t.Errorf("%s json: %s", c.engine, out)
		}
	}
}

func TestStatusOfAStoppedContainerSaysSoAndDoesNotAskForStats(t *testing.T) {
	for _, name := range []string{"podman", "docker"} {
		s := engine.NewScripted()
		s.On(name+" version", engine.Result{Stdout: "1.0\n"})
		s.On(name+" container inspect", engine.Result{Stdout: "exited|" + testImage + "|\n"})
		deps := stubbed(s)
		env := map[string]string{"YAWBLE_IMAGE": testImage, "YAWBLE_ENGINE": name}
		deps.Env = func(k string) string { return env[k] }
		code, out, _ := run(t, deps, "status")
		if code != 0 || !strings.Contains(out, "stats      the container is not running\n") {
			t.Errorf("%s: exit %d\n%s", name, code, out)
		}
		for _, call := range s.Calls {
			if strings.Contains(call, " stats ") {
				t.Errorf("%s: asked for stats of a stopped container: %q", name, call)
			}
		}
	}
}

// A stats command that fails is "not measured", with the engine's reason, and status still
// answers with exit 0.
func TestStatusWhenStatsFailsSaysNotMeasuredAndStillSucceeds(t *testing.T) {
	cases := []struct{ engine, line string }{{"podman", podmanStats}, {"docker", dockerStats}}
	for _, c := range cases {
		s, yawble := runningOn(t, c.engine)
		s.On(c.line, engine.Result{Stderr: "Error: cgroups v1 is not supported", ExitCode: 125})
		code, out, errOut := yawble("status")
		want := "stats      not measured: " + c.engine + " stats yawble: Error: cgroups v1 is not supported (exit 125)  (" + c.engine + " stats)\n"
		if code != 0 || !strings.Contains(out, want) || !strings.Contains(out, "health     healthy") {
			t.Errorf("%s: exit %d %s\n%s", c.engine, code, errOut, out)
		}
	}
}

func TestDoctorShowsTheEnginesOwnStatsOnBothEngines(t *testing.T) {
	cases := []struct{ engine, line, answer, want string }{
		{"podman", podmanStats, podmanStatsJSON, "ok    stats          cpu 12.3%  memory 1.2GB / 12.88GB (9%)  pids 412  (podman stats)\n"},
		{"docker", dockerStats, dockerStatsJSON, "warn  stats          cpu 250.5%  memory 7.5GiB / 8GiB (94%)  pids 37  (docker stats)\n"},
	}
	for _, c := range cases {
		s, yawble := runningOn(t, c.engine)
		s.On(c.line, engine.Result{Stdout: c.answer})
		code, out, errOut := yawble("doctor")
		if code != 0 || !strings.Contains(out, c.want) {
			t.Errorf("%s: exit %d %s\n%s", c.engine, code, errOut, out)
		}
	}
}

func TestDoctorWhenStatsFailsOrTheContainerIsStoppedSkipsTheRow(t *testing.T) {
	for _, c := range []struct{ engine, line string }{{"podman", podmanStats}, {"docker", dockerStats}} {
		s, yawble := runningOn(t, c.engine)
		s.On(c.line, engine.Result{Stderr: "Error: cgroups v1 is not supported", ExitCode: 125})
		code, out, _ := yawble("doctor", "--json")
		if code != 0 {
			t.Fatalf("%s: exit %d %s", c.engine, code, out)
		}
		if v, detail, _ := verdict(t, parseDoctor(t, out), "stats"); v != "skip" || !strings.HasPrefix(detail, "not measured: "+c.engine+" stats yawble: ") {
			t.Errorf("%s failed: %s %q", c.engine, v, detail)
		}
	}
	code, out, _ := run(t, stubbed(stoppedScript()), "doctor", "--json")
	if code != 1 {
		t.Fatalf("stopped: exit %d", code)
	}
	if v, detail, _ := verdict(t, parseDoctor(t, out), "stats"); v != "skip" || detail != "the container is not running" {
		t.Errorf("stopped: %s %q", v, detail)
	}
}
