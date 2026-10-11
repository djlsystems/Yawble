package cli_test

import (
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/engine"
)

// podmanHoldsNothing scripts a Podman that answers and has no Yawble container, pod, image or
// volume: every `exists` exits 1 and the container inspect says "no such container".
func podmanHoldsNothing(s *engine.Scripted) {
	s.On("podman container inspect", engine.Result{ExitCode: 125, Stderr: "Error: no such container yawble"})
	s.On("podman pod exists", engine.Result{ExitCode: 1})
	s.On("podman image exists", engine.Result{ExitCode: 1})
	s.On("podman volume exists", engine.Result{ExitCode: 1})
}

// dockerHoldsNothing is the same for Docker, whose inspect exits 1 with "No such ...".
func dockerHoldsNothing(s *engine.Scripted) {
	missing := engine.Result{ExitCode: 1, Stderr: "Error: No such object: yawble"}
	s.On("docker container inspect", missing)
	s.On("docker network inspect", missing)
	s.On("docker image inspect", missing)
	s.On("docker volume inspect", missing)
}

// dockerHoldsTheInstance is Docker with the container, its network, the image and the volume.
func dockerHoldsTheInstance(s *engine.Scripted) {
	s.On("docker container inspect --format", engine.Result{Stdout: "running|" + testImage + "|" + currentLabel() + "\n"})
	s.On("docker container inspect --format {{.State.Status}}|{{.Config.Image}}|{{index .Config.Labels \"yawble.settings\"}} yawble-tunnel", engine.Result{ExitCode: 1, Stderr: "Error: No such container: yawble-tunnel"})
}

// The reported case: the instance and its volume are on Docker, Podman is installed too, and no
// engine is saved (an earlier uninstall removed the settings). uninstall --data --yes asked only
// Podman, found nothing and said nothing; the volume survived. Every installed engine is asked.
func TestUninstallDataFindsTheInstanceOnDockerWhenPodmanIsAlsoInstalled(t *testing.T) {
	s := engine.NewScripted()
	podmanHoldsNothing(s)
	dockerHoldsTheInstance(s)
	deps := stubbed(s)
	deps.LookPath = lookPath("podman", "docker")

	code, out, errOut := run(t, deps, "uninstall", "--data", "--yes")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	c := calls(s)
	for _, want := range []string{"docker rm -f yawble", "docker network rm yawble", "docker rmi " + testImage, "docker volume rm yawble-data"} {
		if !strings.Contains(c, want) {
			t.Errorf("missing %q in:\n%s", want, c)
		}
	}
	for _, want := range []string{
		"removed container yawble (docker)",
		"removed image " + testImage + " (docker)",
		"removed volume yawble-data (docker) and everything on it",
		"podman: no Yawble container, pod, image or volume",
	} {
		if !strings.Contains(out, want) {
			t.Errorf("out lacks %q:\n%s", want, out)
		}
	}
	if strings.Contains(c, "podman rm ") || strings.Contains(c, "podman volume rm") {
		t.Errorf("removed from Podman, which holds nothing:\n%s", c)
	}
}

// An engine that is not installed is not asked.
func TestUninstallDoesNotAskAnEngineThatIsNotInstalled(t *testing.T) {
	s := engine.NewScripted()
	dockerHoldsTheInstance(s)
	deps := stubbed(s)
	deps.LookPath = lookPath("docker")
	if code, out, errOut := run(t, deps, "uninstall", "--yes"); code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if c := calls(s); strings.Contains(c, "podman") {
		t.Errorf("asked Podman, which is not installed:\n%s", c)
	}
}

// Nothing anywhere: said per engine, and with --data that no data volume was found.
func TestUninstallSaysPerEngineWhenNothingIsFound(t *testing.T) {
	s := engine.NewScripted()
	podmanHoldsNothing(s)
	dockerHoldsNothing(s)
	deps := stubbed(s)
	deps.LookPath = lookPath("podman", "docker")

	code, out, errOut := run(t, deps, "uninstall", "--data", "--yes")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	for _, want := range []string{
		"podman: no Yawble container, pod, image or volume",
		"docker: no Yawble container, network, image or volume",
		"no data volume yawble-data was found on podman or docker",
	} {
		if !strings.Contains(out, want) {
			t.Errorf("out lacks %q:\n%s", want, out)
		}
	}
	if strings.Contains(out, "removed volume") || strings.Contains(calls(s), "rm ") {
		t.Errorf("claimed or attempted a removal of nothing:\nout %s\ncalls %s", out, calls(s))
	}
}

// One engine cannot answer: it is named, the other is still cleaned, and the exit is not clean.
func TestUninstallNamesAnEngineThatCannotAnswerAndStillCleansTheOther(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman version", engine.Result{ExitCode: 125, Stderr: "Cannot connect to Podman. Is the podman machine running?"})
	dockerHoldsTheInstance(s)
	deps := stubbed(s)
	deps.LookPath = lookPath("podman", "docker")

	code, out, errOut := run(t, deps, "uninstall", "--data", "--yes")
	if code == 0 {
		t.Fatalf("a clean exit with an engine that could not be asked: %s %s", out, errOut)
	}
	if !strings.Contains(errOut, "podman") || !strings.Contains(errOut, "Cannot connect to Podman") {
		t.Errorf("the failing engine is not named: %q", errOut)
	}
	if !strings.Contains(out, "removed volume yawble-data (docker) and everything on it") || !strings.Contains(calls(s), "docker volume rm yawble-data") {
		t.Errorf("Docker was not cleaned:\nout %s\ncalls %s", out, calls(s))
	}
	if strings.Contains(out, "podman: no Yawble") {
		t.Errorf("claimed Podman holds nothing when it could not be asked: %s", out)
	}
}

// A stopped Podman machine that cannot be started is that engine's named failure; Docker is still
// cleaned and the exit is not clean.
func TestUninstallAPodmanMachineThatWillNotStartIsNamedAndDockerIsStillCleaned(t *testing.T) {
	s := engine.NewScripted()
	s.On("wsl --status", engine.Result{})
	s.On(machineInspect, engine.Result{Stdout: "podman-machine-default|stopped|false|2048|10\n"})
	s.On("podman machine start", engine.Result{ExitCode: 125, Stderr: "Error: unable to start the machine"})
	dockerHoldsTheInstance(s)
	deps := stubbed(s)
	deps.GOOS = "windows"
	deps.LookPath = lookPath("podman", "docker")

	code, out, errOut := run(t, deps, "uninstall", "--data", "--yes")
	if code == 0 {
		t.Fatalf("a clean exit with a machine that would not start: %s %s", out, errOut)
	}
	if !strings.Contains(errOut, "podman") || !strings.Contains(errOut, "unable to start the machine") {
		t.Errorf("the machine failure is not named: %q", errOut)
	}
	if !strings.Contains(calls(s), "docker volume rm yawble-data") {
		t.Errorf("Docker was not cleaned:\n%s", calls(s))
	}
	if strings.Contains(calls(s), "podman rm") {
		t.Errorf("asked Podman to remove behind a stopped machine:\n%s", calls(s))
	}
}

// A fresh install (no saved settings, no container) on an engine that already holds the data
// volume takes over every team and login on it: `up` says so, dated, and how to remove it. With
// saved settings it is the person's own instance and nothing is said.
func TestUpOnAFreshInstallSaysItReusesTheExistingDataVolume(t *testing.T) {
	const want = "using the existing data volume yawble-data (created 2026-09-30): its teams and logins are kept; `yawble uninstall --data` removes it"
	for _, saved := range []bool{false, true} {
		s := engine.NewScripted()
		s.OnSequence("docker container inspect --format {{.State.Status}}|{{.Config.Image}}|{{index .Config.Labels \"yawble.settings\"}} yawble",
			engine.Result{Stderr: "Error: No such container: yawble", ExitCode: 1},
			engine.Result{Stderr: "Error: No such container: yawble", ExitCode: 1},
			engine.Result{Stdout: "running|" + testImage + "|" + currentLabel() + "\n"})
		s.On("docker volume inspect --format {{.CreatedAt}} yawble-data", engine.Result{Stdout: "2026-09-30T08:12:44Z\n"})
		deps := stubbed(s)
		deps.LookPath = lookPath("docker")
		deps.ConfigDir = t.TempDir()
		if saved {
			if err := os.WriteFile(filepath.Join(deps.ConfigDir, "config.toml"), []byte("engine = \"docker\"\n"), 0o600); err != nil {
				t.Fatal(err)
			}
		}
		code, out, errOut := run(t, deps, "up", "--no-browser")
		if code != 0 {
			t.Fatalf("saved %v: exit %d: %s %s", saved, code, out, errOut)
		}
		if got := strings.Contains(out, want); got == saved {
			t.Errorf("saved %v: reuse note shown %v, want %v:\n%s", saved, got, !saved, out)
		}
	}
}

// The other engine's data volume is named too: it is not this instance's, and a person who
// believes it gone is told where it is.
func TestUpOnAFreshInstallNamesADataVolumeOnTheOtherEngine(t *testing.T) {
	s := engine.NewScripted()
	s.OnSequence("podman container inspect --format {{.State.Status}}|{{.ImageName}}|{{index .Config.Labels \"yawble.settings\"}} yawble",
		engine.Result{Stderr: "Error: no such container yawble", ExitCode: 125},
		engine.Result{Stderr: "Error: no such container yawble", ExitCode: 125},
		engine.Result{Stdout: "running|" + testImage + "|" + currentLabel() + "\n"})
	s.OnSequence("podman volume exists yawble-data", engine.Result{ExitCode: 1}, engine.Result{ExitCode: 1}, engine.Result{})
	s.On("docker volume inspect --format {{.CreatedAt}} yawble-data", engine.Result{Stdout: "2026-09-01T10:00:00Z\n"})
	deps := stubbed(s)
	deps.LookPath = lookPath("podman", "docker")

	code, out, errOut := run(t, deps, "up", "--yes", "--no-browser")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if !strings.Contains(out, "docker also holds a data volume yawble-data (created 2026-09-01), which this instance on podman does not use; `docker volume rm yawble-data` removes it") {
		t.Errorf("the other engine's volume is not named:\n%s", out)
	}
	if strings.Contains(out, "using the existing data volume") {
		t.Errorf("claimed to reuse a volume Podman does not hold:\n%s", out)
	}
	if strings.Contains(calls(s), "docker volume rm") {
		t.Errorf("up removed the other engine's volume:\n%s", calls(s))
	}
}

// doctor lists a data volume on the engine it is not checking.
func TestDoctorListsADataVolumeOnTheOtherEngine(t *testing.T) {
	s := runningScript()
	s.On("docker volume inspect --format {{.CreatedAt}} yawble-data", engine.Result{Stdout: "2026-09-01T10:00:00Z\n"})
	deps := stubbed(s)
	deps.LookPath = lookPath("podman", "docker")
	deps.ConfigDir = t.TempDir()
	if err := os.WriteFile(filepath.Join(deps.ConfigDir, "config.toml"), []byte("engine = \"podman\"\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	code, out, errOut := run(t, deps, "doctor")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	for _, want := range []string{"data volume on docker", "docker also holds a data volume yawble-data (created 2026-09-01), which this instance on podman does not use", "docker volume rm yawble-data"} {
		if !strings.Contains(out, want) {
			t.Errorf("doctor lacks %q:\n%s", want, out)
		}
	}

	// Docker without the volume: no line.
	s = runningScript()
	s.On("docker volume inspect yawble-data", engine.Result{Stderr: "Error: No such volume: yawble-data", ExitCode: 1})
	deps.Runner = s
	if _, out, _ := run(t, deps, "doctor"); strings.Contains(out, "data volume on docker") {
		t.Errorf("named a volume Docker does not hold:\n%s", out)
	}
}
