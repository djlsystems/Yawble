package instance_test

import (
	"bytes"
	"context"
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/instance"
)

// previewed is the settings with a three-port preview range and the ports in taken held by
// something else on this computer.
func previewed(taken ...int) instance.Settings {
	st := settings()
	st.PreviewPorts = []int{28081, 28082, 28083}
	st.PortFree = func(port int) bool {
		for _, t := range taken {
			if t == port {
				return false
			}
		}
		return true
	}
	return st
}

// madeWith is control's label for settings st whose preview ports published were these.
func madeWith(st instance.Settings, published ...int) string {
	st.PreviewPublished = append([]int{}, published...)
	return label(st)
}

func countPrefix(calls []string, prefix string) int {
	n := 0
	for _, c := range calls {
		if strings.HasPrefix(c, prefix) {
			n++
		}
	}
	return n
}

// A pod and control from before the preview range: `up` makes both again once, publishing the
// range, and says so; the next `up` on what it made changes nothing.
func TestUpRemakesAPodWithoutThePreviewRangeOnceAndSaysSo(t *testing.T) {
	st := previewed()
	s := engine.NewScripted()
	s.OnSequence(inspect, engine.Result{Stdout: "running|" + img + "|" + label(settings()) + "\n"}, engine.Result{Stdout: "running|" + img + "|" + madeWith(st, 28081, 28082, 28083) + "\n"})
	s.On(tunnelInspect, engine.Result{Stderr: "no such container", ExitCode: 125})
	workerAs(s, "running", st, 1)
	var out bytes.Buffer
	if err := instance.Up(context.Background(), engine.NewPodman(s), st, healthy, &out); err != nil {
		t.Fatal(err)
	}
	if countPrefix(s.Calls, "podman pod rm -f yawble") != 1 || countPrefix(s.Calls, "podman pod create --name yawble -p 0.0.0.0:8080:8080 -p 0.0.0.0:28081:28081 -p 0.0.0.0:28082:28082 -p 0.0.0.0:28083:28083") != 1 {
		t.Errorf("the pod was not made again once with the range:\n%s", strings.Join(s.Calls, "\n"))
	}
	runs := 0
	for _, c := range s.Calls {
		if strings.HasPrefix(c, "podman run -d --name yawble --pod") {
			runs++
			if !strings.Contains(c, " -e HARNESS_PUBLISHED_PREVIEW_PORTS=28081,28082,28083 ") {
				t.Errorf("control is not told the published ports: %s", c)
			}
		}
	}
	if runs != 1 {
		t.Errorf("control ran %d times", runs)
	}
	if !strings.Contains(out.String(), "previewPorts none -> 28081-28083") || !strings.Contains(out.String(), "recreated pod yawble publishing port 8080 and preview ports 28081-28083") {
		t.Errorf("output does not say why the pod was made again: %q", out.String())
	}

	// The next up, on the instance it made: nothing is made again.
	s.Calls = nil
	out.Reset()
	if err := instance.Up(context.Background(), engine.NewPodman(s), st, healthy, &out); err != nil {
		t.Fatal(err)
	}
	for _, c := range s.Calls {
		if strings.Contains(c, " rm ") || strings.Contains(c, "create") || strings.HasPrefix(c, "podman run") {
			t.Errorf("the second up ran %q", c)
		}
	}
}

// Docker has no pod: control publishes, and a control from before the range is made again once.
func TestUpRemakesADockerControlWithoutThePreviewRangeOnce(t *testing.T) {
	st := previewed()
	s := engine.NewScripted()
	s.On(engines[1].inspect+"yawble", engine.Result{Stdout: "running|" + img + "|" + label(settings()) + "\n"})
	s.On(engines[1].inspect+"yawble-worker-1", engine.Result{Stdout: "running|" + img + "-worker|" + workerLabel(st, 1) + "\n"})
	s.On(engines[1].health+"yawble-worker-1", engine.Result{Stdout: "healthy\n"})
	s.On(engines[1].inspect+"yawble-tunnel", engine.Result{Stderr: "No such container", ExitCode: 1})
	var out bytes.Buffer
	if err := instance.Up(context.Background(), engine.NewDocker(s), st, healthy, &out); err != nil {
		t.Fatal(err)
	}
	if countPrefix(s.Calls, "docker rm -f yawble") < 1 || countPrefix(s.Calls, "docker run -d --name yawble --network yawble -p 0.0.0.0:8080:8080 -p 0.0.0.0:28081:28081 -p 0.0.0.0:28082:28082 -p 0.0.0.0:28083:28083 ") != 1 {
		t.Errorf("control was not made again once with the range:\n%s", strings.Join(s.Calls, "\n"))
	}
	if countPrefix(s.Calls, "docker network rm") != 0 {
		t.Errorf("the network was removed for the preview range:\n%s", strings.Join(s.Calls, "\n"))
	}
	if !strings.Contains(out.String(), "previewPorts none -> 28081-28083") {
		t.Errorf("output %q", out.String())
	}
}

// A port the stopped instance published, taken since by something else: the pod is made again
// without it, said in one line, and the instance starts. The same on Docker, where control is.
func TestAPreviewPortTakenWhileTheInstanceWasStoppedIsSkippedAndTheInstanceStarts(t *testing.T) {
	const line = "preview port 28082 is already in use on this computer; skipped (the instance starts without it)"
	for _, e := range engines {
		st := previewed(28082)
		s := engine.NewScripted()
		s.On(e.inspect+"yawble", engine.Result{Stdout: "exited|" + img + "|" + madeWith(st, 28081, 28082, 28083) + "\n"})
		s.On(e.inspect+"yawble-worker-1", engine.Result{Stdout: "running|" + img + "-worker|" + workerLabel(st, 1) + "\n"})
		s.On(e.health+"yawble-worker-1", engine.Result{Stdout: "healthy\n"})
		s.On(e.inspect+"yawble-tunnel", engine.Result{Stderr: "no such container", ExitCode: 125})
		s.On("podman pod inspect yawble --format {{.State}}", engine.Result{Stdout: "Exited\n"})
		var out bytes.Buffer
		if err := instance.Up(context.Background(), e.make(s), st, healthy, &out); err != nil {
			t.Fatalf("%s: %v", e.name, err)
		}
		if strings.Count(out.String(), line) != 1 {
			t.Errorf("%s: output %q", e.name, out.String())
		}
		joined := strings.Join(s.Calls, "\n")
		for _, c := range s.Calls {
			if (strings.HasPrefix(c, "podman pod create") || strings.HasPrefix(c, "docker run -d --name yawble ")) && (strings.Contains(c, "28082") || !strings.Contains(c, "28081:28081") || !strings.Contains(c, "28083:28083")) {
				t.Errorf("%s publishes the taken port, or not the others: %s", e.name, c)
			}
			if strings.HasPrefix(c, e.name+" run -d --name yawble ") && !strings.Contains(c, " -e HARNESS_PUBLISHED_PREVIEW_PORTS=28081,28083 ") {
				t.Errorf("%s: control is not told exactly the published ports: %s", e.name, c)
			}
		}
		if countPrefix(s.Calls, e.name+" run -d --name yawble ") != 1 {
			t.Errorf("%s: control was not made again:\n%s", e.name, joined)
		}
	}
}

// A stopped instance whose preview ports are all still free starts as it is.
func TestAStoppedInstanceWithItsPreviewPortsFreeStartsAsItIs(t *testing.T) {
	st := previewed()
	s := engine.NewScripted()
	s.On(inspect, engine.Result{Stdout: "exited|" + img + "|" + madeWith(st, 28081, 28082, 28083) + "\n"})
	s.On("podman pod inspect yawble --format {{.State}}", engine.Result{Stdout: "Exited\n"})
	workerAs(s, "exited", st, 1)
	if err := instance.Up(context.Background(), engine.NewPodman(s), st, healthy, &bytes.Buffer{}); err != nil {
		t.Fatal(err)
	}
	joined := strings.Join(s.Calls, "\n")
	if !strings.Contains(joined, "podman start yawble\n") || strings.Contains(joined, "podman run") || strings.Contains(joined, " rm ") {
		t.Errorf("calls:\n%s", joined)
	}
}

// The engine refusing a preview port the probe saw free never stops the instance: it starts
// without the preview ports, said, and control is told none.
func TestAPreviewPortTheEngineCannotBindDoesNotStopTheInstance(t *testing.T) {
	st := previewed()
	s := engine.NewScripted()
	s.On("podman volume exists", engine.Result{ExitCode: 1})
	s.On("podman pod exists", engine.Result{ExitCode: 1})
	s.On("podman container inspect", engine.Result{Stderr: "no such container", ExitCode: 125})
	s.On(healthPrefix+"yawble-worker-1", engine.Result{Stdout: "healthy\n"})
	s.OnSequence("podman run -d --name yawble --pod", engine.Result{Stderr: "Error: starting some containers: internal libpod error", ExitCode: 126}, engine.Result{})
	var out bytes.Buffer
	if err := instance.Up(context.Background(), engine.NewPodman(s), st, healthy, &out); err != nil {
		t.Fatal(err)
	}
	creates := []string{}
	for _, c := range s.Calls {
		if strings.HasPrefix(c, "podman pod create") {
			creates = append(creates, c)
		}
	}
	if len(creates) != 2 || !strings.Contains(creates[0], "28081:28081") || creates[1] != "podman pod create --name yawble -p 0.0.0.0:8080:8080" {
		t.Errorf("pod creates %q", creates)
	}
	if !strings.Contains(out.String(), "the preview ports could not be published") || !strings.Contains(out.String(), "Yawble is up at") {
		t.Errorf("output %q", out.String())
	}
	var runs []string
	for _, c := range s.Calls {
		if strings.HasPrefix(c, "podman run -d --name yawble --pod") {
			runs = append(runs, c)
		}
	}
	if len(runs) != 2 || strings.Contains(runs[1], "HARNESS_PUBLISHED_PREVIEW_PORTS") {
		t.Errorf("control runs %q", runs)
	}
}

// Yawble's own ports are never a preview's.
func TestTheHostsOwnPortIsNeverAPreviewPort(t *testing.T) {
	st := previewed()
	st.PreviewPorts = []int{8080, 8081}
	s := engine.NewScripted()
	s.On("podman volume exists", engine.Result{ExitCode: 1})
	s.On("podman pod exists", engine.Result{ExitCode: 1})
	s.On("podman container inspect", engine.Result{Stderr: "no such container", ExitCode: 125})
	s.On(healthPrefix+"yawble-worker-1", engine.Result{Stdout: "healthy\n"})
	var out bytes.Buffer
	if err := instance.Up(context.Background(), engine.NewPodman(s), st, healthy, &out); err != nil {
		t.Fatal(err)
	}
	if countPrefix(s.Calls, "podman pod create --name yawble -p 0.0.0.0:8080:8080 -p 0.0.0.0:8081:8081") != 1 || !strings.Contains(out.String(), "preview port 8080 is Yawble's own port; skipped") {
		t.Errorf("out %q calls:\n%s", out.String(), strings.Join(s.Calls, "\n"))
	}
}
