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
	want := "docker version --format {{.Client.Version}}\ndocker exec yawble true\ndocker logs --follow --tail 10 yawble\ndocker start yawble\ndocker stop yawble\ndocker rm -f yawble"
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
