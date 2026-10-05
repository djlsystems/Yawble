package engine_test

import (
	"bytes"
	"context"
	"errors"
	"strings"
	"testing"
	"time"

	"github.com/djlsystems/yawble/cli/internal/engine"
)

const inspectPrefix = "podman container inspect --format {{.State.Status}}|{{.ImageName}}|{{index .Config.Labels \"yawble.settings\"}} "

func TestInspectParsesStateImageAndLabelAndTreatsNoSuchAsAbsent(t *testing.T) {
	s := engine.NewScripted()
	s.On(inspectPrefix+"yawble", engine.Result{Stdout: "running|ghcr.io/x/y:1|{\"port\":8080}\n"})
	s.On(inspectPrefix+"bare", engine.Result{Stdout: "exited||\n"})
	s.On(inspectPrefix+"gone", engine.Result{Stderr: "Error: no such container gone", ExitCode: 125})
	p := engine.NewPodman(s)

	info, err := p.Inspect(context.Background(), "yawble")
	if err != nil || info.State != engine.StateRunning || info.Image != "ghcr.io/x/y:1" || info.Label != `{"port":8080}` {
		t.Errorf("running: %+v %v", info, err)
	}
	info, err = p.Inspect(context.Background(), "bare")
	if err != nil || info.State != engine.StateStopped || info.Image != "" || info.Label != "" {
		t.Errorf("bare: %+v %v", info, err)
	}
	info, err = p.Inspect(context.Background(), "gone")
	if err != nil || info.State != engine.StateAbsent {
		t.Errorf("absent: %+v %v", info, err)
	}
	if st, err := p.ContainerState(context.Background(), "yawble"); err != nil || st != engine.StateRunning {
		t.Errorf("ContainerState: %v %v", st, err)
	}
}

func TestRunBuildsTheWholeCommandLineInAFixedOrder(t *testing.T) {
	s := engine.NewScripted()
	p := engine.NewPodman(s)
	err := p.Run(context.Background(), engine.RunSpec{
		Name: "yawble", Pod: "yawble", Image: "ghcr.io/djlsystems/yawble:2026.09.24.1",
		Volumes: []string{"yawble-data:/data"}, Env: map[string]string{"Wip__MaxRunning": "8"},
		Labels:   map[string]string{"yawble.settings": `{"port":8080}`},
		EnvFiles: []string{"/home/d/.config/yawble/env"}, Memory: "12288m", CPUs: 8,
	})
	if err != nil {
		t.Fatal(err)
	}
	want := "podman run -d --name yawble --pod yawble --restart unless-stopped --memory 12288m --cpus 8 -e Wip__MaxRunning=8 --label yawble.settings={\"port\":8080} --env-file /home/d/.config/yawble/env -v yawble-data:/data ghcr.io/djlsystems/yawble:2026.09.24.1"
	if len(s.Calls) != 1 || s.Calls[0] != want {
		t.Errorf("calls %q\nwant %q", s.Calls, want)
	}
}

func TestRunBuildsTheCommandLineFromTheSpec(t *testing.T) {
	cases := []struct {
		name string
		spec engine.RunSpec
		want string
	}{
		{"omits limits that are not set",
			engine.RunSpec{Name: "y", Pod: "y", Image: "img"},
			"podman run -d --name y --pod y --restart unless-stopped img"},
		{"carries cap-drop and cap-add",
			engine.RunSpec{Name: "y", Pod: "y", Image: "img", CapDrop: []string{"ALL"}, CapAdd: []string{"CHOWN", "KILL"}},
			"podman run -d --name y --pod y --restart unless-stopped --cap-drop ALL --cap-add CHOWN --cap-add KILL img"},
		{"passes every env file in order, skipping an empty one",
			engine.RunSpec{Name: "y", Pod: "y", Image: "img", EnvFiles: []string{"/c/env", "", "/c/worker.env"}},
			"podman run -d --name y --pod y --restart unless-stopped --env-file /c/env --env-file /c/worker.env img"},
		// Podman's pod already shares one network namespace; a Docker-only Network never reaches its line.
		{"joins a worker to the pod and ignores Network",
			engine.RunSpec{Name: "yawble-worker-1", Pod: "yawble", Network: "container:yawble", Image: "img"},
			"podman run -d --name yawble-worker-1 --pod yawble --restart unless-stopped img"},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			s := engine.NewScripted()
			_ = engine.NewPodman(s).Run(context.Background(), c.spec)
			if s.Calls[0] != c.want {
				t.Errorf("got %q\nwant %q", s.Calls[0], c.want)
			}
		})
	}
}

func TestExecRunsInsideTheNamedContainerAndAnswersStdout(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman exec -e HARNESS_WORKER_KEY= yawble dotnet /app/Harness.Host.dll --doctor", engine.Result{Stdout: "prose\n{\"at\":\"x\"}\n"})
	res, err := engine.NewPodman(s).Exec(context.Background(), "yawble", "dotnet", "/app/Harness.Host.dll", "--doctor")
	if err != nil || res.Stdout != "prose\n{\"at\":\"x\"}\n" {
		t.Errorf("res %+v err %v", res, err)
	}
	s.On("podman exec -e HARNESS_WORKER_KEY= gone", engine.Result{Stderr: "Error: no container with name or ID \"gone\" found", ExitCode: 125})
	if _, err := engine.NewPodman(s).Exec(context.Background(), "gone", "true"); err == nil || !strings.Contains(err.Error(), "no container") {
		t.Errorf("err %v", err)
	}
}

type absentRunner struct{}

func (absentRunner) Run(context.Context, string, ...string) (engine.Result, error) {
	return engine.Result{}, errors.New(`exec: "podman": executable file not found in $PATH`)
}

// A podman that is not there at all is a different fact from one that exits non-zero, and the
// two get different advice. The first is typed so a caller can tell.
func TestAMissingPodmanIsANotRunnableErrorAndAFailingOneIsNot(t *testing.T) {
	_, err := engine.NewPodman(absentRunner{}).Version(context.Background())
	var notRunnable *engine.NotRunnable
	if !errors.As(err, &notRunnable) {
		t.Errorf("missing podman: %v", err)
	}
	s := engine.NewScripted()
	s.On("podman version", engine.Result{Stderr: "Cannot connect to Podman", ExitCode: 125})
	_, err = engine.NewPodman(s).Version(context.Background())
	if err == nil || errors.As(err, &notRunnable) || !strings.Contains(err.Error(), "Cannot connect") {
		t.Errorf("failing podman: %v", err)
	}
}

func TestRemoveForcesTheContainerAway(t *testing.T) {
	s := engine.NewScripted()
	if err := engine.NewPodman(s).Remove(context.Background(), "yawble"); err != nil {
		t.Fatal(err)
	}
	if s.Calls[0] != "podman rm -f yawble" {
		t.Errorf("got %q", s.Calls[0])
	}
}

func TestAFailedCommandIsAnErrorCarryingStderr(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman pod create", engine.Result{Stderr: "Error: address already in use", ExitCode: 125})
	err := engine.NewPodman(s).CreatePod(context.Background(), "yawble", 8080, 8080)
	if err == nil || !strings.Contains(err.Error(), "address already in use") {
		t.Errorf("err %v", err)
	}
	if s.Calls[0] != "podman pod create --name yawble -p 0.0.0.0:8080:8080" {
		t.Errorf("got %q", s.Calls[0])
	}
}

func TestExistsMapsExitOneToFalseAndOtherExitsToErrors(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman volume exists here", engine.Result{})
	s.On("podman volume exists gone", engine.Result{ExitCode: 1})
	s.On("podman volume exists broken", engine.Result{Stderr: "cannot connect to Podman", ExitCode: 125})
	p := engine.NewPodman(s)
	if ok, err := p.VolumeExists(context.Background(), "here"); !ok || err != nil {
		t.Errorf("here: %v %v", ok, err)
	}
	if ok, err := p.VolumeExists(context.Background(), "gone"); ok || err != nil {
		t.Errorf("gone: %v %v", ok, err)
	}
	if _, err := p.VolumeExists(context.Background(), "broken"); err == nil || !strings.Contains(err.Error(), "cannot connect") {
		t.Errorf("broken: %v", err)
	}
}

func TestAScriptedSequenceAnswersInOrderAndRepeatsTheLast(t *testing.T) {
	s := engine.NewScripted()
	s.OnSequence("podman x", engine.Result{Stdout: "1"}, engine.Result{Stdout: "2"})
	var got []string
	for range 3 {
		r, _ := s.Run(context.Background(), "podman", "x")
		got = append(got, r.Stdout)
	}
	if strings.Join(got, "") != "122" {
		t.Errorf("got %q", got)
	}
}

func TestLogsStreamTheCannedOutput(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman logs --tail 50 yawble", engine.Result{Stdout: "line1\nline2\n"})
	var out bytes.Buffer
	if err := engine.NewPodman(s).Logs(context.Background(), "yawble", false, 50, &out); err != nil {
		t.Fatal(err)
	}
	if out.String() != "line1\nline2\n" {
		t.Errorf("got %q", out.String())
	}
}

// On Windows with winget's Podman 5.8.3, `-p 0.0.0.0:8080` still makes pasta listen on a
// dual-stack socket (`*:8080`), WSL relays nothing, and http://localhost:8080 is refused while
// the instance answers on the machine's own address.
// `--network pasta:-4` makes pasta listen on 0.0.0.0, which WSL relays. The label says the pod
// was made this way, so an older pod can be told apart and remade.
func TestOnWindowsThePodIsIPv4OnlyAndSaysSo(t *testing.T) {
	s := engine.NewScripted()
	if err := engine.NewFor("podman", s, "windows").CreatePod(context.Background(), "yawble", 8080, 8080); err != nil {
		t.Fatal(err)
	}
	if want := "podman pod create --name yawble --network pasta:-4 --label yawble.pod=pasta-ipv4 -p 0.0.0.0:8080:8080"; s.Calls[0] != want {
		t.Errorf("got  %q\nwant %q", s.Calls[0], want)
	}
	s = engine.NewScripted()
	_ = engine.NewFor("podman", s, "linux").CreatePod(context.Background(), "yawble", 8080, 8080)
	if s.Calls[0] != "podman pod create --name yawble -p 0.0.0.0:8080:8080" {
		t.Errorf("elsewhere the pod is unchanged: %q", s.Calls[0])
	}
}

func TestOnWindowsAPodWithoutTheLabelIsNotCurrent(t *testing.T) {
	ctx := context.Background()
	for label, want := range map[string]bool{"pasta-ipv4\n": true, "\n": false, "<no value>\n": false} {
		s := engine.NewScripted()
		s.On("podman pod inspect", engine.Result{Stdout: label})
		current, err := engine.NewFor("podman", s, "windows").PodCurrent(ctx, "yawble")
		if err != nil || current != want {
			t.Errorf("label %q: current %v err %v", label, current, err)
		}
		if !strings.HasPrefix(s.Calls[0], "podman pod inspect yawble --format") {
			t.Errorf("calls %q", s.Calls)
		}
	}
	s := engine.NewScripted()
	if current, err := engine.NewFor("podman", s, "darwin").PodCurrent(ctx, "yawble"); !current || err != nil || len(s.Calls) != 0 {
		t.Errorf("elsewhere every pod is current and nothing runs: %v %v %q", current, err, s.Calls)
	}
	if current, _ := engine.NewDocker(s).PodCurrent(ctx, "yawble"); !current {
		t.Error("docker has no pod layout to change")
	}
}

// `up` follows the log from the moment it started the container, so an earlier run's lines are
// not replayed as if they were this start's progress.
func TestFollowSinceFollowsFromATime(t *testing.T) {
	at := time.Date(2026, 9, 24, 15, 0, 0, 0, time.UTC)
	for name, e := range map[string]func(engine.Runner) engine.Engine{"podman": engine.NewPodman, "docker": engine.NewDocker} {
		s := engine.NewScripted()
		s.On(name+" logs", engine.Result{Stdout: "line\n"})
		var out bytes.Buffer
		if err := e(s).FollowSince(context.Background(), "yawble", at, &out); err != nil {
			t.Fatal(err)
		}
		if want := name + " logs --follow --since 2026-09-24T15:00:00Z yawble"; s.Calls[0] != want || out.String() != "line\n" {
			t.Errorf("%s: %q out %q", name, s.Calls[0], out.String())
		}
	}
}

// On the release machine `rmi` answers "tag not known" for the image, because there the name is
// the multi-platform manifest list the release built, which `rmi` does not remove. It is removed as a manifest; any other failure is still a failure.
func TestRemovingAnImageThatIsAManifestListRemovesTheList(t *testing.T) {
	ref := "ghcr.io/djlsystems/yawble:2026.09.24.2"
	s := engine.NewScripted()
	s.On("podman rmi", engine.Result{Stderr: "Error: " + ref + ": tag not known", ExitCode: 125})
	if err := engine.NewPodman(s).RemoveImage(context.Background(), ref); err != nil {
		t.Fatalf("err %v, calls %q", err, s.Calls)
	}
	if got := strings.Join(s.Calls, "\n"); got != "podman rmi "+ref+"\npodman manifest rm "+ref {
		t.Errorf("calls:\n%s", got)
	}

	s = engine.NewScripted()
	s.On("podman rmi", engine.Result{Stderr: "Error: image is in use by a container", ExitCode: 2})
	if err := engine.NewPodman(s).RemoveImage(context.Background(), ref); err == nil || len(s.Calls) != 1 {
		t.Errorf("another failure must fail, without a manifest rm: err %v calls %q", err, s.Calls)
	}
}

func TestCopyToCopiesAFolderIntoTheContainer(t *testing.T) {
	s := engine.NewScripted()
	if err := engine.NewPodman(s).CopyTo(context.Background(), "yawble", "/home/d/build/0.1.0", "/data/plugins/x/.incoming-0.1.0"); err != nil {
		t.Fatal(err)
	}
	if err := engine.NewDocker(s).CopyTo(context.Background(), "yawble", `C:\build\0.1.0`, "/data/plugins/x/.incoming-0.1.0"); err != nil {
		t.Fatal(err)
	}
	want := []string{
		"podman cp /home/d/build/0.1.0 yawble:/data/plugins/x/.incoming-0.1.0",
		`docker cp C:\build\0.1.0 yawble:/data/plugins/x/.incoming-0.1.0`,
	}
	if strings.Join(s.Calls, "\n") != strings.Join(want, "\n") {
		t.Errorf("calls %q", s.Calls)
	}
	s.On("podman cp", engine.Result{Stderr: "Error: no such container", ExitCode: 125})
	if err := engine.NewPodman(s).CopyTo(context.Background(), "gone", "/a", "/b"); err == nil || !strings.Contains(err.Error(), "no such container") {
		t.Errorf("err %v", err)
	}
}

func TestExecToStreamsTheProgramsOutputOnBothEngines(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman exec -e HARNESS_WORKER_KEY= yawble tar", engine.Result{Stdout: "tar bytes"})
	s.On("docker exec -e HARNESS_WORKER_KEY= yawble tar", engine.Result{Stdout: "docker tar bytes"})
	var p, d strings.Builder
	if _, err := engine.NewPodman(s).ExecTo(context.Background(), "yawble", &p, "tar", "-C", "/data/repos", "-cf", "-", "x.git"); err != nil {
		t.Fatal(err)
	}
	if _, err := engine.NewDocker(s).ExecTo(context.Background(), "yawble", &d, "tar", "-C", "/data/repos", "-cf", "-", "x.git"); err != nil {
		t.Fatal(err)
	}
	want := []string{
		"podman exec -e HARNESS_WORKER_KEY= yawble tar -C /data/repos -cf - x.git",
		"docker exec -e HARNESS_WORKER_KEY= yawble tar -C /data/repos -cf - x.git",
	}
	if strings.Join(s.Calls, "\n") != strings.Join(want, "\n") || p.String() != "tar bytes" || d.String() != "docker tar bytes" {
		t.Errorf("calls %q, podman %q, docker %q", s.Calls, p.String(), d.String())
	}
	s.On("podman exec -e HARNESS_WORKER_KEY= gone", engine.Result{Stderr: "Error: no container with name or ID \"gone\" found", ExitCode: 125})
	if _, err := engine.NewPodman(s).ExecTo(context.Background(), "gone", &p, "true"); err == nil || !strings.Contains(err.Error(), "no container") {
		t.Errorf("err %v", err)
	}
}

// `up` dates a data volume it is about to reuse: the day of each engine's CreatedAt, in either
// form Podman prints it; an answer that is not a date is no date, not a guess.
func TestVolumeCreatedIsTheDayOnBothEngines(t *testing.T) {
	for name, e := range map[string]func(engine.Runner) engine.Engine{"podman": engine.NewPodman, "docker": engine.NewDocker} {
		for answer, want := range map[string]string{
			"2026-09-30T08:12:44Z\n":             "2026-09-30",
			"2026-09-30 10:12:44.5 +0200 CEST\n": "2026-09-30",
			"<no value>\n":                       "",
		} {
			s := engine.NewScripted()
			s.On(name+" volume inspect", engine.Result{Stdout: answer})
			got, err := e(s).VolumeCreated(context.Background(), "yawble-data")
			if err != nil || got != want {
				t.Errorf("%s %q: %q %v, want %q", name, answer, got, err, want)
			}
			if line := name + " volume inspect --format {{.CreatedAt}} yawble-data"; s.Calls[0] != line {
				t.Errorf("%s: %q, want %q", name, s.Calls[0], line)
			}
		}
	}
}

func TestListByLabel(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman ps -a --filter label=yawble.role=worker --format {{.Names}}", engine.Result{Stdout: "yawble-worker-2\nyawble-worker-1\n\n"})
	names, err := engine.NewPodman(s).List(context.Background(), "label=yawble.role=worker")
	if err != nil || strings.Join(names, ",") != "yawble-worker-2,yawble-worker-1" {
		t.Errorf("names %q err %v", names, err)
	}
	s.On("podman ps -a --filter label=none", engine.Result{})
	if names, err := engine.NewPodman(s).List(context.Background(), "label=none"); err != nil || len(names) != 0 {
		t.Errorf("none: %q %v", names, err)
	}
	if s.Calls[0] != "podman ps -a --filter label=yawble.role=worker --format {{.Names}}" {
		t.Errorf("call %q", s.Calls[0])
	}
}

// Podman 4 and later name the health `.State.Health`; an older one only `.State.Healthcheck`,
// and its template error on the first is what makes the second be asked.
func TestHealthReadsHealthStatusAndFallsBackToHealthcheck(t *testing.T) {
	const current = "podman container inspect --format {{.State.Health.Status}} "
	const older = "podman container inspect --format {{.State.Healthcheck.Status}} "
	s := engine.NewScripted()
	s.On(current+"yawble-worker-1", engine.Result{Stdout: "healthy\n"})
	s.On(current+"yawble-worker-2", engine.Result{Stderr: `template: inspect:1:8: executing "inspect" at <.State.Health.Status>: can't evaluate field Health`, ExitCode: 125})
	s.On(older+"yawble-worker-2", engine.Result{Stdout: "starting\n"})
	s.On(current+"plain", engine.Result{Stdout: "\n"})
	s.On(current+"gone", engine.Result{Stderr: "Error: no such container gone", ExitCode: 125})
	p := engine.NewPodman(s)
	ctx := context.Background()
	for name, want := range map[string]engine.Health{"yawble-worker-1": engine.HealthHealthy, "yawble-worker-2": engine.HealthStarting, "plain": engine.HealthNone, "gone": engine.HealthNone} {
		if got, err := p.Health(ctx, name); err != nil || got != want {
			t.Errorf("%s: %q %v, want %q", name, got, err, want)
		}
	}
	if !strings.Contains(strings.Join(s.Calls, "\n"), older+"yawble-worker-2") {
		t.Errorf("the older field was not asked:\n%s", strings.Join(s.Calls, "\n"))
	}
}

func TestStopWithinGivesTheGraceOnTheLine(t *testing.T) {
	s := engine.NewScripted()
	if err := engine.NewPodman(s).StopWithin(context.Background(), "yawble-worker-3", 30); err != nil {
		t.Fatal(err)
	}
	if s.Calls[0] != "podman stop -t 30 yawble-worker-3" {
		t.Errorf("got %q", s.Calls[0])
	}
}

func TestExecBlanksTheWorkerKey(t *testing.T) {
	for _, e := range []struct {
		name string
		make func(engine.Runner) engine.Engine
	}{{"podman", engine.NewPodman}, {"docker", engine.NewDocker}} {
		s := engine.NewScripted()
		en := e.make(s)
		ctx := context.Background()
		_, _ = en.Exec(ctx, "yawble-worker-1", "sh", "-c", "true")
		_, _ = en.ExecTo(ctx, "yawble", &bytes.Buffer{}, "tar", "-cf", "-")
		_, _ = en.ExecInput(ctx, "yawble", "code", "sh", "-c", "cat")
		want := []string{
			e.name + " exec -e HARNESS_WORKER_KEY= yawble-worker-1 sh -c true",
			e.name + " exec -e HARNESS_WORKER_KEY= yawble tar -cf -",
			e.name + " exec -i -e HARNESS_WORKER_KEY= yawble sh -c cat",
		}
		if strings.Join(s.Calls, "\n") != strings.Join(want, "\n") {
			t.Errorf("%s calls:\n%s\nwant:\n%s", e.name, strings.Join(s.Calls, "\n"), strings.Join(want, "\n"))
		}
		// The backup helper is no instance container: it is given no env file at all.
		_, _ = en.RunHelper(ctx, engine.HelperSpec{Image: "img", Volume: "yawble-data", Target: "/data", Entrypoint: "tar"})
		if helper := s.Calls[len(s.Calls)-1]; strings.Contains(helper, "--env-file") || strings.Contains(helper, "HARNESS_WORKER_KEY") {
			t.Errorf("%s helper line %q", e.name, helper)
		}
	}
}
