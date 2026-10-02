package cli_test

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/cli"
	"github.com/djlsystems/yawble/cli/internal/config"
	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/instance"
)

const (
	bigDocker   = "68719476736|16|Docker Desktop\n" // 65536 MB, 16 CPUs
	dockerDoc   = "docker exec -e HARNESS_WORKER_KEY= yawble dotnet /app/Harness.Host.dll --doctor"
	workerSized = "memory = \"4g\"\ncpus = 2\ngithubAsked = true\n"
)

// sized is what prepare() derives from workerSized: each worker 4g and 2 CPUs.
func sized(workers int) instance.Settings {
	s, _ := instance.Defaults(config.Config{Image: testImage, Memory: "4g", CPUs: 2, Workers: workers}, instance.Machine{}, "")
	s.KeyHash = testKeyHash()
	return s
}

// reportWith is the Host's doctor report with control's record of its workers.
func reportWith(items string) string {
	return strings.TrimSuffix(doctorStdout, "}\n") + `,"workers":{"recordedAt":"2026-10-05T10:00:00Z","items":[` + items + "]}}\n"
}

func workerItem(id, state string, runs ...string) string {
	var rs []string
	for i, r := range runs {
		team, member, _ := strings.Cut(r, "/")
		rs = append(rs, `{"run":"r`+string(rune('1'+i))+`","team":"`+team+`","member":"`+member+`"}`)
	}
	return `{"id":"` + id + `","version":"2026.10.05.1+abc","state":"` + state + `","draining":false,"connectedSince":"2026-10-05T09:00:00Z","droppedAt":null,"runs":[` + strings.Join(rs, ",") + `],` +
		`"capacity":{"cpus":2,"memoryLimitBytes":4294967296,"memoryInUseBytes":1073741824,"bound":1,"notMeasured":false}}`
}

// dockerInstance is a running instance on Docker, control and workers 1..n made with sized(n).
func dockerInstance(t *testing.T, n int, report string) (*engine.Scripted, cli.Deps) {
	t.Helper()
	return dockerInstanceWith(t, n, report, func(s *engine.Scripted, i int) { scriptWorker(s, "docker", "running", sized(n), i) })
}

// dockerInstanceWith is dockerInstance with each worker scripted by worker.
func dockerInstanceWith(t *testing.T, n int, report string, worker func(*engine.Scripted, int)) (*engine.Scripted, cli.Deps) {
	t.Helper()
	s := engine.NewScripted()
	s.On("docker version", engine.Result{Stdout: "29.8.0\n"})
	s.On("docker info", engine.Result{Stdout: bigDocker})
	s.On("docker container inspect", engine.Result{Stderr: "Error: No such container", ExitCode: 1})
	s.On(inspectLine("docker", "yawble"), engine.Result{Stdout: "running|" + testImage + "|" + instance.SettingsLabel(sized(n)) + "\n"})
	for i := 1; i <= n; i++ {
		worker(s, i)
	}
	scriptWorkers(s, "docker", n)
	s.On(dockerDoc, engine.Result{Stdout: report})
	deps := stubbed(s)
	deps.LookPath = lookPath("docker")
	deps.ConfigDir = t.TempDir()
	seedWorkerKey(t, deps.ConfigDir)
	writeConfig(t, deps.ConfigDir, workerSized)
	return s, deps
}

func TestWorkersRefusesZero(t *testing.T) {
	_, deps := dockerInstance(t, 1, doctorStdout)
	for _, arg := range []string{"0", "-1", "two"} {
		code, _, errOut := run(t, deps, "workers", "--", arg)
		if code != 2 || !strings.Contains(errOut, "1 or more") {
			t.Errorf("%s: exit %d %s", arg, code, errOut)
		}
	}
	if c := savedConfig(t, deps.ConfigDir); c.Workers != 0 {
		t.Errorf("saved %d", c.Workers)
	}
}

func TestWorkersRefusesACountOverTheEngineNamingTheBound(t *testing.T) {
	s, deps := dockerInstance(t, 1, doctorStdout)
	writeConfig(t, deps.ConfigDir, "memory = \"20g\"\ncpus = 2\n")
	// 4 × 20480 + 1536 = 83456, over the engine's 65536.
	code, _, errOut := run(t, deps, "workers", "4")
	if code != 1 || !strings.Contains(errOut, "4 worker(s) × 20480 MB + control's 1536 MB = 83456 MB, more than the 65536 MB the engine has") || !strings.Contains(errOut, "the count was not changed") {
		t.Errorf("exit %d: %s", code, errOut)
	}
	if c := savedConfig(t, deps.ConfigDir); c.Workers == 4 {
		t.Errorf("a refused count was saved")
	}
	if len(callsContaining(s, "run -d --name yawble-worker-4")) != 0 {
		t.Error("a refused count started a worker")
	}
}

// workers 3 through the command: two workers added, the count saved, nothing else touched.
func TestWorkersTouchesNoVolume(t *testing.T) {
	s, deps := dockerInstance(t, 1, doctorStdout)
	for i := 2; i <= 3; i++ {
		s.On(healthLine("docker", instance.WorkerName(i)), engine.Result{Stdout: "healthy\n"})
	}
	code, out, errOut := run(t, deps, "workers", "3")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if c := savedConfig(t, deps.ConfigDir); c.Workers != 3 || c.Memory != "4g" {
		t.Errorf("saved %+v", c)
	}
	if len(callsContaining(s, "docker run -d --name yawble-worker-2 --network container:yawble")) != 1 || len(callsContaining(s, "docker run -d --name yawble-worker-3 --network container:yawble")) != 1 {
		t.Errorf("calls:\n%s", calls(s))
	}
	if !strings.Contains(out, "yawble-worker-3 is connected to control") {
		t.Errorf("out %q", out)
	}
	for _, c := range s.Calls {
		for _, never := range []string{"volume ", "network create", "network rm", "docker rm -f yawble\n", "docker stop yawble", "--name yawble --network"} {
			if strings.Contains(c+"\n", never) {
				t.Errorf("workers touched %q: %s", never, c)
			}
		}
	}
}

// Through the command: --now names the runs on each worker it stops, and --yes answers.
func TestWorkersOneNowWarnsNamingTheRunsThatFailWorkerLost(t *testing.T) {
	draining := func(item string) string { return strings.Replace(item, `"draining":false`, `"draining":true`, 1) }
	report := reportWith(workerItem("worker-1", "connected") + "," + draining(workerItem("worker-2", "connected")) + "," + draining(workerItem("worker-3", "connected", "acme/Ada", "beta/Cy")))
	s, deps := dockerInstance(t, 3, report)
	code, out, errOut := run(t, deps, "workers", "1", "--now", "--yes")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if !strings.Contains(out, "Stopping worker-3 now: this fails its 2 runs as worker-lost (acme/Ada, beta/Cy); the Manager re-sends each once.") {
		t.Errorf("out %q", out)
	}
	if strings.Contains(out, "Stopping worker-2") {
		t.Errorf("worker-2 has no runs; nothing to warn about: %q", out)
	}
	rm3, rm2 := indexOf(s.Calls, "docker rm -f yawble-worker-3"), indexOf(s.Calls, "docker rm -f yawble-worker-2")
	if rm3 < 0 || rm2 < rm3 || indexOf(s.Calls, "docker rm -f yawble-worker-1") >= 0 {
		t.Errorf("calls:\n%s", calls(s))
	}
	// Each is drained, and control's record read, before the warning and the removal.
	for _, w := range []string{"yawble-worker-3", "yawble-worker-2"} {
		touch, rm := indexOf(s.Calls, "docker exec -e HARNESS_WORKER_KEY= "+w+" touch /var/lib/harness-worker/drain"), indexOf(s.Calls, "docker rm -f "+w)
		read := -1
		for i := touch + 1; touch >= 0 && i < rm; i++ {
			if strings.HasPrefix(s.Calls[i], dockerDoc) {
				read = i
			}
		}
		if touch < 0 || read < 0 {
			t.Errorf("%s not drained and read before it was removed:\n%s", w, calls(s))
		}
	}
	// Without --yes and no terminal, the question is a refusal and nothing is stopped.
	s2, deps2 := dockerInstance(t, 3, report)
	if code, _, errOut := run(t, deps2, "workers", "1", "--now"); code == 0 || !strings.Contains(errOut, "--yes") {
		t.Errorf("exit %d: %s", code, errOut)
	}
	if len(callsContaining(s2, "docker rm -f yawble-worker-3")) != 0 || len(callsContaining(s2, "yawble-worker-3 rm -f /var/lib/harness-worker/drain")) != 1 {
		t.Errorf("stopped without a yes, or left it draining:\n%s", calls(s2))
	}
}

func TestWorkersWithNoNumberPrintsTheCountAndEachWorker(t *testing.T) {
	_, deps := dockerInstance(t, 2, reportWith(workerItem("worker-1", "connected", "acme/Ada")+","+workerItem("worker-2", "dropped")))
	writeConfig(t, deps.ConfigDir, workerSized+"workers = 2\n")
	code, out, errOut := run(t, deps, "workers")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	for _, want := range []string{
		"workers    2 (yawble's config)",
		"worker-1   running, healthy; control: connected, version 2026.10.05.1+abc, 1 run (acme/Ada)",
		"worker-2   running, healthy; control: dropped, version 2026.10.05.1+abc, no runs",
	} {
		if !strings.Contains(out, want) {
			t.Errorf("out lacks %q:\n%s", want, out)
		}
	}
}

func TestLogsTakesAWorker(t *testing.T) {
	for arg, want := range map[string]string{"": "podman logs --tail 200 yawble", "control": "podman logs --tail 200 yawble", "worker-2": "podman logs --tail 200 yawble-worker-2", "3": "podman logs --tail 200 yawble-worker-3"} {
		s := engine.NewScripted()
		args := []string{"logs"}
		if arg != "" {
			args = append(args, arg)
		}
		if code, out, errOut := run(t, stubbed(s), args...); code != 0 || calls(s) != want {
			t.Errorf("%q: exit %d %s %s calls %q", arg, code, out, errOut, calls(s))
		}
	}
	if code, _, errOut := run(t, stubbed(engine.NewScripted()), "logs", "tunnel"); code != 2 || !strings.Contains(errOut, "worker-<n>") {
		t.Errorf("exit %d %s", code, errOut)
	}
}

func TestStatusAndDoctorShowEachWorker(t *testing.T) {
	report := reportWith(workerItem("worker-1", "connected", "acme/Ada") + "," + workerItem("worker-2", "connected"))
	s, deps := dockerInstance(t, 2, report)
	writeConfig(t, deps.ConfigDir, workerSized+"workers = 2\n")
	s.On("docker stats", engine.Result{Stdout: `{"MemUsage":"1GiB / 4GiB","MemPerc":"25.00%","CPUPerc":"3.00%","PIDs":"12"}` + "\n"})
	code, out, errOut := run(t, deps, "status")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	for _, want := range []string{"worker-1   running, healthy; control: connected, version 2026.10.05.1+abc, 1 run (acme/Ada)", "worker-2   running, healthy; control: connected"} {
		if !strings.Contains(out, want) {
			t.Errorf("status lacks %q:\n%s", want, out)
		}
	}
	if len(callsContaining(s, "docker stats --no-stream --format {{json .}} yawble-worker-2")) != 1 {
		t.Errorf("no engine stats of worker-2:\n%s", calls(s))
	}
	_, out, _ = run(t, deps, "status", "--json")
	var st struct {
		Workers []struct {
			ID        string `json:"id"`
			Container string `json:"container"`
			Health    string `json:"health"`
			Control   *struct {
				State string `json:"state"`
				Runs  []any  `json:"runs"`
			} `json:"control"`
		} `json:"workers"`
	}
	if err := json.Unmarshal([]byte(out), &st); err != nil || len(st.Workers) != 2 || st.Workers[0].Control == nil || len(st.Workers[0].Control.Runs) != 1 || st.Workers[1].Health != "healthy" {
		t.Errorf("status --json workers: %+v %v\n%s", st.Workers, err, out)
	}

	_, out, _ = run(t, deps, "doctor", "--json")
	got := parseDoctor(t, out)
	rows := map[string]string{}
	for _, c := range got.Checks {
		rows[c.Name] = c.Verdict + " " + c.Detail
	}
	if !strings.HasPrefix(rows["worker-1"], "ok connected, version 2026.10.05.1+abc, 1 run (acme/Ada); Host: memory 1024 of 4096 MB, 2 CPUs, holds 1 runs at once; engine: ") || !strings.HasPrefix(rows["worker-2"], "ok ") {
		t.Errorf("doctor rows %q", rows)
	}
	if _, warned := rows["workers"]; warned {
		t.Errorf("the count matches; no count row: %q", rows["workers"])
	}
}

func doctorRows(t *testing.T, deps cli.Deps, args ...string) map[string]string {
	t.Helper()
	_, out, _ := run(t, deps, append([]string{"doctor", "--json"}, args...)...)
	rows := map[string]string{}
	for _, c := range parseDoctor(t, out).Checks {
		rows[c.Name] = c.Verdict + " " + c.Detail + " | " + c.Fix
	}
	return rows
}

func TestDoctorWarnsAWorkerThatRunsButIsNotConnected(t *testing.T) {
	_, deps := dockerInstance(t, 1, reportWith(workerItem("worker-1", "dropped")))
	if row := doctorRows(t, deps)["worker-1"]; !strings.HasPrefix(row, "warn yawble-worker-1 runs but is not connected to control") || !strings.Contains(row, "yawble logs worker-1") {
		t.Errorf("row %q", row)
	}
	_, deps = dockerInstance(t, 1, doctorStdout)
	if row := doctorRows(t, deps)["worker-1"]; !strings.Contains(row, "control has no record of it") || !strings.HasPrefix(row, "warn") {
		t.Errorf("no record: %q", row)
	}
}

func TestDoctorWarnsAWorkerOnAnotherImageThanControls(t *testing.T) {
	_, deps := dockerInstanceWith(t, 1, reportWith(workerItem("worker-1", "connected")), func(s *engine.Scripted, i int) {
		s.On(inspectLine("docker", "yawble-worker-1"), engine.Result{Stdout: "running|ghcr.io/djlsystems/yawble:2026.01.01.1-worker|" + instance.WorkerSettingsLabel(sized(1), 1, testImage+"-worker") + "\n"})
	})
	if row := doctorRows(t, deps)["worker-1"]; !strings.Contains(row, "runs ghcr.io/djlsystems/yawble:2026.01.01.1-worker, not "+testImage+"-worker") || !strings.Contains(row, "yawble up replaces it") {
		t.Errorf("row %q", row)
	}
}

func TestDoctorWarnsWhenTheWorkerCountDiffersFromTheConfig(t *testing.T) {
	_, deps := dockerInstance(t, 2, reportWith(workerItem("worker-1", "connected")+","+workerItem("worker-2", "connected")))
	// The config asks for 1; two worker containers exist.
	if row := doctorRows(t, deps)["workers"]; !strings.HasPrefix(row, "warn 2 worker container(s), yawble's config asks for 1") || !strings.Contains(row, "yawble workers 1") {
		t.Errorf("row %q", row)
	}
}

func TestDoctorFixStartsAStoppedWorker(t *testing.T) {
	label := instance.WorkerSettingsLabel(sized(1), 1, testImage+"-worker")
	s, deps := dockerInstanceWith(t, 1, reportWith(workerItem("worker-1", "dropped")), func(s *engine.Scripted, i int) {
		s.On(inspectLine("docker", "yawble-worker-1"), engine.Result{Stdout: "exited|" + testImage + "-worker|" + label + "\n"})
		s.On(healthLine("docker", "yawble-worker-1"), engine.Result{Stdout: "healthy\n"})
	})
	if row := doctorRows(t, deps)["worker-1"]; !strings.HasPrefix(row, "warn yawble-worker-1 is stopped") || !strings.Contains(row, "--fix starts it") {
		t.Errorf("row %q", row)
	}
	_, out, _ := run(t, deps, "doctor", "--fix", "--json")
	if indexOf(s.Calls, "docker start yawble-worker-1") < 0 || !strings.Contains(out, "started yawble-worker-1") {
		t.Errorf("--fix: %s\n%s", out, calls(s))
	}
	if indexOf(s.Calls, "docker start yawble\n") >= 0 || len(callsContaining(s, "rm -f")) != 0 {
		t.Errorf("--fix touched more than the stopped worker:\n%s", calls(s))
	}
}

// One worker sized to the whole engine: control's allowance on top is warned about, not refused,
// on a later `up` and on an upgrade alike. Two such workers are refused.
func TestUpAtACountOfOneWarnsAndDoesNotRefuse(t *testing.T) {
	s, deps := dockerUp("darwin")
	_ = s
	deps.ConfigDir = t.TempDir()
	writeConfig(t, deps.ConfigDir, "memory = \"12g\"\ncpus = 10\ngithubAsked = true\n")
	code, out, errOut := run(t, deps, "up", "--no-browser")
	if code != 0 || !strings.Contains(errOut, "warning: 1 worker(s) × 12288 MB + control's 1536 MB") {
		t.Errorf("exit %d: %s %s", code, out, errOut)
	}
	writeConfig(t, deps.ConfigDir, "memory = \"6g\"\ncpus = 4\nworkers = 2\ngithubAsked = true\n")
	s2, deps2 := dockerUp("darwin")
	deps2.ConfigDir = deps.ConfigDir
	code, _, errOut = run(t, deps2, "up", "--no-browser")
	if code != 1 || !strings.Contains(errOut, "2 worker(s) × 6144 MB + control's 1536 MB = 13824 MB, more than the 12288 MB the engine has") || !strings.Contains(errOut, "nothing was started") {
		t.Errorf("two workers over the engine: exit %d %s", code, errOut)
	}
	if len(callsContaining(s2, " run -d ")) != 0 {
		t.Errorf("a refused up ran something:\n%s", calls(s2))
	}
}

func TestUpgradeAtTheEnginesFullMemoryWarnsAndDoesNotRefuse(t *testing.T) {
	pinBuild(t)
	s, deps := dockerUp("darwin")
	pre := `{"port":8080,"memory":"12g","cpus":10,"maxRunning":0,"image":"ghcr.io/djlsystems/yawble:2026.09.24.1","envHash":""}`
	s.On(inspectLine("docker", "yawble"), engine.Result{Stdout: "running|ghcr.io/djlsystems/yawble:2026.09.24.1|" + pre + "\n"})
	s.On(healthLine("docker", "yawble-worker-1"), engine.Result{Stdout: "healthy\n"})
	deps.Env = func(string) string { return "" }
	deps.ConfigDir = t.TempDir()
	writeConfig(t, deps.ConfigDir, "memory = \"12g\"\ncpus = 10\n")
	code, out, errOut := run(t, deps, "update", "--instance")
	if code != 0 || !strings.Contains(out, instance.PreSplitLine) {
		t.Errorf("exit %d: %s %s", code, out, errOut)
	}
	if indexOf(s.Calls, "docker run -d --name yawble-worker-1 --network container:yawble --restart unless-stopped --memory 12g --cpus 10") < 0 {
		t.Errorf("the worker gets what the single container had:\n%s", calls(s))
	}
}

func TestTheFirstUpSizeScreenOffersEngineMemoryLessControl(t *testing.T) {
	out := refusalsOf(t, dockerEngine("12884901888|10|Docker Desktop\n"), "darwin", "docker", "10753\n\n\n")
	for _, want := range []string{
		"control takes    1536 MB memory, 2 CPUs (fixed), so a worker may have up to 10752 MB",
		"Memory in MB (4096 to 10752) [6144]: ",
		"refused: 10753 MB is more than the engine has beside control's 1536 MB; the most is 10752 MB",
	} {
		if !strings.Contains(out, want) {
			t.Errorf("out lacks %q:\n%s", want, out)
		}
	}
}

// A container from before control and workers is replaced by control and worker 1 on the same
// volume, the key made once, and nothing on the volume or the network touched.
func TestUpdateFromASingleContainerReplacesItWithControlAndOneWorkerOnTheSameVolume(t *testing.T) {
	pinBuild(t)
	old := "ghcr.io/djlsystems/yawble:2026.09.24.1"
	pre := `{"port":8080,"memory":"8192m","cpus":4,"maxRunning":0,"image":"` + old + `","envHash":""}`
	s := engine.NewScripted()
	s.On("podman container inspect", engine.Result{Stderr: "no such container", ExitCode: 125})
	s.On(inspectLine("podman", "yawble"), engine.Result{Stdout: "running|" + old + "|" + pre + "\n"})
	s.On("podman exec -e HARNESS_WORKER_KEY= yawble runuser -u agent", engine.Result{Stdout: agentListing})
	s.On(healthLine("podman", "yawble-worker-1"), engine.Result{Stdout: "healthy\n"})
	deps := stubbed(s)
	deps.Env = func(string) string { return "" }
	deps.ConfigDir = t.TempDir()
	code, out, errOut := run(t, deps, "update", "--instance")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if strings.Count(out, instance.PreSplitLine) != 1 || !strings.Contains(out, "this stops Alpha/Ann (claude)") {
		t.Errorf("out %q", out)
	}
	rm, control, worker := indexOf(s.Calls, "podman rm -f yawble"), indexOf(s.Calls, "podman run -d --name yawble --pod yawble"), indexOf(s.Calls, "podman run -d --name yawble-worker-1 --pod yawble")
	if rm < 0 || control < rm || worker < control {
		t.Errorf("remove, control, worker 1:\n%s", calls(s))
	}
	for _, c := range s.Calls {
		for _, never := range []string{"volume rm", "volume create", "pod rm", "pod create"} {
			if strings.Contains(c, never) {
				t.Errorf("the upgrade ran %q", c)
			}
		}
	}
	if _, err := os.Stat(filepath.Join(deps.ConfigDir, config.WorkerKeyFileName)); err != nil {
		t.Errorf("no key: %v", err)
	}
}

func TestTheKeyIsMadeOnceAndKeptAcrossUpdates(t *testing.T) {
	pinBuild(t)
	dir := t.TempDir()
	var keys []string
	for i := 0; i < 3; i++ {
		s := engine.NewScripted()
		s.On("podman container inspect", engine.Result{Stderr: "no such container", ExitCode: 125})
		s.On(healthLine("podman", "yawble-worker-1"), engine.Result{Stdout: "healthy\n"})
		deps := stubbed(s)
		deps.Env = func(string) string { return "" }
		deps.ConfigDir = dir
		verb := []string{"update", "--instance"}
		if i == 0 {
			verb = []string{"up", "--yes", "--no-browser"}
		}
		code, out, errOut := run(t, deps, verb...)
		if code != 0 {
			t.Fatalf("%v: exit %d %s %s", verb, code, out, errOut)
		}
		if made := strings.Contains(out, "made the key"); made != (i == 0) {
			t.Errorf("run %d: made the key %v:\n%s", i, made, out)
		}
		data, err := os.ReadFile(filepath.Join(dir, config.WorkerKeyFileName))
		if err != nil {
			t.Fatal(err)
		}
		keys = append(keys, string(data))
	}
	if keys[0] != keys[1] || keys[1] != keys[2] || !regexp.MustCompile(`^HARNESS_WORKER_KEY=[0-9a-f]{64}\n$`).MatchString(keys[0]) {
		t.Errorf("keys %q", keys)
	}
}

// The strong form: across every verb that touches the instance, the key is on no command line
// and in no input; it reaches the containers only as the owner-only env file, written whole, and
// a label carries only the first 12 hex characters of its SHA-256.
func TestWorkerKeyReachesContainersOnlyAsAnEnvFile(t *testing.T) {
	pinBuild(t)
	dir := t.TempDir()
	writeConfig(t, dir, workerSized)
	s := engine.NewScripted()
	s.On("docker version", engine.Result{Stdout: "29.8.0\n"})
	s.On("docker info", engine.Result{Stdout: bigDocker})
	s.On("docker container inspect", engine.Result{Stderr: "Error: No such container", ExitCode: 1})
	for i := 1; i <= 3; i++ {
		s.On(healthLine("docker", instance.WorkerName(i)), engine.Result{Stdout: "healthy\n"})
	}
	s.On(dockerDoc, engine.Result{Stdout: reportWith(workerItem("worker-1", "connected"))})
	deps := stubbed(s)
	deps.LookPath = lookPath("docker")
	deps.Env = func(string) string { return "" }
	deps.ConfigDir = dir
	verbs := [][]string{{"up", "--yes", "--no-browser"}}
	var key string
	runVerbs := func(vs [][]string) {
		for _, v := range vs {
			if code, out, errOut := run(t, deps, v...); code != 0 && v[0] != "doctor" {
				t.Fatalf("%v: exit %d %s %s", v, code, out, errOut)
			}
		}
	}
	runVerbs(verbs)
	data, err := os.ReadFile(filepath.Join(dir, config.WorkerKeyFileName))
	if err != nil {
		t.Fatal(err)
	}
	key = strings.TrimSpace(strings.TrimPrefix(string(data), "HARNESS_WORKER_KEY="))
	sum := sha256.Sum256([]byte(key))
	hash := hex.EncodeToString(sum[:])[:12]

	// Now an instance from before the split, then every other verb.
	pre := `{"port":8080,"memory":"4g","cpus":2,"maxRunning":0,"image":"` + testImage + `","envHash":""}`
	s.On(inspectLine("docker", "yawble"), engine.Result{Stdout: "running|" + testImage + "|" + pre + "\n"})
	runVerbs([][]string{{"update", "--instance"}})
	st := sized(3)
	st.KeyHash = hash
	s.On(inspectLine("docker", "yawble"), engine.Result{Stdout: "running|" + testImage + "|" + instance.SettingsLabel(st) + "\n"})
	scriptWorker(s, "docker", "running", st, 1)
	runVerbs([][]string{{"workers", "3"}, {"status"}, {"status", "--json"}, {"doctor"}, {"logs", "2"}, {"uninstall", "--yes", "--keep-settings"}})

	if len(key) != 64 {
		t.Fatalf("key %q", key)
	}
	for _, c := range s.Calls {
		if strings.Contains(c, key) {
			t.Errorf("the key is on a command line: %s", c)
		}
	}
	for _, in := range s.Inputs {
		if strings.Contains(in, key) {
			t.Error("the key was handed to a program on stdin")
		}
	}
	envFile := "--env-file " + filepath.Join(dir, config.WorkerKeyFileName)
	runs := callsContaining(s, " run -d --name yawble")
	if len(runs) < 3 {
		t.Fatalf("runs %q", runs)
	}
	for _, r := range runs {
		if !strings.Contains(r, envFile) || !strings.Contains(r, `"keyHash":"`+hash+`"`) {
			t.Errorf("run line without the key file or its hash: %s", r)
		}
	}
	info, err := os.Stat(filepath.Join(dir, config.WorkerKeyFileName))
	if err != nil || info.Mode().Perm() != 0o600 {
		t.Errorf("worker.env mode %v %v", info.Mode(), err)
	}
	left, _ := os.ReadDir(dir)
	for _, e := range left {
		if strings.HasPrefix(e.Name(), ".worker.env") {
			t.Errorf("a partial key file was left: %s", e.Name())
		}
	}
	// No instance container, tunnel or helper mounts a temporary folder: each has its own /tmp.
	for _, c := range s.Calls {
		if regexp.MustCompile(`-v \S*:/tmp(\s|:|$)|--tmpfs`).MatchString(c) {
			t.Errorf("a shared /tmp: %s", c)
		}
	}
}

func TestNoInstanceContainerMountsTmp(t *testing.T) {
	st := sized(2)
	st.KeyFile = "/c/worker.env"
	for _, e := range []struct {
		name string
		make func(engine.Runner) engine.Engine
	}{{"podman", engine.NewPodman}, {"docker", engine.NewDocker}} {
		s := engine.NewScripted()
		s.On(e.name+" container inspect", engine.Result{Stderr: "no such container", ExitCode: 1})
		s.On(e.name+" volume exists", engine.Result{ExitCode: 1})
		s.On(e.name+" pod exists", engine.Result{ExitCode: 1})
		for i := 1; i <= 2; i++ {
			s.On(healthLine(e.name, instance.WorkerName(i)), engine.Result{Stdout: "healthy\n"})
		}
		en := e.make(s)
		ctx := t.Context()
		if err := instance.Up(ctx, en, st, func(string) bool { return true }, &strings.Builder{}); err != nil {
			t.Fatal(err)
		}
		_, _ = en.RunHelper(ctx, engine.HelperSpec{Image: testImage, Volume: "yawble-data", Target: "/data", Entrypoint: "tar"})
		lines := callsContaining(s, " run ")
		if len(lines) != 4 {
			t.Fatalf("%s run lines %q", e.name, lines)
		}
		for _, c := range lines {
			if regexp.MustCompile(`-v \S*:/tmp(\s|:|$)|--tmpfs`).MatchString(c) || strings.Count(c, " -v ") != 1 {
				t.Errorf("%s: %s", e.name, c)
			}
		}
	}
}

// Agents run on workers: backup names them from every worker, stops the workers before control
// (on Docker they live in its namespace), and starts them again after control answers.
func TestBackupNamesAgentsOnEveryWorkerStopsWorkersFirstAndStartsThemAfterControl(t *testing.T) {
	for _, program := range []string{"podman", "docker"} {
		t.Run(program, func(t *testing.T) {
			s := backupScript(t, program, "running", "")
			scriptWorkers(s, program, 2)
			for i := 1; i <= 2; i++ {
				s.On(healthLine(program, instance.WorkerName(i)), engine.Result{Stdout: "healthy\n"})
			}
			s.On(program+" exec -e HARNESS_WORKER_KEY= yawble-worker-2 runuser -u agent", engine.Result{Stdout: agentListing})
			deps := backupDeps(t, s, program)
			path := filepath.Join(t.TempDir(), "b.tar.gz")
			code, out, errOut := run(t, deps, "backup", "--output", path, "--yes")
			if code != 0 {
				t.Fatalf("exit %d: %s %s", code, out, errOut)
			}
			if !strings.Contains(out, "this stops Alpha/Ann (claude)") {
				t.Errorf("the agent on worker-2 was not named: %s", out)
			}
			w2, w1, control := indexOf(s.Calls, program+" stop yawble-worker-2"), indexOf(s.Calls, program+" stop yawble-worker-1"), indexOf(s.Calls, program+" stop yawble\n")
			control = -1
			for i, c := range s.Calls {
				if c == program+" stop yawble" {
					control = i
				}
			}
			tar := indexOf(s.Calls, helper(program, "tar", true))
			start := -1
			for i, c := range s.Calls {
				if c == program+" start yawble" {
					start = i
				}
			}
			s1, s2 := indexOf(s.Calls, program+" start yawble-worker-1"), indexOf(s.Calls, program+" start yawble-worker-2")
			if w2 < 0 || w1 < w2 || control < w1 || tar < control || start < tar || s1 < start || s2 < s1 {
				t.Errorf("order: stop w2 %d, w1 %d, control %d; tar %d; start control %d, w1 %d, w2 %d\n%s", w2, w1, control, tar, start, s1, s2, calls(s))
			}
		})
	}
}

func TestRestoreReplaceStopsEveryWorkerBeforeTouchingTheVolume(t *testing.T) {
	for _, program := range []string{"podman", "docker"} {
		t.Run(program, func(t *testing.T) {
			s := restoreScript(t, program, "/data/messages.db\n", "running")
			scriptWorkers(s, program, 2)
			s.On(healthLine(program, "yawble-worker-1"), engine.Result{Stdout: "healthy\n"})
			deps := restoreDeps(t, s, program)
			file := backupFile(t, t.TempDir(), "2026.09.24.1")
			code, out, errOut := run(t, deps, "restore", file, "--replace", "--yes")
			if code != 0 {
				t.Fatalf("exit %d: %s %s", code, out, errOut)
			}
			clear := indexOf(s.Calls, helper(program, "find", false))
			for _, w := range []string{"yawble-worker-2", "yawble-worker-1"} {
				if i := indexOf(s.Calls, program+" stop "+w); i < 0 || i > clear {
					t.Errorf("%s stopped at %d, the volume cleared at %d:\n%s", w, i, clear, calls(s))
				}
			}
		})
	}
}

// Workers first (on Docker each lives in control's namespace), then control, the pod or network,
// and both images, on every engine installed.
func TestUninstallRemovesEveryInstanceContainerWorkersFirstOnEveryEngine(t *testing.T) {
	s := engine.NewScripted()
	for _, program := range []string{"podman", "docker"} {
		s.On(program+" version", engine.Result{Stdout: "1\n"})
		s.On(program+" container inspect", engine.Result{Stdout: "running|x|{}\n"})
		s.On(inspectLine(program, "yawble-tunnel"), engine.Result{Stderr: "no such container", ExitCode: 1})
		s.On(program+" ps -a --filter label=yawble.role=worker", engine.Result{Stdout: "yawble-worker-1\nyawble-worker-2\n"})
		// A worker whose labels were lost is still found by its name.
		s.On(program+" ps -a --filter name=^yawble-worker-", engine.Result{Stdout: "yawble-worker-3\n"})
	}
	deps := stubbed(s)
	deps.LookPath = lookPath("podman", "docker")
	code, out, errOut := run(t, deps, "uninstall", "--yes")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	for _, program := range []string{"podman", "docker"} {
		var removed []string
		for _, c := range s.Calls {
			if strings.HasPrefix(c, program+" rm -f ") {
				removed = append(removed, strings.TrimPrefix(c, program+" rm -f "))
			}
		}
		if strings.Join(removed, " ") != "yawble-worker-3 yawble-worker-2 yawble-worker-1 yawble" {
			t.Errorf("%s removed %q", program, removed)
		}
		pod := map[string]string{"podman": "podman pod rm -f yawble", "docker": "docker network rm yawble"}[program]
		if i := indexOf(s.Calls, pod); i < indexOf(s.Calls, program+" rm -f yawble-worker-1") {
			t.Errorf("%s: the pod went before the workers:\n%s", program, calls(s))
		}
		if !strings.Contains(out, "removed container yawble-worker-2 ("+program+")") {
			t.Errorf("out lacks %s's worker:\n%s", program, out)
		}
	}
}

func TestUninstallRemovesBothImages(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman container inspect", engine.Result{ExitCode: 125, Stderr: "Error: no such container yawble"})
	s.On("podman pod exists", engine.Result{ExitCode: 1})
	s.On("podman volume exists", engine.Result{ExitCode: 1})
	deps := stubbed(s)
	deps.LookPath = lookPath("podman")
	deps.ConfigDir = t.TempDir()
	seedWorkerKey(t, deps.ConfigDir)
	code, out, errOut := run(t, deps, "uninstall", "--yes")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	for _, want := range []string{"podman rmi " + testImage, "podman rmi " + testImage + "-worker"} {
		if indexOf(s.Calls, want) < 0 {
			t.Errorf("missing %q:\n%s", want, calls(s))
		}
	}
	if _, err := os.Stat(filepath.Join(deps.ConfigDir, config.WorkerKeyFileName)); !os.IsNotExist(err) {
		t.Errorf("worker.env was left: %v", err)
	}
}
