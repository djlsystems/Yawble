package cli_test

import (
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/engine"
)

// A first-up value above the engine's share of this computer is refused with how to give that kind of engine
// more, and asked again. The wording is pinned here per engine kind.

const (
	podmanMachineMore = "To give the Podman machine more: podman machine stop, then podman machine set --memory <MB> --cpus <n>, then podman machine start"
	dockerDesktopMore = "To give Docker Desktop more: Docker Desktop's Settings > Resources"
	linuxMore         = "On Linux there is no VM: this computer's own RAM and CPUs are the limit, and there is nothing to enlarge"
)

func refusalsOf(t *testing.T, s *engine.Scripted, goos, engineName string, stdin string) string {
	t.Helper()
	deps := stubbed(s)
	deps.GOOS, deps.LookPath = goos, lookPath(engineName)
	deps.ConfigDir = t.TempDir()
	deps.Interactive, deps.Stdin = true, strings.NewReader(stdin)
	code, out, errOut := run(t, deps, "up", "--no-browser")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	return out
}

func TestFirstUpRefusalOnAPodmanMachineSaysHowToGiveTheMachineMore(t *testing.T) {
	s := upScript()
	s.On("podman machine inspect", engine.Result{Stdout: "podman-machine-default|running|false|8192|4\n"})
	out := refusalsOf(t, s, "darwin", "podman", "9000\n\n6\n\n")
	for _, want := range []string{
		"refused: 9000 MB, with control's 1536 MB, is more than the Podman machine's share of this computer; the most is 6656 MB. " + podmanMachineMore + "\n",
		"refused: 6 CPUs is more than the Podman machine's share of this computer; the most is 4. " + podmanMachineMore + "\n",
	} {
		if !strings.Contains(out, want) {
			t.Errorf("out lacks %q:\n%s", want, out)
		}
	}
	if n := strings.Count(out, "Memory in MB (4096 to 6656)"); n != 2 {
		t.Errorf("memory asked %d times after a refusal, want 2:\n%s", n, out)
	}
}

func TestFirstUpRefusalOnDockerDesktopPointsAtItsResourcesSettings(t *testing.T) {
	for _, goos := range []string{"darwin", "windows", "linux"} {
		s := engine.NewScripted()
		s.On("docker info", engine.Result{Stdout: "12884901888|10|Docker Desktop\n"})
		s.On("docker container inspect", engine.Result{Stderr: "Error: No such container: yawble", ExitCode: 1})
		s.On("docker image inspect", engine.Result{})
		out := refusalsOf(t, s, goos, "docker", "20000\n\n12\n\n")
		for _, want := range []string{
			"refused: 20000 MB, with control's 1536 MB, is more than Docker Desktop's share of this computer; the most is 10752 MB. " + dockerDesktopMore + "\n",
			"refused: 12 CPUs is more than Docker Desktop's share of this computer; the most is 10. " + dockerDesktopMore + "\n",
		} {
			if !strings.Contains(out, want) {
				t.Errorf("%s: out lacks %q:\n%s", goos, want, out)
			}
		}
	}
}

func TestFirstUpRefusalOnLinuxSaysThereIsNothingToEnlarge(t *testing.T) {
	s := engine.NewScripted()
	s.On("docker info", engine.Result{Stdout: "12884901888|10|Ubuntu 24.04 LTS\n"})
	s.On("docker container inspect", engine.Result{Stderr: "Error: No such container: yawble", ExitCode: 1})
	s.On("docker image inspect", engine.Result{})
	out := refusalsOf(t, s, "linux", "docker", "20000\n\n12\n\n")
	for _, want := range []string{
		"refused: 20000 MB, with control's 1536 MB, is more than what this computer has; the most is 10752 MB. " + linuxMore + "\n",
		"refused: 12 CPUs is more than what this computer has; the most is 10. " + linuxMore + "\n",
	} {
		if !strings.Contains(out, want) {
			t.Errorf("out lacks %q:\n%s", want, out)
		}
	}
}

func TestDoctorSaysHowToGiveTheEngineMoreWhenASavedValueIsOverIt(t *testing.T) {
	s := engine.NewScripted()
	s.On("docker version", engine.Result{Stdout: "29.8.0\n"})
	s.On("docker info", engine.Result{Stdout: "12884901888|10|Docker Desktop\n"})
	s.On("docker container inspect", engine.Result{Stdout: "running|" + testImage + "|\n"})
	s.On("docker exec -e HARNESS_WORKER_KEY= yawble dotnet /app/Harness.Host.dll --doctor", engine.Result{Stdout: withWip(`{"mechanism":"none","perRunMb":null,"detail":"not set"}`)})
	deps := stubbed(s)
	deps.GOOS, deps.LookPath = "darwin", lookPath("docker")
	deps.ConfigDir = t.TempDir()
	writeConfig(t, deps.ConfigDir, "engine = \"docker\"\nmemory = \"16g\"\ncpus = 8\n")
	_, out, errOut := run(t, deps, "doctor")
	want := "yawble config set memory 12288m (or less), then yawble up; or to give Docker Desktop more: Docker Desktop's Settings > Resources"
	if !strings.Contains(out, want) {
		t.Errorf("doctor lacks %q:\n%s %s", want, out, errOut)
	}
}

func TestALaterUpWarnsAboutAnOverLargeSavedValueWithTheSameHint(t *testing.T) {
	s := upScript()
	s.On("podman machine inspect", engine.Result{Stdout: "podman-machine-default|running|false|8192|4\n"})
	deps := stubbed(s)
	deps.GOOS, deps.LookPath = "darwin", lookPath("podman")
	deps.ConfigDir = t.TempDir()
	writeConfig(t, deps.ConfigDir, "memory = \"6g\"\ncpus = 6\n")
	code, out, errOut := run(t, deps, "up", "--no-browser")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	want := "warning: cpus 6 in yawble's config is more than the Podman machine's share of this computer (4 CPUs); the container cannot get them: yawble config set cpus 4 or less, then yawble up; or to give the Podman machine more: podman machine stop, then podman machine set --memory <MB> --cpus <n>, then podman machine start"
	if !strings.Contains(errOut, want) {
		t.Errorf("stderr lacks %q:\n%s", want, errOut)
	}
}
