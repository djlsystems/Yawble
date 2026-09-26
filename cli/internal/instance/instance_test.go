package instance_test

import (
	"bytes"
	"context"
	"regexp"
	"strings"
	"testing"
	"time"

	"github.com/djlsystems/yawble/cli/internal/config"
	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/instance"
)

const img = "ghcr.io/djlsystems/yawble:2026.09.24.1"

const inspect = "podman container inspect --format {{.State.Status}}|{{.ImageName}}|{{index .Config.Labels \"yawble.settings\"}} yawble"

func settings() instance.Settings {
	return instance.Settings{Port: 8080, Memory: "12288m", CPUs: 8, MaxRunning: 8, Image: img}
}

// label is what Run stamps on the container and what a later Up compares against.
func label(s instance.Settings) string { return instance.SettingsLabel(s) }

func healthy(string) bool { return true }

func unhealthy(string) bool { return false }

func TestUpOnAFreshMachineCreatesEverythingInOrder(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman volume exists", engine.Result{ExitCode: 1})
	s.On("podman pod exists", engine.Result{ExitCode: 1})
	s.On("podman container inspect", engine.Result{Stderr: "no such container", ExitCode: 125})
	s.On("podman image exists", engine.Result{ExitCode: 1})
	var out bytes.Buffer

	if err := instance.Up(context.Background(), engine.NewPodman(s), settings(), healthy, &out); err != nil {
		t.Fatal(err)
	}

	want := []string{
		"podman volume exists yawble-data",
		"podman volume create yawble-data",
		"podman pod exists yawble",
		"podman pod create --name yawble -p 0.0.0.0:8080:8080",
		inspect,
		"podman image exists " + img,
		"podman pull " + img,
		"podman run -d --name yawble --pod yawble --restart unless-stopped --memory 12288m --cpus 8 -e Wip__MaxRunning=8 --label yawble.settings=" + label(settings()) + " -v yawble-data:/data " + img,
		// While waiting: the log from this start on, for the progress lines (the time varies).
		"podman logs --follow --since <start> yawble",
		// After health: is there a tunnel sidecar to bring back? (none on a fresh machine)
		"podman container inspect --format {{.State.Status}}|{{.ImageName}}|{{index .Config.Labels \"yawble.settings\"}} yawble-tunnel",
	}
	got := regexp.MustCompile(`--since \S+`).ReplaceAllString(strings.Join(s.Calls, "\n"), "--since <start>")
	if got != strings.Join(want, "\n") {
		t.Errorf("calls:\n%s\nwant:\n%s", got, strings.Join(want, "\n"))
	}
	if !strings.Contains(out.String(), "http://127.0.0.1:8080") {
		t.Errorf("output %q lacks the URL", out.String())
	}
}

func TestUpOnARunningInstanceWithTheSameSettingsChangesNothing(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman container inspect", engine.Result{Stdout: "running|" + img + "|" + label(settings()) + "\n"})
	var out bytes.Buffer
	if err := instance.Up(context.Background(), engine.NewPodman(s), settings(), healthy, &out); err != nil {
		t.Fatal(err)
	}
	for _, c := range s.Calls {
		if strings.HasPrefix(c, "podman run") || strings.HasPrefix(c, "podman start") || strings.Contains(c, "create") || strings.Contains(c, " rm ") {
			t.Errorf("up on a running instance ran %q", c)
		}
	}
	if !strings.Contains(out.String(), "already running") {
		t.Errorf("output %q", out.String())
	}
}

func TestUpAfterDownStartsTheExistingContainer(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman container inspect", engine.Result{Stdout: "exited|" + img + "|" + label(settings()) + "\n"})
	var out bytes.Buffer
	if err := instance.Up(context.Background(), engine.NewPodman(s), settings(), healthy, &out); err != nil {
		t.Fatal(err)
	}
	joined := strings.Join(s.Calls, "\n")
	if !strings.Contains(joined, "podman start yawble") || strings.Contains(joined, "podman run") || strings.Contains(joined, " rm ") {
		t.Errorf("calls:\n%s", joined)
	}
}

// Critical 1 from the review: `config set` promises the next `up` applies it. A container made
// with other settings is replaced, on the same volume.
func TestUpRecreatesTheContainerWhenTheSettingsChanged(t *testing.T) {
	old := settings()
	old.Memory = "8192m"
	s := engine.NewScripted()
	s.On(inspect, engine.Result{Stdout: "exited|" + img + "|" + label(old) + "\n"})
	s.On(tunnelInspect, engine.Result{Stderr: "no such container", ExitCode: 125})
	var out bytes.Buffer
	if err := instance.Up(context.Background(), engine.NewPodman(s), settings(), healthy, &out); err != nil {
		t.Fatal(err)
	}
	joined := strings.Join(s.Calls, "\n")
	for _, want := range []string{"podman rm -f yawble", "podman run -d --name yawble"} {
		if !strings.Contains(joined, want) {
			t.Errorf("missing %q in:\n%s", want, joined)
		}
	}
	if strings.Contains(joined, "pod rm") || strings.Contains(joined, "podman start yawble\n") {
		t.Errorf("a memory change must not touch the pod or start the old container:\n%s", joined)
	}
	if !strings.Contains(out.String(), "settings changed") {
		t.Errorf("output should say why it recreated: %q", out.String())
	}
}

func TestUpRecreatesThePodWhenThePortChanged(t *testing.T) {
	old := settings()
	old.Port = 8080
	now := settings()
	now.Port = 9090
	s := engine.NewScripted()
	s.On("podman container inspect", engine.Result{Stdout: "running|" + img + "|" + label(old) + "\n"})
	var out bytes.Buffer
	if err := instance.Up(context.Background(), engine.NewPodman(s), now, healthy, &out); err != nil {
		t.Fatal(err)
	}
	joined := strings.Join(s.Calls, "\n")
	for _, want := range []string{"podman rm -f yawble", "podman pod rm -f yawble", "podman pod create --name yawble -p 0.0.0.0:9090:8080", "podman run -d --name yawble"} {
		if !strings.Contains(joined, want) {
			t.Errorf("missing %q in:\n%s", want, joined)
		}
	}
	if !strings.Contains(out.String(), "http://127.0.0.1:9090") {
		t.Errorf("output %q", out.String())
	}
}

func TestUpWithNoImageRefusesBeforeTouchingTheEngine(t *testing.T) {
	s := engine.NewScripted()
	st := settings()
	st.Image = ""
	err := instance.Up(context.Background(), engine.NewPodman(s), st, healthy, &bytes.Buffer{})
	if err == nil || !strings.Contains(err.Error(), "config set image") {
		t.Errorf("err %v", err)
	}
	if len(s.Calls) != 0 {
		t.Errorf("engine was called: %q", s.Calls)
	}
}

// Critical 2 from the review: the host port is bound when the pod's infra container starts,
// which is `podman run --pod`, not `pod create`. A failed first run must not leave a half-made
// container and a pod on a port nobody can use.
func TestUpWhenThePortIsTakenNamesThePortCleansUpAndNamesTheSetting(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman volume exists", engine.Result{})
	s.On("podman pod exists", engine.Result{ExitCode: 1})
	s.On("podman container inspect", engine.Result{Stderr: "no such container", ExitCode: 125})
	s.On("podman image exists", engine.Result{})
	s.On("podman run", engine.Result{Stderr: "Error: cannot listen on the TCP port: listen tcp4 0.0.0.0:8080: bind: address already in use", ExitCode: 126})
	err := instance.Up(context.Background(), engine.NewPodman(s), settings(), healthy, &bytes.Buffer{})
	if err == nil || !strings.Contains(err.Error(), "8080") || !strings.Contains(err.Error(), "config set port") {
		t.Errorf("err %v", err)
	}
	joined := strings.Join(s.Calls, "\n")
	for _, want := range []string{"podman rm -f yawble", "podman pod rm -f yawble"} {
		if !strings.Contains(joined, want) {
			t.Errorf("missing %q in:\n%s", want, joined)
		}
	}
}

// Measured on Podman 6.0.2 with the port held by another container: the run fails with
// "Error: starting some containers: internal libpod error" and no mention of the port. The person
// still needs the port named as the thing to check, without being told it IS the cause.
func TestAFailedFirstRunWithAnOpaqueErrorStillPointsAtThePort(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman volume exists", engine.Result{})
	s.On("podman pod exists", engine.Result{ExitCode: 1})
	s.On("podman container inspect", engine.Result{Stderr: "no such container", ExitCode: 125})
	s.On("podman image exists", engine.Result{})
	s.On("podman run", engine.Result{Stderr: "Error: starting some containers: internal libpod error", ExitCode: 126})
	err := instance.Up(context.Background(), engine.NewPodman(s), settings(), healthy, &bytes.Buffer{})
	if err == nil || !strings.Contains(err.Error(), "internal libpod error") || !strings.Contains(err.Error(), "8080") || !strings.Contains(err.Error(), "config set port") {
		t.Errorf("err %v", err)
	}
	if strings.Contains(err.Error(), "is already in use") {
		t.Errorf("must not claim the cause it could not measure: %v", err)
	}
}

// Measured in a fresh WSL distro: a pull that fails on a first up left the pod it had just
// created holding the port. It goes the way a failed run's does.
func TestAFailedPullOnAFirstUpRemovesThePodItCreated(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman volume exists", engine.Result{ExitCode: 1})
	s.On("podman pod exists", engine.Result{ExitCode: 1})
	s.On("podman container inspect", engine.Result{Stderr: "no such container", ExitCode: 125})
	s.On("podman image exists", engine.Result{ExitCode: 1})
	s.On("podman pull", engine.Result{Stderr: "Error: initializing source docker://localhost/x: pinging container registry localhost: connection refused", ExitCode: 125})
	err := instance.Up(context.Background(), engine.NewPodman(s), settings(), healthy, &bytes.Buffer{})
	// The pull streams to the terminal, so podman's own reason has already been shown; the
	// error names the pull and the image.
	if err == nil || !strings.Contains(err.Error(), "pull") || !strings.Contains(err.Error(), img) {
		t.Errorf("err %v", err)
	}
	c := strings.Join(s.Calls, "\n")
	if !strings.Contains(c, "podman pod rm -f yawble") || strings.Contains(c, "volume rm") {
		t.Errorf("the pod this up made must go and the volume must stay:\n%s", c)
	}
}

func TestUpWhenThePortIsTakenLeavesAPodItDidNotCreate(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman volume exists", engine.Result{})
	s.On("podman pod exists", engine.Result{})
	s.On("podman container inspect", engine.Result{Stderr: "no such container", ExitCode: 125})
	s.On("podman image exists", engine.Result{})
	s.On("podman run", engine.Result{Stderr: "address already in use", ExitCode: 126})
	_ = instance.Up(context.Background(), engine.NewPodman(s), settings(), healthy, &bytes.Buffer{})
	joined := strings.Join(s.Calls, "\n")
	if !strings.Contains(joined, "podman rm -f yawble") || strings.Contains(joined, "pod rm") {
		t.Errorf("calls:\n%s", joined)
	}
}

// Important 4 from the review: a container that stops during the wait (a crash loop, a volume
// the Host refuses) must not be waited on for ten minutes.
func TestUpStopsWaitingWhenTheContainerStops(t *testing.T) {
	defer instance.SetPollingForTests(time.Millisecond, time.Minute)()
	s := engine.NewScripted()
	s.OnSequence("podman container inspect",
		engine.Result{Stderr: "no such container", ExitCode: 125},
		engine.Result{Stdout: "running|" + img + "|" + label(settings()) + "\n"},
		engine.Result{Stdout: "exited|" + img + "|" + label(settings()) + "\n"},
	)
	s.On("podman volume exists", engine.Result{})
	s.On("podman pod exists", engine.Result{})
	s.On("podman image exists", engine.Result{})
	started := time.Now()
	err := instance.Up(context.Background(), engine.NewPodman(s), settings(), unhealthy, &bytes.Buffer{})
	if err == nil || !strings.Contains(err.Error(), "stopped") || !strings.Contains(err.Error(), "yawble logs") {
		t.Errorf("err %v", err)
	}
	if time.Since(started) > 10*time.Second {
		t.Errorf("waited %s for a container that had stopped", time.Since(started))
	}
}

// Review Focus 1 of steps 6-9: the new image's Host refuses the volume. The wait reads the log
// and says what the Host said, plus what the person can do.
func TestAHostThatRefusesTheVolumeIsSaidInTheHostsOwnWords(t *testing.T) {
	defer instance.SetPollingForTests(time.Millisecond, time.Minute)()
	s := engine.NewScripted()
	s.OnSequence("podman container inspect",
		engine.Result{Stderr: "no such container", ExitCode: 125},
		engine.Result{Stdout: "running|" + img + "|" + label(settings()) + "\n"},
		engine.Result{Stdout: "exited|" + img + "|" + label(settings()) + "\n"},
	)
	s.On("podman volume exists", engine.Result{})
	s.On("podman pod exists", engine.Result{})
	s.On("podman image exists", engine.Result{})
	s.On("podman logs --tail 50 yawble", engine.Result{Stdout: "Host log: /data/logs/x\nThe database at /data/messages.db was written by a newer build: it records schema steps this build does not know (zzz-999).\n"})
	err := instance.Up(context.Background(), engine.NewPodman(s), settings(), unhealthy, &bytes.Buffer{})
	if err == nil || !strings.Contains(err.Error(), "written by a newer build") || !strings.Contains(err.Error(), "`yawble update` moves yawble and the instance to the latest release") || !strings.Contains(err.Error(), "uninstall --data") {
		t.Errorf("err %v", err)
	}
}

const tunnelInspect = "podman container inspect --format {{.State.Status}}|{{.ImageName}}|{{index .Config.Labels \"yawble.settings\"}} yawble-tunnel"

// The tunnel starts and stops with the instance.
func TestDownStopsTheTunnelSidecarTooAndUpStartsItAgain(t *testing.T) {
	s := engine.NewScripted()
	s.On(inspect, engine.Result{Stdout: "running|" + img + "|" + label(settings()) + "\n"})
	s.On(tunnelInspect, engine.Result{Stdout: "running|docker.io/cloudflare/cloudflared:latest|\n"})
	if err := instance.Down(context.Background(), engine.NewPodman(s), 8080, &bytes.Buffer{}); err != nil {
		t.Fatal(err)
	}
	c := strings.Join(s.Calls, "\n")
	if !strings.Contains(c, "podman stop yawble-tunnel") || !strings.Contains(c, "podman stop yawble") {
		t.Errorf("down calls:\n%s", c)
	}
	s2 := engine.NewScripted()
	s2.On(inspect, engine.Result{Stdout: "exited|" + img + "|" + label(settings()) + "\n"})
	s2.On(tunnelInspect, engine.Result{Stdout: "exited|docker.io/cloudflare/cloudflared:latest|\n"})
	if err := instance.Up(context.Background(), engine.NewPodman(s2), settings(), healthy, &bytes.Buffer{}); err != nil {
		t.Fatal(err)
	}
	c = strings.Join(s2.Calls, "\n")
	start, tunnel := strings.Index(c, "podman start yawble\n"), strings.Index(c, "podman start yawble-tunnel")
	if start < 0 || tunnel < start {
		t.Errorf("up should start the Host, then the tunnel:\n%s", c)
	}
}

// A refused volume must not leave a container that restarts forever.
func TestARefusedVolumeStopsTheContainerSoItDoesNotLoop(t *testing.T) {
	defer instance.SetPollingForTests(time.Millisecond, time.Minute)()
	s := engine.NewScripted()
	s.OnSequence(inspect,
		engine.Result{Stderr: "no such container", ExitCode: 125},
		engine.Result{Stdout: "running|" + img + "|" + label(settings()) + "\n"},
		engine.Result{Stdout: "exited|" + img + "|" + label(settings()) + "\n"},
	)
	s.On("podman volume exists", engine.Result{})
	s.On("podman pod exists", engine.Result{})
	s.On("podman image exists", engine.Result{})
	s.On("podman logs --tail 50 yawble", engine.Result{Stdout: "This database was written by a newer build of Harness: it records schema steps this build does not know (zzz-999).\n"})
	err := instance.Up(context.Background(), engine.NewPodman(s), settings(), unhealthy, &bytes.Buffer{})
	if err == nil || !strings.Contains(err.Error(), "older or a newer") {
		t.Errorf("err %v", err)
	}
	if !strings.Contains(strings.Join(s.Calls, "\n"), "podman stop yawble") {
		t.Errorf("the crash loop must be stopped:\n%s", strings.Join(s.Calls, "\n"))
	}
}

func TestUninstallRemovesOnlyWhatExistsAndSaysWhatItRemoved(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman version", engine.Result{Stdout: "6.0.2\n"})
	s.On(tunnelInspect, engine.Result{Stderr: "no such container", ExitCode: 125})
	s.On(inspect, engine.Result{Stdout: "exited|" + img + "|\n"})
	s.On("podman pod exists", engine.Result{})
	s.On("podman image exists", engine.Result{ExitCode: 1})
	s.On("podman volume exists", engine.Result{})
	var out bytes.Buffer
	if err := instance.Uninstall(context.Background(), engine.NewPodman(s), img, false, &out); err != nil {
		t.Fatal(err)
	}
	c := strings.Join(s.Calls, "\n")
	if strings.Contains(c, "rm -f yawble-tunnel") || strings.Contains(c, "rmi") || strings.Contains(c, "volume rm") {
		t.Errorf("removed things that were not there:\n%s", c)
	}
	if !strings.Contains(c, "podman rm -f yawble\n") || !strings.Contains(c, "podman pod rm -f yawble") {
		t.Errorf("did not remove what was there:\n%s", c)
	}
	if strings.Contains(out.String(), "removed tunnel") || !strings.Contains(out.String(), "removed container yawble") {
		t.Errorf("out %q", out.String())
	}
}

// An engine that cannot be asked is a refusal, not a silent no-op with exit 0.
func TestUninstallRefusesWhenTheEngineCannotBeAsked(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman version", engine.Result{Stderr: "Cannot connect to Podman", ExitCode: 125})
	err := instance.Uninstall(context.Background(), engine.NewPodman(s), img, true, &bytes.Buffer{})
	if err == nil || !strings.Contains(err.Error(), "Cannot connect") {
		t.Errorf("err %v", err)
	}
	for _, c := range s.Calls {
		if strings.Contains(c, " rm") {
			t.Errorf("removed something with the engine down: %q", c)
		}
	}
}

func TestDownStopsARunningContainerAndLeavesTheRest(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman container inspect", engine.Result{Stdout: "running|" + img + "|" + label(settings()) + "\n"})
	var out bytes.Buffer
	if err := instance.Down(context.Background(), engine.NewPodman(s), 8080, &out); err != nil {
		t.Fatal(err)
	}
	joined := strings.Join(s.Calls, "\n")
	// "--format" contains the letters rm, so the check names the verbs that would remove things.
	if !strings.Contains(joined, "podman stop yawble") || strings.Contains(joined, "podman rm") || strings.Contains(joined, "pod rm") || strings.Contains(joined, "volume rm") {
		t.Errorf("calls:\n%s", joined)
	}
	if !strings.Contains(out.String(), "kept") {
		t.Errorf("down should say the data volume is kept: %q", out.String())
	}
}

// Important 3 from the review: status reports what RUNS, not what the config says.
func TestStatusReportsTheRunningImageAndPortNotTheConfigs(t *testing.T) {
	running := settings()
	running.Port = 8080
	running.Image = "localhost/old:1"
	wanted := settings()
	wanted.Port = 9090
	s := engine.NewScripted()
	s.On("podman version", engine.Result{Stdout: "5.4.0\n"})
	s.On("podman container inspect", engine.Result{Stdout: "running|localhost/old:1|" + label(running) + "\n"})
	st, err := instance.GetStatus(context.Background(), engine.NewPodman(s), wanted, healthy)
	if err != nil {
		t.Fatal(err)
	}
	if st.Image != "localhost/old:1" || st.URL != "http://127.0.0.1:8080" || st.Container != engine.StateRunning {
		t.Errorf("got %+v", st)
	}
	if st.Pending == nil || !strings.Contains(strings.Join(st.Pending, " "), "port") {
		t.Errorf("status should say a port change is pending: %+v", st)
	}
}

func TestDefaultsDeriveFromTheMachineAndCap(t *testing.T) {
	m := instance.Machine{MemoryBytes: 64 << 30, CPUs: 20, Measured: true}
	got, notes := instance.Defaults(config.Config{}, m, img)
	if got.Memory != "12288m" || got.CPUs != 8 || got.MaxRunning != 8 || got.Port != 8080 || got.Image != img || len(notes) != 0 {
		t.Errorf("got %+v notes %v", got, notes)
	}
	small := instance.Machine{MemoryBytes: 8 << 30, CPUs: 4, Measured: true}
	got, _ = instance.Defaults(config.Config{CPUs: 2}, small, img)
	if got.Memory != "4096m" || got.CPUs != 2 || got.MaxRunning != 2 {
		t.Errorf("got %+v", got)
	}
	// Important 9 from the review: a 1 GB board must not get "0g".
	tiny := instance.Machine{MemoryBytes: 1 << 30, CPUs: 4, Measured: true}
	got, _ = instance.Defaults(config.Config{}, tiny, img)
	if got.Memory != "512m" {
		t.Errorf("tiny machine: got %+v", got)
	}
	got, notes = instance.Defaults(config.Config{Image: "x:1"}, instance.Machine{}, img)
	if got.Memory != "8192m" || got.CPUs != 4 || got.Image != "x:1" || len(notes) != 2 {
		t.Errorf("unmeasured machine: got %+v notes %v", got, notes)
	}
}

// A pod made before the Windows fix is remade: removing it takes the container with it, and the
// container is created again on the same volume, which is never touched.
func TestUpRemakesAPodThatIsNotCurrentKeepingTheVolume(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman pod inspect", engine.Result{Stdout: "\n"})
	// Removing the pod removed the container in it, so by the time it is inspected it is absent.
	s.On("podman container inspect", engine.Result{Stderr: "no such container", ExitCode: 125})
	var out bytes.Buffer
	if err := instance.Up(context.Background(), engine.NewFor("podman", s, "windows"), settings(), healthy, &out); err != nil {
		t.Fatal(err)
	}
	c := strings.Join(s.Calls, "\n")
	rm, create, run := strings.Index(c, "podman pod rm -f yawble"), strings.Index(c, "podman pod create"), strings.Index(c, "podman run")
	if rm < 0 || create < rm || run < create {
		t.Errorf("calls:\n%s", c)
	}
	if strings.Contains(c, "volume rm") {
		t.Errorf("the volume must be kept:\n%s", c)
	}
	if !strings.Contains(out.String(), "localhost") {
		t.Errorf("say why the pod was remade: %q", out.String())
	}
}

// firstStartLog is what a first start writes, noise included: the Host's JSON request lines and
// apt's own output, between the lines a person waiting wants.
const firstStartLog = `volume: ownership set: 36 change(s) (0 means the volume already matched)
Get:1 http://archive.ubuntu.com/ubuntu noble InRelease [256 kB]
agent cli: installing claude
{"Timestamp":"t","LogLevel":"Information","Category":"Microsoft.AspNetCore.Hosting.Diagnostics","Message":"Request finished HTTP/1.1 GET /healthz - 200"}
{"Timestamp":"t","LogLevel":"Information","Category":"Harness.Host","Message":"Agent launch: separate user - agent children run as 'agent'"}
agent cli: claude is installed
`

// A slow start does not send the person to a second window with "`yawble logs -f` shows
// progress". Past the note, `up` shows that progress itself, and only the lines that mean something.
func TestASlowStartShowsItsProgressInPlaceOfTheLogsCommand(t *testing.T) {
	defer instance.SetPollingForTests(5*time.Millisecond, 5*time.Second)()
	defer instance.SetNoteAtForTests(0)()
	s := engine.NewScripted()
	s.On("podman container inspect", engine.Result{Stdout: "exited|" + img + "|" + label(settings()) + "\n"})
	s.On("podman logs --follow --since", engine.Result{Stdout: firstStartLog})
	s.On(inspect+"-tunnel", engine.Result{Stderr: "no such container", ExitCode: 125})
	polls := 0
	slow := func(string) bool { polls++; return polls > 3 }
	var out bytes.Buffer
	if err := instance.Up(context.Background(), engine.NewPodman(s), settings(), slow, &out); err != nil {
		t.Fatal(err)
	}
	o := out.String()
	for _, want := range []string{"  volume: ownership set: 36 change(s)", "  agent cli: installing claude", "  Agent launch: separate user", "  agent cli: claude is installed"} {
		if !strings.Contains(o, want) {
			t.Errorf("missing %q in:\n%s", want, o)
		}
	}
	for _, noise := range []string{"Request finished", "Get:1", "yawble logs -f"} {
		if strings.Contains(o, noise) {
			t.Errorf("%q should not be shown:\n%s", noise, o)
		}
	}
	if !strings.HasSuffix(strings.TrimSpace(o), "Yawble is up at http://127.0.0.1:8080") {
		t.Errorf("the last line is the URL:\n%s", o)
	}
}

// A start that answers before the note shows none of it: a restart is not a first start.
func TestAQuickStartShowsNoProgress(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman container inspect", engine.Result{Stdout: "exited|" + img + "|" + label(settings()) + "\n"})
	s.On("podman logs --follow --since", engine.Result{Stdout: firstStartLog})
	var out bytes.Buffer
	if err := instance.Up(context.Background(), engine.NewPodman(s), settings(), healthy, &out); err != nil {
		t.Fatal(err)
	}
	if strings.Contains(out.String(), "agent cli") || strings.Contains(out.String(), "volume:") {
		t.Errorf("a quick start printed progress:\n%s", out.String())
	}
}
