package engine_test

import (
	"context"
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/engine"
)

func TestDockerHasNoPodsSoTheNetworkAndThePublishedPortStandIn(t *testing.T) {
	s := engine.NewScripted()
	d := engine.NewDocker(s)
	ctx := context.Background()
	_, _ = d.PodExists(ctx, "yawble")
	_ = d.CreatePod(ctx, "yawble", 8080, 8080)
	_ = d.Run(ctx, engine.RunSpec{
		Name: "yawble", Pod: "yawble", Image: "img", HostPort: 18080, ContainerPort: 8080,
		Volumes: []string{"yawble-data:/data"}, Env: map[string]string{"Wip__MaxRunning": "4"},
		Labels: map[string]string{"yawble.settings": "{}"}, Memory: "8192m", CPUs: 4,
	})
	_ = d.RemovePod(ctx, "yawble")
	want := []string{
		"docker network inspect yawble",
		"docker network create yawble",
		"docker run -d --name yawble --network yawble -p 0.0.0.0:18080:8080 --restart unless-stopped --memory 8192m --cpus 4 -e Wip__MaxRunning=4 --label yawble.settings={} -v yawble-data:/data img",
		"docker network rm yawble",
	}
	if strings.Join(s.Calls, "\n") != strings.Join(want, "\n") {
		t.Errorf("calls:\n%s\nwant:\n%s", strings.Join(s.Calls, "\n"), strings.Join(want, "\n"))
	}
}

func TestDockerExistenceIsAnInspectExitCode(t *testing.T) {
	s := engine.NewScripted()
	s.On("docker volume inspect gone", engine.Result{Stderr: "Error response from daemon: get gone: no such volume", ExitCode: 1})
	s.On("docker image inspect img:1", engine.Result{})
	d := engine.NewDocker(s)
	if ok, err := d.VolumeExists(context.Background(), "gone"); ok || err != nil {
		t.Errorf("gone: %v %v", ok, err)
	}
	if ok, err := d.ImagePresent(context.Background(), "img:1"); !ok || err != nil {
		t.Errorf("img: %v %v", ok, err)
	}
	if s.Calls[0] != "docker volume inspect gone" || s.Calls[1] != "docker image inspect img:1" {
		t.Errorf("calls %q", s.Calls)
	}
}

// Docker 29 says a missing network is "not found", not "no such": measured on Docker Desktop for
// macOS, where the first `yawble up` stopped at `docker network inspect yawble` before creating it.
func TestDockerTreatsNotFoundAsAbsentAsWellAsNoSuch(t *testing.T) {
	s := engine.NewScripted()
	s.On("docker network inspect yawble", engine.Result{Stderr: "Error response from daemon: network yawble not found", ExitCode: 1})
	s.On("docker container inspect", engine.Result{Stderr: "Error response from daemon: container yawble not found", ExitCode: 1})
	s.On("docker volume inspect other", engine.Result{Stderr: "Error response from daemon: permission denied", ExitCode: 1})
	d := engine.NewDocker(s)
	if ok, err := d.PodExists(context.Background(), "yawble"); ok || err != nil {
		t.Errorf("network: %v %v", ok, err)
	}
	if info, err := d.Inspect(context.Background(), "yawble"); err != nil || info.State != engine.StateAbsent {
		t.Errorf("container: %+v %v", info, err)
	}
	if _, err := d.VolumeExists(context.Background(), "other"); err == nil {
		t.Error("a failure that is not a missing object must stay an error")
	}
}

func TestDockerInspectUsesConfigImageAndTreatsNoSuchAsAbsent(t *testing.T) {
	s := engine.NewScripted()
	s.On("docker container inspect --format {{.State.Status}}|{{.Config.Image}}|{{index .Config.Labels \"yawble.settings\"}} yawble", engine.Result{Stdout: "running|img:1|{\"port\":8080}\n"})
	s.On("docker container inspect", engine.Result{Stderr: "Error: No such container: gone", ExitCode: 1})
	d := engine.NewDocker(s)
	info, err := d.Inspect(context.Background(), "yawble")
	if err != nil || info.State != engine.StateRunning || info.Image != "img:1" || info.Label != `{"port":8080}` {
		t.Errorf("info %+v err %v", info, err)
	}
	if info, err := d.Inspect(context.Background(), "gone"); err != nil || info.State != engine.StateAbsent {
		t.Errorf("gone: %+v %v", info, err)
	}
}

func TestDockerVersionExecAndLogsUseTheDockerVerbs(t *testing.T) {
	s := engine.NewScripted()
	s.On("docker version --format {{.Client.Version}}", engine.Result{Stdout: "27.1.0\n"})
	d := engine.NewDocker(s)
	ctx := context.Background()
	v, _ := d.Version(ctx)
	_, _ = d.Exec(ctx, "yawble", "true")
	_ = d.Logs(ctx, "yawble", true, 10, &strings.Builder{})
	_ = d.Start(ctx, "yawble")
	_ = d.Stop(ctx, "yawble")
	_ = d.Remove(ctx, "yawble")
	if v != "27.1.0" || d.Name() != "docker" {
		t.Errorf("version %q name %q", v, d.Name())
	}
	want := "docker version --format {{.Client.Version}}\ndocker exec -e HARNESS_WORKER_KEY= yawble true\ndocker logs --follow --tail 10 yawble\ndocker start yawble\ndocker stop yawble\ndocker rm -f yawble"
	if strings.Join(s.Calls, "\n") != want {
		t.Errorf("calls:\n%s", strings.Join(s.Calls, "\n"))
	}
}

func TestPodmanIgnoresThePublishedPortBecauseThePodHoldsIt(t *testing.T) {
	s := engine.NewScripted()
	_ = engine.NewPodman(s).Run(context.Background(), engine.RunSpec{Name: "y", Pod: "y", Image: "img", HostPort: 18080, ContainerPort: 8080})
	if strings.Contains(s.Calls[0], "-p ") {
		t.Errorf("podman run must not publish; the pod does: %q", s.Calls[0])
	}
}

func TestDockerListByLabel(t *testing.T) {
	s := engine.NewScripted()
	s.On("docker ps -a --filter label=yawble.instance=yawble --format {{.Names}}", engine.Result{Stdout: "yawble-worker-1\nyawble\n"})
	names, err := engine.NewDocker(s).List(context.Background(), "label=yawble.instance=yawble")
	if err != nil || strings.Join(names, ",") != "yawble-worker-1,yawble" {
		t.Errorf("names %q err %v", names, err)
	}
	if s.Calls[0] != "docker ps -a --filter label=yawble.instance=yawble --format {{.Names}}" {
		t.Errorf("call %q", s.Calls[0])
	}
}

// Docker leaves .State.Health nil without a HEALTHCHECK; the guard keeps that "none", not an error
// and never healthy.
func TestDockerHealthReadsStateHealthAndNoneWithoutOne(t *testing.T) {
	const format = "docker container inspect --format {{if .State.Health}}{{.State.Health.Status}}{{end}} "
	s := engine.NewScripted()
	s.On(format+"yawble-worker-1", engine.Result{Stdout: "healthy\n"})
	s.On(format+"yawble-worker-2", engine.Result{Stdout: "unhealthy\n"})
	s.On(format+"plain", engine.Result{Stdout: "\n"})
	s.On(format+"gone", engine.Result{Stderr: "Error response from daemon: No such container: gone", ExitCode: 1})
	d := engine.NewDocker(s)
	for name, want := range map[string]engine.Health{"yawble-worker-1": engine.HealthHealthy, "yawble-worker-2": engine.HealthUnhealthy, "plain": engine.HealthNone, "gone": engine.HealthNone} {
		if got, err := d.Health(context.Background(), name); err != nil || got != want {
			t.Errorf("%s: %q %v, want %q", name, got, err, want)
		}
	}
}

func TestDockerStopWithinGivesTheGraceOnTheLine(t *testing.T) {
	s := engine.NewScripted()
	_ = engine.NewDocker(s).StopWithin(context.Background(), "yawble-worker-3", 30)
	if s.Calls[0] != "docker stop -t 30 yawble-worker-3" {
		t.Errorf("got %q", s.Calls[0])
	}
}

func TestDockerRunCarriesCapDropCapAddAndEveryEnvFileInOrder(t *testing.T) {
	s := engine.NewScripted()
	_ = engine.NewDocker(s).Run(context.Background(), engine.RunSpec{
		Name: "yawble", Pod: "yawble", Image: "img", HostPort: 8080, ContainerPort: 8080,
		CapDrop: []string{"ALL"}, CapAdd: []string{"CHOWN", "KILL"}, EnvFiles: []string{"/c/env", "/c/worker.env"},
	})
	want := "docker run -d --name yawble --network yawble -p 0.0.0.0:8080:8080 --restart unless-stopped --cap-drop ALL --cap-add CHOWN --cap-add KILL --env-file /c/env --env-file /c/worker.env img"
	if s.Calls[0] != want {
		t.Errorf("got  %q\nwant %q", s.Calls[0], want)
	}
}

// Docker refuses -p, --hostname and --dns on a container that joins another's namespace, and
// the namespace is control's: a worker reaches it on 127.0.0.1.
func TestWorkerRunJoinsControlsNamespaceWithNoPublishOrHostname(t *testing.T) {
	s := engine.NewScripted()
	_ = engine.NewDocker(s).Run(context.Background(), engine.RunSpec{
		Name: "yawble-worker-1", Pod: "yawble", Network: "container:yawble", Image: "img-worker",
		HostPort: 8080, ContainerPort: 8080,
	})
	line := s.Calls[0]
	if !strings.HasPrefix(line, "docker run -d --name yawble-worker-1 --network container:yawble --restart unless-stopped") {
		t.Errorf("got %q", line)
	}
	for _, refused := range []string{" -p ", "--publish", "--hostname", "--dns", "--network yawble"} {
		if strings.Contains(line, refused) {
			t.Errorf("%q carries %q", line, refused)
		}
	}
}
