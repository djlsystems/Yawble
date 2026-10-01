package cli_test

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/cli"
	"github.com/djlsystems/yawble/cli/internal/config"
	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/instance"
)

const (
	testImage    = "ghcr.io/djlsystems/yawble:2026.09.24.1"
	doctorExec   = "podman exec yawble dotnet /app/Harness.Host.dll --doctor"
	doctorStdout = "Host log: /data/logs/x\n{\"at\":\"2026-09-23T21:04:19+00:00\",\"dataRoot\":{\"path\":\"/data\",\"writable\":true,\"freeBytes\":1000000000000},\"database\":{\"path\":\"/data/messages.db\",\"exists\":true,\"schema\":{\"applied\":[\"a\",\"b\"],\"pending\":[],\"unknown\":[],\"accepted\":true},\"error\":null},\"backups\":{\"directory\":\"/data/backups\",\"dailyCount\":1,\"newestDailyAt\":\"2026-09-23T18:47:24+00:00\"},\"versionsRecordedAt\":null,\"agents\":[{\"agent\":\"claude\",\"installed\":true,\"version\":\"2.1.280\",\"authenticated\":true,\"detail\":\"saved login\",\"credentialVariable\":\"ANTHROPIC_API_KEY\"},{\"agent\":\"codex\",\"installed\":true,\"version\":null,\"authenticated\":false,\"detail\":\"exit 1\",\"credentialVariable\":\"OPENAI_API_KEY\"}]}\n"
)

type brokenRunner struct{}

func (brokenRunner) Run(context.Context, string, ...string) (engine.Result, error) {
	return engine.Result{}, errors.New(`exec: "podman": executable file not found in $PATH`)
}

// healthStub answers /healthz from a script of status codes, repeating the last one. No test in
// this package touches the network: a real Yawble on this machine must not make a test pass.
type healthStub struct {
	codes []int
	calls int
}

func (h *healthStub) RoundTrip(*http.Request) (*http.Response, error) {
	code := h.codes[min(h.calls, len(h.codes)-1)]
	h.calls++
	return &http.Response{StatusCode: code, Body: io.NopCloser(strings.NewReader(""))}, nil
}

func stubbed(runner engine.Runner, codes ...int) cli.Deps {
	if len(codes) == 0 {
		codes = []int{200}
	}
	env := map[string]string{"YAWBLE_IMAGE": testImage}
	return cli.Deps{
		Runner:   runner,
		Env:      func(k string) string { return env[k] },
		HTTP:     &http.Client{Transport: &healthStub{codes: codes}},
		PortFree: func(int) bool { return true },
		// Linux: no machine, so the tests that are not about the machine stay the same on every
		// developer's OS. The machine tests set "windows" or "darwin" themselves.
		GOOS: "linux",
		// A supported Mac when a test says "darwin": macOS is Apple silicon only.
		GOARCH: "arm64",
	}
}

// currentLabel is what a real `up` on this machine would have stamped: the same derivation
// prepare() makes, so the scripted container reads as "made by this config".
func currentLabel() string {
	s, _ := instance.Defaults(config.Config{Image: testImage}, instance.Measure(), "")
	return instance.SettingsLabel(s)
}

func runningScript() *engine.Scripted {
	s := engine.NewScripted()
	s.On("podman version", engine.Result{Stdout: "6.0.2\n"})
	s.On("podman image exists", engine.Result{})
	s.On("podman volume exists", engine.Result{})
	s.On("podman pod exists", engine.Result{})
	s.On("podman container inspect", engine.Result{Stdout: "running|" + testImage + "|" + currentLabel() + "\n"})
	return s
}

func stoppedScript() *engine.Scripted {
	s := engine.NewScripted()
	s.On("podman version", engine.Result{Stdout: "6.0.2\n"})
	s.On("podman image exists", engine.Result{})
	s.On("podman volume exists", engine.Result{})
	s.On("podman pod exists", engine.Result{})
	s.On("podman container inspect", engine.Result{Stdout: "exited|" + testImage + "|" + currentLabel() + "\n"})
	return s
}

type doctorJSON struct {
	Checks []struct {
		Name, Verdict, Detail, Fix string
	} `json:"checks"`
	Fixed    []string         `json:"fixed"`
	Instance *json.RawMessage `json:"instance"`
}

func parseDoctor(t *testing.T, out string) doctorJSON {
	t.Helper()
	var got doctorJSON
	if err := json.Unmarshal([]byte(out), &got); err != nil {
		t.Fatalf("not json: %v\n%s", err, out)
	}
	return got
}

func verdict(t *testing.T, checks doctorJSON, name string) (string, string, string) {
	t.Helper()
	for _, c := range checks.Checks {
		if c.Name == name {
			return c.Verdict, c.Detail, c.Fix
		}
	}
	t.Fatalf("no check %q", name)
	return "", "", ""
}

// Review Focus 1: podman is not there at all.
func TestDoctorWithoutPodmanFailsTheEngineSkipsTheRestAndExitsOne(t *testing.T) {
	code, out, _ := run(t, stubbed(brokenRunner{}), "doctor", "--json")
	if code != 1 {
		t.Fatalf("exit %d: %s", code, out)
	}
	got := parseDoctor(t, out)
	if v, _, fix := verdict(t, got, "engine"); v != "FAIL" || !strings.Contains(fix, "install Podman") {
		t.Errorf("engine %s %s", v, fix)
	}
	for _, name := range []string{"volume", "container", "health", "data root", "agents"} {
		if v, _, _ := verdict(t, got, name); v != "skip" {
			t.Errorf("%s should be skip, got %s", name, v)
		}
	}
}

// Podman is installed but its engine (the machine, the service) is not reachable: the fix is not
// "install Podman".
func TestDoctorWithAnUnreachableEngineSaysStartItNotInstallIt(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman version", engine.Result{Stderr: "Cannot connect to Podman. Please verify your connection to the Linux system", ExitCode: 125})
	code, out, _ := run(t, stubbed(s), "doctor", "--json")
	if code != 1 {
		t.Fatalf("exit %d: %s", code, out)
	}
	v, detail, fix := verdict(t, parseDoctor(t, out), "engine")
	if v != "FAIL" || strings.Contains(fix, "install") || !strings.Contains(fix, "podman machine start") || !strings.Contains(detail, "Cannot connect") {
		t.Errorf("engine %s %q %q", v, detail, fix)
	}
}

// Review Focus 2: a stopped container.
func TestDoctorOnAStoppedInstanceFailsTheContainerAndSkipsTheInstanceChecks(t *testing.T) {
	s := stoppedScript()
	code, out, _ := run(t, stubbed(s), "doctor", "--json")
	if code != 1 {
		t.Fatalf("exit %d: %s", code, out)
	}
	got := parseDoctor(t, out)
	if v, _, fix := verdict(t, got, "container"); v != "FAIL" || !strings.Contains(fix, "yawble up") {
		t.Errorf("container %s %s", v, fix)
	}
	for _, name := range []string{"health", "data root", "database", "backups", "agents"} {
		if v, _, _ := verdict(t, got, name); v != "skip" {
			t.Errorf("%s should be skip, got %s", name, v)
		}
	}
	for _, c := range s.Calls {
		if strings.HasPrefix(c, "podman exec") {
			t.Errorf("must not exec into a stopped container: %q", c)
		}
	}
}

// After `down` the pod still holds the port. That is ours, not "something else".
func TestDoctorAfterDownDoesNotBlameOurOwnPodForThePort(t *testing.T) {
	deps := stubbed(stoppedScript())
	deps.PortFree = func(int) bool { return false }
	_, out, _ := run(t, deps, "doctor", "--json")
	v, detail, _ := verdict(t, parseDoctor(t, out), "port")
	if v != "skip" || !strings.Contains(detail, "pod") {
		t.Errorf("port %s %q", v, detail)
	}
}

func TestDoctorWithNoPodAndAHeldPortWarns(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman version", engine.Result{Stdout: "6.0.2\n"})
	s.On("podman image exists", engine.Result{})
	s.On("podman volume exists", engine.Result{})
	s.On("podman pod exists", engine.Result{ExitCode: 1})
	s.On("podman container inspect", engine.Result{Stderr: "no such container", ExitCode: 125})
	deps := stubbed(s)
	deps.PortFree = func(int) bool { return false }
	_, out, _ := run(t, deps, "doctor", "--json")
	v, _, fix := verdict(t, parseDoctor(t, out), "port")
	if v != "warn" || !strings.Contains(fix, "config set port") {
		t.Errorf("port %s %s", v, fix)
	}
}

func TestDoctorFixStartsAStoppedContainerWaitsForHealthAndReportsIt(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman version", engine.Result{Stdout: "6.0.2\n"})
	s.On("podman image exists", engine.Result{})
	s.On("podman volume exists", engine.Result{})
	s.On("podman pod exists", engine.Result{})
	s.OnSequence("podman container inspect",
		engine.Result{Stdout: "exited|" + testImage + "|" + currentLabel() + "\n"},
		engine.Result{Stdout: "running|" + testImage + "|" + currentLabel() + "\n"},
	)
	s.On(doctorExec, engine.Result{Stdout: doctorStdout})
	// The Host is not up the instant `podman start` returns: the first health poll is 503.
	code, out, _ := run(t, stubbed(s, 503, 200), "doctor", "--fix")
	if code != 0 {
		t.Fatalf("exit %d: %s", code, out)
	}
	if !strings.Contains(strings.Join(s.Calls, "\n"), "podman start yawble") {
		t.Errorf("calls:\n%s", strings.Join(s.Calls, "\n"))
	}
	if !strings.Contains(out, "started yawble") || !strings.Contains(out, "ok    health") {
		t.Errorf("out:\n%s", out)
	}
}

func TestDoctorFixJSONIsStillJSONAndNamesWhatItFixed(t *testing.T) {
	s2 := engine.NewScripted()
	s2.On("podman version", engine.Result{Stdout: "6.0.2\n"})
	s2.On("podman image exists", engine.Result{})
	// Missing on the first check, present after --fix creates it.
	s2.OnSequence("podman volume exists", engine.Result{ExitCode: 1}, engine.Result{})
	s2.On("podman pod exists", engine.Result{})
	s2.OnSequence("podman container inspect",
		engine.Result{Stdout: "exited|" + testImage + "|" + currentLabel() + "\n"},
		engine.Result{Stdout: "running|" + testImage + "|" + currentLabel() + "\n"},
	)
	s2.On(doctorExec, engine.Result{Stdout: doctorStdout})
	code, out, _ := run(t, stubbed(s2), "doctor", "--fix", "--json")
	if code != 0 {
		t.Fatalf("exit %d: %s", code, out)
	}
	got := parseDoctor(t, out) // fails the test if any prose precedes the JSON
	if len(got.Fixed) != 2 || !strings.Contains(got.Fixed[0], "volume") || !strings.Contains(got.Fixed[1], "started") {
		t.Errorf("fixed %v", got.Fixed)
	}
}

func TestDoctorOnAHealthyInstanceRunsTheHostsDoctorAndExitsZero(t *testing.T) {
	s := runningScript()
	s.On(doctorExec, engine.Result{Stdout: doctorStdout})
	code, out, errOut := run(t, stubbed(s), "doctor")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	for _, want := range []string{"ok    engine         podman 6.0.2", "ok    container      running", "ok    data root", "ok    database       schema accepted (2 steps)", "warn  agents", "codex NOT signed in", "fix: yawble agents"} {
		if !strings.Contains(out, want) {
			t.Errorf("output lacks %q:\n%s", want, out)
		}
	}
	if !strings.Contains(strings.Join(s.Calls, "\n"), doctorExec) {
		t.Errorf("the Host's doctor was not run:\n%s", strings.Join(s.Calls, "\n"))
	}
}

func TestDoctorUnhealthyFailsWithoutTouchingTheNetwork(t *testing.T) {
	s := runningScript()
	s.On(doctorExec, engine.Result{Stdout: doctorStdout})
	code, out, _ := run(t, stubbed(s, 503), "doctor", "--json")
	if code != 1 {
		t.Fatalf("exit %d: %s", code, out)
	}
	if v, _, fix := verdict(t, parseDoctor(t, out), "health"); v != "FAIL" || !strings.Contains(fix, "yawble logs") {
		t.Errorf("health %s %s", v, fix)
	}
}

// Review Focus 3: an image whose Host does not know --doctor. Measured: the Host ignores the
// switch, tries to serve, is refused by the data-root lock the live Host holds, and exits 1.
func TestDoctorAgainstAnOlderImageSkipsTheInstanceChecksWithTheReason(t *testing.T) {
	s := runningScript()
	s.On(doctorExec, engine.Result{Stderr: "Another host is already using this data root.", ExitCode: 1})
	code, out, _ := run(t, stubbed(s), "doctor", "--json")
	if code != 0 {
		t.Fatalf("exit %d: %s", code, out)
	}
	got := parseDoctor(t, out)
	for _, name := range []string{"data root", "database", "agents"} {
		if v, detail, _ := verdict(t, got, name); v != "skip" || !strings.Contains(detail, "predates") {
			t.Errorf("%s should be skip with the reason, got %s %q", name, v, detail)
		}
	}
	if got.Instance != nil && string(*got.Instance) != "null" {
		t.Errorf("instance should be null when there is no report: %s", *got.Instance)
	}
}

const machineInspect = "podman machine inspect --format {{.Name}}|{{.State}}|{{.Rootful}}|{{.Resources.Memory}}|{{.Resources.CPUs}}"

// Review Focus 2 at the command: a stopped Windows machine. The engine rows are skip, the machine
// row FAILs, and --fix starts the machine before anything else.
func stoppedMachineScript() *engine.Scripted {
	s := engine.NewScripted()
	s.On("wsl --status", engine.Result{})
	// Asked twice while stopped (once by prepare's measurement, once by the observation), and
	// running once `podman machine start` has been run.
	s.OnSequence(machineInspect,
		engine.Result{Stdout: "podman-machine-default|stopped|false|2048|10\n"},
		engine.Result{Stdout: "podman-machine-default|stopped|false|2048|10\n"},
		engine.Result{Stdout: "podman-machine-default|running|false|2048|10\n"},
	)
	s.On("wsl -d podman-machine-default -e free -m", engine.Result{Stdout: "Mem: 15688 1 1\n"})
	s.On("podman version", engine.Result{Stdout: "6.0.2\n"})
	s.On("podman image exists", engine.Result{})
	s.On("podman volume exists", engine.Result{})
	s.On("podman pod exists", engine.Result{})
	s.On("podman container inspect", engine.Result{Stdout: "running|" + testImage + "|" + currentLabel() + "\n"})
	s.On(doctorExec, engine.Result{Stdout: doctorStdout})
	return s
}

// With Docker as the engine there is no Podman machine to mind, even when Podman is installed
// beside it: measured on a Mac with both, where `doctor --fix` started the Podman machine and
// reported on it while the instance ran on Docker. Podman is asked one thing only: whether it
// holds a data volume this instance does not use, which starts nothing.
func TestDoctorOnDockerNeverAsksOrStartsThePodmanMachine(t *testing.T) {
	s := engine.NewScripted()
	s.On(machineInspect, engine.Result{Stdout: "podman-machine-default|stopped|false|8192|6\n"})
	s.On("docker version", engine.Result{Stdout: "29.8.0\n"})
	s.On("docker info", engine.Result{Stdout: "8589934592|8\n"})
	s.On("docker image inspect", engine.Result{})
	s.On("docker volume inspect", engine.Result{})
	s.On("docker network inspect", engine.Result{})
	s.On("docker container inspect", engine.Result{Stderr: "Error response from daemon: container yawble not found", ExitCode: 1})
	for _, args := range [][]string{{"doctor", "--json"}, {"doctor", "--fix", "--json"}} {
		deps := stubbed(s)
		deps.GOOS, deps.LookPath = "darwin", lookPath("podman", "docker")
		deps.ConfigDir = t.TempDir()
		if err := os.WriteFile(filepath.Join(deps.ConfigDir, "config.toml"), []byte("engine = \"docker\"\n"), 0o644); err != nil {
			t.Fatal(err)
		}
		_, out, _ := run(t, deps, args...)
		for _, c := range s.Calls {
			if strings.HasPrefix(c, "podman") && !strings.HasPrefix(c, "podman volume ") {
				t.Errorf("%v asked Podman while the engine is Docker: %q", args, c)
			}
		}
		if strings.Contains(out, "started the podman machine") || strings.Contains(out, "podman-machine-default") {
			t.Errorf("%v reported the Podman machine: %s", args, out)
		}
	}
}

func TestDoctorOnAStoppedWindowsMachineFailsTheMachineAndFixStartsIt(t *testing.T) {
	s := stoppedMachineScript()
	deps := stubbed(s)
	deps.GOOS = "windows"
	code, out, _ := run(t, deps, "doctor", "--json")
	if code != 1 {
		t.Fatalf("exit %d: %s", code, out)
	}
	got := parseDoctor(t, out)
	if v, detail, fix := verdict(t, got, "machine"); v != "FAIL" || !strings.Contains(detail, "stopped") || !strings.Contains(fix, "--fix") {
		t.Errorf("machine %s %q %q", v, detail, fix)
	}
	if v, _, _ := verdict(t, got, "engine"); v != "skip" {
		t.Errorf("engine should be skip while the machine is stopped, got %s", v)
	}
	for _, c := range s.Calls {
		if strings.HasPrefix(c, "podman version") || strings.HasPrefix(c, "podman exec") {
			t.Errorf("must not ask a stopped machine's engine: %q", c)
		}
	}

	s = stoppedMachineScript()
	deps = stubbed(s)
	deps.GOOS = "windows"
	code, out, _ = run(t, deps, "doctor", "--fix", "--json")
	if code != 0 {
		t.Fatalf("--fix exit %d: %s", code, out)
	}
	got = parseDoctor(t, out)
	if len(got.Fixed) != 1 || !strings.Contains(got.Fixed[0], "started the podman machine") {
		t.Errorf("fixed %v", got.Fixed)
	}
	if v, _, _ := verdict(t, got, "machine"); v != "ok" {
		t.Errorf("machine after fix: %s", v)
	}
	if !strings.Contains(strings.Join(s.Calls, "\n"), "podman machine start") {
		t.Errorf("calls:\n%s", strings.Join(s.Calls, "\n"))
	}
}

// A Host that HAS --doctor and fails anyway is a failure of the instance, not a skip.
func TestDoctorWhenTheHostsDoctorCrashesFailsAndExitsOne(t *testing.T) {
	s := runningScript()
	s.On(doctorExec, engine.Result{Stderr: "Unhandled exception. System.IO.IOException: disk full", ExitCode: 134})
	code, out, _ := run(t, stubbed(s), "doctor", "--json")
	if code != 1 {
		t.Fatalf("exit %d: %s", code, out)
	}
	v, detail, fix := verdict(t, parseDoctor(t, out), "instance")
	if v != "FAIL" || !strings.Contains(detail, "disk full") || !strings.Contains(fix, "yawble logs") {
		t.Errorf("instance %s %q %q", v, detail, fix)
	}
}

// Step 10 (D): a plugin member or a preset that runs no model has no sign-in, so it is never the
// reason the agents row warns. Only codex, a model agent, is NOT signed in here.
func TestDoctorDoesNotReportAPluginOrANonModelPresetAsNotSignedIn(t *testing.T) {
	s := runningScript()
	withOthers := strings.Replace(doctorStdout, `"authenticated":true,"detail":"saved login","credentialVariable":"ANTHROPIC_API_KEY"}`,
		`"authenticated":true,"detail":"saved login","credentialVariable":"ANTHROPIC_API_KEY"},`+
			`{"agent":"plugin:sample-echo","kind":"plugin","installed":true,"version":null,"authenticated":false,"detail":"no sign-in"},`+
			`{"agent":"echo","languageModel":false,"installed":true,"version":null,"authenticated":false,"detail":"no model"}`, 1)
	s.On(doctorExec, engine.Result{Stdout: withOthers})
	code, out, errOut := run(t, stubbed(s), "doctor")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if !strings.Contains(out, "claude signed in, launch not known · codex NOT signed in, launch not known") {
		t.Errorf("the agents row is not the two model agents:\n%s", out)
	}
	for _, unwanted := range []string{"sample-echo", "echo NOT", "echo not"} {
		if strings.Contains(out, unwanted) {
			t.Errorf("output reports %q as an agent:\n%s", unwanted, out)
		}
	}

	// With only the plugin and the non-model preset wrong, nothing warns.
	s2 := runningScript()
	s2.On(doctorExec, engine.Result{Stdout: strings.Replace(withOthers, `"authenticated":false,"detail":"exit 1"`, `"authenticated":true,"detail":"ok"`, 1)})
	_, out, _ = run(t, stubbed(s2), "doctor")
	if strings.Contains(out, "warn  agents") || strings.Contains(out, "NOT signed in") {
		t.Errorf("a plugin or a non-model preset made the agents row warn:\n%s", out)
	}
}

// withWip is doctorStdout from a Host that reports its own running limit and per-run memory.
func withWip(runMemory string) string {
	wip := `,"wip":{"limit":{"limit":4,"bound":"memory","cpuBound":7,"cpus":8,"memoryBound":4,"memoryLimitMb":8192,"memoryPerRunMb":1792,` +
		`"reason":"8192 MB less 1024 MB for the Host, at 1792 MB a run, allows 4, below the CPU bound of 7: the memory bound applies"},` +
		`"runMemory":` + runMemory + `}}` + "\n"
	return strings.TrimSuffix(doctorStdout, "}\n") + wip
}

// B002N: doctor's running-limit and run-memory lines are the Host's answers, each mechanism
// said in the Host's words, "not enforced" included.
func TestDoctorShowsTheHostsRunningLimitAndRunMemory(t *testing.T) {
	for _, c := range []struct{ runMemory, want string }{
		{`{"mechanism":"cgroup","perRunMb":null,"detail":"each run in its own cgroup"}`, "info  run memory     cgroup: each run in its own cgroup"},
		{`{"mechanism":"rlimit","perRunMb":1792,"detail":"runs.memoryLimitMb, prlimit --data"}`, "info  run memory     rlimit, 1792 MB per run: runs.memoryLimitMb, prlimit --data"},
		{`{"mechanism":"none","perRunMb":null,"detail":"runs.memoryLimitMb is not set"}`, "info  run memory     not enforced: runs.memoryLimitMb is not set"},
	} {
		s := runningScript()
		s.On(doctorExec, engine.Result{Stdout: withWip(c.runMemory)})
		code, out, errOut := run(t, stubbed(s), "doctor")
		if code != 0 {
			t.Fatalf("exit %d: %s %s", code, out, errOut)
		}
		for _, want := range []string{c.want, "ok    running limit  4, from the memory bound (the Host's answer: 8192 MB less 1024 MB for the Host"} {
			if !strings.Contains(out, want) {
				t.Errorf("output lacks %q:\n%s", want, out)
			}
		}
	}
}

// When the Host cannot be asked - the container stopped, the Host's doctor crashing, or an older
// Host that does not send the figures - both lines say not known. Nothing is estimated instead.
func TestDoctorWhenTheHostCannotBeAskedSaysTheFiguresAreNotKnown(t *testing.T) {
	crashed := runningScript()
	crashed.On(doctorExec, engine.Result{Stderr: "Unhandled exception.", ExitCode: 134})
	older := runningScript()
	older.On(doctorExec, engine.Result{Stdout: doctorStdout})
	for name, script := range map[string]*engine.Scripted{"stopped": stoppedScript(), "crashed": crashed, "older Host": older} {
		_, out, _ := run(t, stubbed(script), "doctor", "--json")
		got := parseDoctor(t, out)
		for _, row := range []string{"running limit", "run memory"} {
			if v, detail, _ := verdict(t, got, row); v != "skip" || !strings.HasPrefix(detail, "not known: ") {
				t.Errorf("%s: %s should be skip, not known; got %s %q", name, row, v, detail)
			}
		}
	}
}

// The CLI's own formula is gone: whatever the container's size, no figure doctor or up prints is
// derived from it. The only figures are the Host's.
func TestDoctorPrintsNoFormulaDerivedFigure(t *testing.T) {
	for _, stdout := range []string{doctorStdout, withWip(`{"mechanism":"none","perRunMb":null,"detail":"not set"}`)} {
		s := runningScript()
		s.On(doctorExec, engine.Result{Stdout: stdout})
		_, out, _ := run(t, stubbed(s), "doctor")
		for _, formula := range []string{"MB per run allows", "MB at 2048", "2048 MB per run", "CPUs allow", "the container allows", "by memory", "by cpu"} {
			if strings.Contains(out, formula) {
				t.Errorf("doctor printed a CLI-computed figure (%q):\n%s", formula, out)
			}
		}
	}
}
