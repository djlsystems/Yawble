package instance_test

import (
	"bytes"
	"context"
	"errors"
	"regexp"
	"strings"
	"testing"
	"time"

	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/instance"
)

// engines are both implementations with what differs on their lines: the inspect format, the
// health line, and how a container joins the instance.
var engines = []struct {
	name, inspect, health string
	make                  func(engine.Runner) engine.Engine
}{
	{"podman", "podman container inspect --format {{.State.Status}}|{{.ImageName}}|{{index .Config.Labels \"yawble.settings\"}} ", "podman container inspect --format {{.State.Health.Status}} ", engine.NewPodman},
	{"docker", "docker container inspect --format {{.State.Status}}|{{.Config.Image}}|{{index .Config.Labels \"yawble.settings\"}} ", "docker container inspect --format {{if .State.Health}}{{.State.Health.Status}}{{end}} ", engine.NewDocker},
}

func keyed() instance.Settings {
	st := settings()
	st.EnvFile, st.EnvFileHash = "/c/yawble/env", "e1"
	st.KeyFile, st.KeyHash = "/c/yawble/worker.env", "0123456789ab"
	return st
}

// fresh scripts an engine with nothing of the instance on it, every worker healthy once run.
func fresh(program, health string, workers int) *engine.Scripted {
	s := engine.NewScripted()
	s.On(program+" volume exists", engine.Result{ExitCode: 1})
	s.On(program+" pod exists", engine.Result{ExitCode: 1})
	s.On(program+" image exists", engine.Result{ExitCode: 1})
	s.On(program+" volume inspect", engine.Result{Stderr: "no such volume", ExitCode: 1})
	s.On(program+" network inspect", engine.Result{Stderr: "network yawble not found", ExitCode: 1})
	s.On(program+" image inspect", engine.Result{Stderr: "No such image", ExitCode: 1})
	s.On(program+" container inspect", engine.Result{Stderr: "no such container", ExitCode: 125})
	for i := 1; i <= workers; i++ {
		s.On(health+instance.WorkerName(i), engine.Result{Stdout: "healthy\n"})
	}
	return s
}

func runLines(s *engine.Scripted) []string {
	var lines []string
	for _, c := range s.Calls {
		if strings.Contains(c, " run -d ") {
			lines = append(lines, c)
		}
	}
	return lines
}

func TestControlRunLineOnBothEngines(t *testing.T) {
	st := keyed()
	caps := "--cap-drop ALL --cap-add CHOWN --cap-add DAC_OVERRIDE --cap-add FOWNER --cap-add FSETID --cap-add SETUID --cap-add SETGID --cap-add SETPCAP --cap-add KILL"
	tail := " --restart unless-stopped --memory 1536m --cpus 2 " + caps + " -e HARNESS_ROLE=control -e Wip__MaxRunning=8 --label yawble.instance=yawble --label yawble.role=control --label yawble.settings=" + label(st) + " --env-file /c/yawble/env --env-file /c/yawble/worker.env -v yawble-data:/data " + img
	want := map[string]string{
		"podman": "podman run -d --name yawble --pod yawble" + tail,
		"docker": "docker run -d --name yawble --network yawble -p 0.0.0.0:8080:8080" + tail,
	}
	for _, e := range engines {
		s := fresh(e.name, e.health, 1)
		if err := instance.Up(context.Background(), e.make(s), st, healthy, &bytes.Buffer{}); err != nil {
			t.Fatalf("%s: %v", e.name, err)
		}
		if got := runLines(s)[0]; got != want[e.name] {
			t.Errorf("%s control:\n got %s\nwant %s", e.name, got, want[e.name])
		}
	}
}

func TestWorkerRunLineOnBothEngines(t *testing.T) {
	st := keyed()
	st.Workers = 2
	for _, e := range engines {
		s := fresh(e.name, e.health, 2)
		if err := instance.Up(context.Background(), e.make(s), st, healthy, &bytes.Buffer{}); err != nil {
			t.Fatalf("%s: %v", e.name, err)
		}
		join := "--pod yawble"
		if e.name == "docker" {
			join = "--network container:yawble"
		}
		lines := runLines(s)
		if len(lines) != 3 {
			t.Fatalf("%s: run lines %q", e.name, lines)
		}
		for i, line := range lines[1:] {
			n := i + 1
			want := e.name + " run -d --name yawble-worker-" + itoa(n) + " " + join + " --restart unless-stopped --memory 12288m --cpus 8 -e HARNESS_CONTROL_URL=ws://127.0.0.1:8080 -e HARNESS_ROLE=worker -e HARNESS_WORKER_ID=worker-" + itoa(n) + " --label yawble.instance=yawble --label yawble.role=worker --label yawble.settings=" + workerLabel(st, n) + " --label yawble.worker=" + itoa(n) + " --env-file /c/yawble/env --env-file /c/yawble/worker.env -v yawble-data:/data " + img + "-worker"
			if line != want {
				t.Errorf("%s worker %d:\n got %s\nwant %s", e.name, n, line, want)
			}
			for _, refused := range []string{"--cap-drop", "--cap-add", " -p ", "--hostname"} {
				if strings.Contains(line, refused) {
					t.Errorf("%s worker %d carries %q", e.name, n, refused)
				}
			}
		}
	}
}

func itoa(n int) string { return string(rune('0' + n)) }

func TestUpOnAFreshDockerMachineMakesTheNetworkControlAndOneWorkerInControlsNamespace(t *testing.T) {
	st := keyed()
	s := fresh("docker", engines[1].health, 1)
	var out bytes.Buffer
	if err := instance.Up(context.Background(), engine.NewDocker(s), st, healthy, &out); err != nil {
		t.Fatal(err)
	}
	want := []string{
		"docker volume inspect yawble-data",
		"docker volume create yawble-data",
		"docker network inspect yawble",
		"docker network create yawble",
		engines[1].inspect + "yawble",
		"docker image inspect " + img,
		"docker pull " + img,
		"docker run -d --name yawble --network yawble -p 0.0.0.0:8080:8080 <control>",
		"docker logs --follow --since <start> yawble",
		engines[1].inspect + "yawble-worker-1",
		"docker image inspect " + img + "-worker",
		"docker pull " + img + "-worker",
		"docker run -d --name yawble-worker-1 --network container:yawble <worker>",
		engines[1].health + "yawble-worker-1",
		"docker ps -a --filter label=yawble.role=worker --format {{.Names}}",
		"docker ps -a --filter name=^yawble-worker- --format {{.Names}}",
		engines[1].inspect + "yawble-tunnel",
	}
	got := regexp.MustCompile(`--since \S+`).ReplaceAllString(strings.Join(s.Calls, "\n"), "--since <start>")
	got = regexp.MustCompile(`(-p 0\.0\.0\.0:8080:8080|--network container:yawble) --restart .*`).ReplaceAllStringFunc(got, func(m string) string {
		if strings.HasPrefix(m, "-p") {
			return "-p 0.0.0.0:8080:8080 <control>"
		}
		return "--network container:yawble <worker>"
	})
	if got != strings.Join(want, "\n") {
		t.Errorf("calls:\n%s\nwant:\n%s", got, strings.Join(want, "\n"))
	}
}

// Control is waited for first, then every worker in turn until the engine reads it healthy:
// connected to control. The tunnel comes after the last.
func TestUpWaitsForControlThenEachWorkerHealthy(t *testing.T) {
	defer instance.SetPollingForTests(time.Millisecond, time.Minute)()
	st := keyed()
	st.Workers = 2
	s := fresh("podman", engines[0].health, 0)
	starting := engine.Result{Stdout: "starting\n"}
	s.OnSequence(engines[0].health+"yawble-worker-1", starting, starting, engine.Result{Stdout: "healthy\n"})
	s.OnSequence(engines[0].health+"yawble-worker-2", starting, engine.Result{Stdout: "healthy\n"})
	controlHealthy := false
	health := func(string) bool { controlHealthy = true; return true }
	var out bytes.Buffer
	if err := instance.Up(context.Background(), engine.NewPodman(s), st, health, &out); err != nil {
		t.Fatal(err)
	}
	if !controlHealthy {
		t.Error("control was not waited for")
	}
	var order []string
	for _, c := range s.Calls {
		switch {
		case strings.HasPrefix(c, "podman logs --follow"):
			order = append(order, "control")
		case strings.HasPrefix(c, engines[0].health):
			order = append(order, strings.TrimPrefix(c, engines[0].health))
		case strings.HasPrefix(c, engines[0].inspect+"yawble-tunnel"):
			order = append(order, "tunnel")
		}
	}
	want := "control yawble-worker-1 yawble-worker-1 yawble-worker-1 yawble-worker-2 yawble-worker-2 tunnel"
	if strings.Join(order, " ") != want {
		t.Errorf("order %q, want %q", strings.Join(order, " "), want)
	}
	if !strings.Contains(out.String(), "yawble-worker-2 is connected to control") {
		t.Errorf("out %q", out.String())
	}
}

// A worker that stops while waited for is said at once, with the command that shows why.
func TestUpStopsWaitingWhenAWorkerStops(t *testing.T) {
	defer instance.SetPollingForTests(time.Millisecond, time.Minute)()
	s := fresh("podman", engines[0].health, 0)
	s.On(engines[0].health+"yawble-worker-1", engine.Result{Stdout: "starting\n"})
	s.OnSequence(engines[0].inspect+"yawble-worker-1", engine.Result{Stderr: "no such container", ExitCode: 125}, engine.Result{Stdout: "exited|x|\n"})
	err := instance.Up(context.Background(), engine.NewPodman(s), keyed(), healthy, &bytes.Buffer{})
	if err == nil || !strings.Contains(err.Error(), "yawble-worker-1 stopped before it connected to control") || !strings.Contains(err.Error(), "yawble logs worker-1") {
		t.Errorf("err %v", err)
	}
}

// Docker: a worker shares control's network namespace, so a control made again takes every
// worker with it, even one whose own settings did not change.
func TestDockerRecreatingControlRecreatesTheWorkers(t *testing.T) {
	old, now := keyed(), keyed()
	now.MaxRunning = 3
	d := engines[1]
	s := engine.NewScripted()
	s.On(d.inspect+"yawble", engine.Result{Stdout: "running|" + img + "|" + label(old) + "\n"})
	ref, _ := now.WorkerRef()
	s.On(d.inspect+"yawble-worker-1", engine.Result{Stdout: "running|" + ref + "|" + workerLabel(now, 1) + "\n"})
	s.On(d.inspect+"yawble-tunnel", engine.Result{Stderr: "No such container", ExitCode: 1})
	s.On(d.health+"yawble-worker-1", engine.Result{Stdout: "healthy\n"})
	if err := instance.Up(context.Background(), engine.NewDocker(s), now, healthy, &bytes.Buffer{}); err != nil {
		t.Fatal(err)
	}
	c := strings.Join(s.Calls, "\n")
	control, worker := strings.Index(c, "docker run -d --name yawble --network"), strings.Index(c, "docker rm -f yawble-worker-1")
	if control < 0 || worker < control || !strings.Contains(c, "docker run -d --name yawble-worker-1 --network container:yawble") {
		t.Errorf("control made again, then the worker:\n%s", c)
	}

	// Podman's pod owns the namespace: the same change leaves the worker running.
	p := engines[0]
	s2 := engine.NewScripted()
	s2.On(p.inspect+"yawble", engine.Result{Stdout: "running|" + img + "|" + label(old) + "\n"})
	s2.On(p.inspect+"yawble-worker-1", engine.Result{Stdout: "running|" + ref + "|" + workerLabel(now, 1) + "\n"})
	s2.On(p.health+"yawble-worker-1", engine.Result{Stdout: "healthy\n"})
	if err := instance.Up(context.Background(), engine.NewPodman(s2), now, healthy, &bytes.Buffer{}); err != nil {
		t.Fatal(err)
	}
	if c := strings.Join(s2.Calls, "\n"); strings.Contains(c, "rm -f yawble-worker-1") || strings.Contains(c, "stop yawble-worker-1") {
		t.Errorf("podman touched the worker:\n%s", c)
	}
}

// The converse: a change to the workers alone leaves control running on both engines.
func TestUpWhereOnlyAWorkerChangedLeavesControlRunning(t *testing.T) {
	old, now := keyed(), keyed()
	old.CPUs = 4
	for _, e := range engines {
		s := engine.NewScripted()
		s.On(e.inspect+"yawble", engine.Result{Stdout: "running|" + img + "|" + label(now) + "\n"})
		s.On(e.inspect+"yawble-worker-1", engine.Result{Stdout: "running|x|" + workerLabel(old, 1) + "\n"})
		s.On(e.health+"yawble-worker-1", engine.Result{Stdout: "healthy\n"})
		if err := instance.Up(context.Background(), e.make(s), now, healthy, &bytes.Buffer{}); err != nil {
			t.Fatal(err)
		}
		c := strings.Join(s.Calls, "\n") + "\n"
		if strings.Contains(c, "rm -f yawble\n") || strings.Contains(c, "stop yawble\n") || !strings.Contains(c, "rm -f yawble-worker-1\n") {
			t.Errorf("%s:\n%s", e.name, c)
		}
	}
}

// After `down`, `up` starts control and waits for it, then starts each worker. On Docker a
// worker started into control's namespace is started after control, never before.
func TestUpAfterDownStartsControlThenEachWorker(t *testing.T) {
	st := keyed()
	st.Workers = 2
	for _, e := range engines {
		s := engine.NewScripted()
		s.On(e.inspect+"yawble", engine.Result{Stdout: "exited|" + img + "|" + label(st) + "\n"})
		ref, _ := st.WorkerRef()
		for i := 1; i <= 2; i++ {
			s.On(e.inspect+instance.WorkerName(i), engine.Result{Stdout: "exited|" + ref + "|" + workerLabel(st, i) + "\n"})
			s.On(e.health+instance.WorkerName(i), engine.Result{Stdout: "healthy\n"})
		}
		controlUp := -1
		health := func(string) bool { controlUp = len(s.Calls); return true }
		if err := instance.Up(context.Background(), e.make(s), st, health, &bytes.Buffer{}); err != nil {
			t.Fatal(err)
		}
		c := strings.Join(s.Calls, "\n") + "\n"
		control := strings.Index(c, e.name+" start yawble\n")
		w1, w2 := strings.Index(c, e.name+" start yawble-worker-1\n"), strings.Index(c, e.name+" start yawble-worker-2\n")
		if control < 0 || w1 < control || w2 < w1 || strings.Contains(c, " run -d") {
			t.Errorf("%s:\n%s", e.name, c)
		}
		before := strings.Join(s.Calls[:controlUp], "\n")
		if strings.Contains(before, "start yawble-worker") {
			t.Errorf("%s: a worker started before control answered:\n%s", e.name, before)
		}
	}
}

// A container from before control and workers: one line says so, it is replaced by control on
// the same volume, and worker 1 joins it. The pod and the volume stay.
func TestUpOnAPreSplitInstanceSaysSoOnce(t *testing.T) {
	old := `{"port":8080,"memory":"12288m","cpus":8,"maxRunning":8,"image":"` + img + `","envHash":""}`
	for _, e := range engines {
		s := engine.NewScripted()
		s.On(e.inspect+"yawble", engine.Result{Stdout: "running|" + img + "|" + old + "\n"})
		s.On(e.health+"yawble-worker-1", engine.Result{Stdout: "healthy\n"})
		s.On(e.inspect+"yawble-worker-1", engine.Result{Stderr: "no such container", ExitCode: 1})
		var out bytes.Buffer
		if err := instance.Up(context.Background(), e.make(s), keyed(), healthy, &out); err != nil {
			t.Fatal(err)
		}
		if n := strings.Count(out.String(), instance.PreSplitLine); n != 1 {
			t.Errorf("%s: the line %d times:\n%s", e.name, n, out.String())
		}
		if strings.Contains(out.String(), "settings changed") {
			t.Errorf("%s: a pre-split container is not a settings change:\n%s", e.name, out.String())
		}
		c := strings.Join(s.Calls, "\n") + "\n"
		rm, control, worker := strings.Index(c, e.name+" rm -f yawble\n"), strings.Index(c, e.name+" run -d --name yawble "), strings.Index(c, e.name+" run -d --name yawble-worker-1 ")
		if rm < 0 || control < rm || worker < control {
			t.Errorf("%s: remove, control, worker:\n%s", e.name, c)
		}
		for _, never := range []string{"volume rm", "volume create", "pod rm", "network rm"} {
			if strings.Contains(c, never) {
				t.Errorf("%s ran %q:\n%s", e.name, never, c)
			}
		}
	}
	if !instance.PreSplit(old) || !instance.PreSplit("") || instance.PreSplit(label(keyed())) {
		t.Error("PreSplit: a label without role is pre-split, control's is not")
	}
}

func TestDownStopsEveryWorkerThenControlOnBothEngines(t *testing.T) {
	for _, e := range engines {
		s := engine.NewScripted()
		s.On(e.name+" ps -a --filter label=yawble.role=worker", engine.Result{Stdout: "yawble-worker-1\nyawble-worker-2\n"})
		s.On(e.inspect, engine.Result{Stdout: "running|x|{}\n"})
		s.On(e.inspect+"yawble-tunnel", engine.Result{Stderr: "no such container", ExitCode: 1})
		var out bytes.Buffer
		if err := instance.Down(context.Background(), e.make(s), 8080, &out); err != nil {
			t.Fatal(err)
		}
		var stops []string
		for _, c := range s.Calls {
			if strings.HasPrefix(c, e.name+" stop ") {
				stops = append(stops, strings.TrimPrefix(c, e.name+" stop "))
			}
		}
		if strings.Join(stops, " ") != "yawble-worker-2 yawble-worker-1 yawble" {
			t.Errorf("%s stops %q", e.name, stops)
		}
		if !strings.Contains(out.String(), "stopped yawble-worker-2, yawble-worker-1") {
			t.Errorf("%s out %q", e.name, out.String())
		}
	}
}

func TestWorkerImageDerivesFromTheControlRef(t *testing.T) {
	for control, want := range map[string]string{
		"ghcr.io/djlsystems/yawble:2026.10.05.1": "ghcr.io/djlsystems/yawble:2026.10.05.1-worker",
		"localhost/yawble:dev-abc1234":           "localhost/yawble:dev-abc1234-worker",
		"localhost/yawble":                       "localhost/yawble:latest-worker",
		"localhost:5000/yawble":                  "localhost:5000/yawble:latest-worker",
	} {
		if got, err := instance.WorkerImage(control); err != nil || got != want {
			t.Errorf("%s: %q %v, want %q", control, got, err, want)
		}
	}
	digest := "ghcr.io/djlsystems/yawble@sha256:0123"
	if _, err := instance.WorkerImage(digest); err == nil || !strings.Contains(err.Error(), "yawble config set workerImage") {
		t.Errorf("digest: %v", err)
	}
	st := keyed()
	st.Image, st.WorkerImage = digest, "ghcr.io/me/worker:1"
	if ref, err := st.WorkerRef(); err != nil || ref != "ghcr.io/me/worker:1" {
		t.Errorf("override: %q %v", ref, err)
	}
	st.WorkerImage = ""
	if err := instance.Up(context.Background(), engine.NewPodman(engine.NewScripted()), st, healthy, &bytes.Buffer{}); err == nil || !strings.Contains(err.Error(), "workerImage") {
		t.Errorf("up with a digest and no workerImage: %v", err)
	}
}

func TestBoundAcceptsATotalEqualToTheEngineAndRefusesOneMiBMore(t *testing.T) {
	// 3 × 4096 + 1536 = 13824.
	if _, refusal := instance.Bound(13824, 64, 3, 4096, 4); refusal != nil {
		t.Errorf("equal to the engine: %v", refusal)
	}
	_, refusal := instance.Bound(13823, 64, 3, 4096, 4)
	if refusal == nil {
		t.Fatal("one MiB more was accepted")
	}
	for _, figure := range []string{"3 worker(s) × 4096 MB", "control's 1536 MB", "= 13824 MB", "the 13823 MB the engine has"} {
		if !strings.Contains(refusal.Error(), figure) {
			t.Errorf("refusal %q lacks %q", refusal, figure)
		}
	}
}

func TestCPUsOverTheEngineOnlyWarn(t *testing.T) {
	cpus, refusal := instance.Bound(1<<20, 8, 3, 1024, 4)
	if refusal != nil {
		t.Errorf("CPUs refused: %v", refusal)
	}
	if !strings.Contains(cpus, "3 worker(s) × 4 CPUs + control's 2 = 14 CPUs, more than the engine's 8") {
		t.Errorf("warning %q", cpus)
	}
	if cpus, _ := instance.Bound(1<<20, 14, 3, 1024, 4); cpus != "" {
		t.Errorf("equal CPUs warned: %q", cpus)
	}
}

// runsScript answers a worker's runs from a list of answers, one per ask, the last repeated.
func runsScript(answers ...[]instance.Run) (instance.RunsOf, *[]string) {
	var asked []string
	n := 0
	return func(_ context.Context, id string) ([]instance.Run, error) {
		asked = append(asked, id)
		if id != "worker-3" {
			return nil, nil
		}
		r := answers[min(n, len(answers)-1)]
		n++
		return r, nil
	}, &asked
}

func threeRunning(e struct {
	name, inspect, health string
	make                  func(engine.Runner) engine.Engine
}) *engine.Scripted {
	s := engine.NewScripted()
	s.On(e.name+" ps -a --filter label=yawble.role=worker", engine.Result{Stdout: "yawble-worker-1\nyawble-worker-2\nyawble-worker-3\n"})
	st := keyed()
	ref, _ := st.WorkerRef()
	for i := 1; i <= 3; i++ {
		s.On(e.inspect+instance.WorkerName(i), engine.Result{Stdout: "running|" + ref + "|" + workerLabel(st, i) + "\n"})
		s.On(e.health+instance.WorkerName(i), engine.Result{Stdout: "healthy\n"})
	}
	return s
}

// workers 1: the highest goes first. It is told to drain, waited for while control still records
// runs on it, and stopped only once the answer is none; then the next.
func TestWorkersOneDrainsWaitsForRunsThenStopsHighestFirst(t *testing.T) {
	defer instance.SetDrainPollingForTests(time.Millisecond, time.Minute)()
	two := []instance.Run{{"r1", "acme", "Ada"}, {"r2", "acme", "Bo"}}
	for _, e := range engines {
		s := threeRunning(e)
		runsOf, _ := runsScript(two, two[:1], nil)
		st := keyed()
		var out bytes.Buffer
		if err := instance.Scale(context.Background(), e.make(s), st, runsOf, instance.ScaleOptions{}, &out); err != nil {
			t.Fatal(err)
		}
		var steps []string
		for _, c := range s.Calls {
			for _, verb := range []string{" exec ", " stop ", " rm -f "} {
				if strings.Contains(c, verb) {
					steps = append(steps, strings.TrimPrefix(c, e.name+" "))
				}
			}
		}
		want := []string{
			"exec -e HARNESS_WORKER_KEY= yawble-worker-3 touch /var/lib/harness-worker/drain",
			"stop -t 30 yawble-worker-3",
			"rm -f yawble-worker-3",
			"stop -t 30 yawble-worker-2",
			"rm -f yawble-worker-2",
		}
		if strings.Join(steps, "\n") != strings.Join(want, "\n") {
			t.Errorf("%s steps:\n%s\nwant:\n%s", e.name, strings.Join(steps, "\n"), strings.Join(want, "\n"))
		}
		o := out.String()
		waiting := "worker-3: 2 runs still going (acme/Ada, acme/Bo); waiting. `--now` stops it at once."
		stopped := strings.Index(o, "stopped and removed yawble-worker-3")
		if i := strings.Index(o, waiting); i < 0 || stopped < i || !strings.Contains(o, "worker-3: 1 run still going (acme/Ada)") {
			t.Errorf("%s out:\n%s", e.name, o)
		}
		if strings.Contains(o, "worker-2: ") {
			t.Errorf("%s: worker-2 had no runs and is stopped with no wait:\n%s", e.name, o)
		}
	}
}

// A drain that stops at once is what the test above must catch: here the runs never end, and
// nothing is stopped before the drain gives up.
func TestADrainNeverStopsAWorkerThatStillHoldsRuns(t *testing.T) {
	defer instance.SetDrainPollingForTests(time.Millisecond, 20*time.Millisecond)()
	runsOf, _ := runsScript([]instance.Run{{"r1", "acme", "Ada"}})
	s := threeRunning(engines[0])
	err := instance.Scale(context.Background(), engine.NewPodman(s), keyed(), runsOf, instance.ScaleOptions{}, &bytes.Buffer{})
	if err == nil || !strings.Contains(err.Error(), "left running and draining") {
		t.Errorf("err %v", err)
	}
	for _, c := range s.Calls {
		if strings.Contains(c, "stop") || strings.Contains(c, "rm -f") {
			t.Errorf("stopped a worker with runs: %q", c)
		}
	}
}

// --now names the runs that fail worker-lost and asks; a no keeps the worker and stops nothing.
func TestWorkersOneNowWarnsNamingTheRunsThatFailWorkerLostAndANoKeepsIt(t *testing.T) {
	runsOf, _ := runsScript([]instance.Run{{"r1", "acme", "Ada"}, {"r2", "beta", "Cy"}})
	s := threeRunning(engines[0])
	var asked []string
	var out bytes.Buffer
	opt := instance.ScaleOptions{Now: true, Confirm: func(q string) (bool, error) { asked = append(asked, q); return false, nil }}
	if err := instance.Scale(context.Background(), engine.NewPodman(s), keyed(), runsOf, opt, &out); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(out.String(), "Stopping worker-3 now: this fails its 2 runs as worker-lost (acme/Ada, beta/Cy); the Manager re-sends each once.") || len(asked) != 1 {
		t.Errorf("out %q asked %q", out.String(), asked)
	}
	for _, c := range s.Calls {
		if strings.Contains(c, "rm -f") || strings.Contains(c, " stop ") || strings.Contains(c, "drain") {
			t.Errorf("a no stopped something: %q", c)
		}
	}
}

// Runs that cannot be read are never taken as none: without --now nothing is stopped.
func TestWorkersRefusesToStopAWorkerWhoseRunsAreNotKnown(t *testing.T) {
	s := threeRunning(engines[0])
	unknown := func(context.Context, string) ([]instance.Run, error) { return nil, errors.New("control did not answer") }
	err := instance.Scale(context.Background(), engine.NewPodman(s), keyed(), unknown, instance.ScaleOptions{}, &bytes.Buffer{})
	if err == nil || !strings.Contains(err.Error(), "runs on worker-3 are not known") || !strings.Contains(err.Error(), "--now") {
		t.Errorf("err %v", err)
	}
	for _, c := range s.Calls {
		if strings.Contains(c, "rm -f") || strings.Contains(c, " stop ") {
			t.Errorf("stopped: %q", c)
		}
	}
}

func TestWorkersThreeAddsWorkerTwoAndThreeAndWaitsHealthy(t *testing.T) {
	st := keyed()
	st.Workers = 3
	for _, e := range engines {
		s := engine.NewScripted()
		ref, _ := st.WorkerRef()
		s.On(e.name+" ps -a --filter label=yawble.role=worker", engine.Result{Stdout: "yawble-worker-1\n"})
		s.On(e.inspect+"yawble-worker-1", engine.Result{Stdout: "running|" + ref + "|" + workerLabel(st, 1) + "\n"})
		s.On(e.inspect, engine.Result{Stderr: "no such container", ExitCode: 1})
		for i := 1; i <= 3; i++ {
			s.On(e.health+instance.WorkerName(i), engine.Result{Stdout: "healthy\n"})
		}
		var out bytes.Buffer
		if err := instance.Scale(context.Background(), e.make(s), st, nil, instance.ScaleOptions{}, &out); err != nil {
			t.Fatal(err)
		}
		lines := runLines(s)
		if len(lines) != 2 || !strings.Contains(lines[0], "--name yawble-worker-2 ") || !strings.Contains(lines[1], "--name yawble-worker-3 ") {
			t.Errorf("%s runs %q", e.name, lines)
		}
		c := strings.Join(s.Calls, "\n")
		for _, never := range []string{"volume", "pod create", "pod rm", "network create", "network rm", "run -d --name yawble ", "stop yawble\n", "rm -f yawble-worker-1"} {
			if strings.Contains(c, never) {
				t.Errorf("%s: adding workers touched %q:\n%s", e.name, never, c)
			}
		}
		if !strings.Contains(out.String(), "yawble-worker-3 is connected to control") {
			t.Errorf("%s out %q", e.name, out.String())
		}
	}
}
