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
		Labels:  map[string]string{"yawble.settings": `{"port":8080}`},
		EnvFile: "/home/d/.config/yawble/env", Memory: "12288m", CPUs: 8,
	})
	if err != nil {
		t.Fatal(err)
	}
	want := "podman run -d --name yawble --pod yawble --restart unless-stopped --memory 12288m --cpus 8 -e Wip__MaxRunning=8 --label yawble.settings={\"port\":8080} --env-file /home/d/.config/yawble/env -v yawble-data:/data ghcr.io/djlsystems/yawble:2026.09.24.1"
	if len(s.Calls) != 1 || s.Calls[0] != want {
		t.Errorf("calls %q\nwant %q", s.Calls, want)
	}
}

func TestRunOmitsLimitsThatAreNotSet(t *testing.T) {
	s := engine.NewScripted()
	_ = engine.NewPodman(s).Run(context.Background(), engine.RunSpec{Name: "y", Pod: "y", Image: "img"})
	if s.Calls[0] != "podman run -d --name y --pod y --restart unless-stopped img" {
		t.Errorf("got %q", s.Calls[0])
	}
}

func TestExecRunsInsideTheNamedContainerAndAnswersStdout(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman exec yawble dotnet /app/Harness.Host.dll --doctor", engine.Result{Stdout: "prose\n{\"at\":\"x\"}\n"})
	res, err := engine.NewPodman(s).Exec(context.Background(), "yawble", "dotnet", "/app/Harness.Host.dll", "--doctor")
	if err != nil || res.Stdout != "prose\n{\"at\":\"x\"}\n" {
		t.Errorf("res %+v err %v", res, err)
	}
	s.On("podman exec gone", engine.Result{Stderr: "Error: no container with name or ID \"gone\" found", ExitCode: 125})
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
