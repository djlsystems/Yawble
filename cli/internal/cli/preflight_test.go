package cli_test

import (
	"errors"
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/cli"
	"github.com/djlsystems/yawble/cli/internal/config"
	"github.com/djlsystems/yawble/cli/internal/engine"
)

// lookPath answers found for the names given and not found for everything else.
func lookPath(found ...string) func(string) (string, error) {
	return func(name string) (string, error) {
		for _, f := range found {
			if f == name {
				return "/usr/bin/" + name, nil
			}
		}
		return "", errors.New("not found")
	}
}

// upScript is an instance that needs nothing but a start: the preflight is what the tests watch.
func upScript() *engine.Scripted {
	s := engine.NewScripted()
	s.On("podman container inspect", engine.Result{Stdout: "exited|" + testImage + "|" + currentLabel() + "\n"})
	return s
}

func calls(s *engine.Scripted) string { return strings.Join(s.Calls, "\n") }

// Docker present and no Podman: Docker is the engine, nothing is installed, no machine is minded.
func TestUpWithDockerButNoPodmanUsesDockerAndInstallsNothing(t *testing.T) {
	s := engine.NewScripted()
	s.On("docker container inspect", engine.Result{Stdout: "exited|" + testImage + "|" + currentLabel() + "\n"})
	deps := stubbed(s)
	deps.LookPath = lookPath("docker", "apt-get")
	code, out, errOut := run(t, deps, "up")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	c := calls(s)
	if strings.Contains(c, "apt-get") || strings.Contains(c, "podman") || !strings.Contains(c, "docker start yawble") || !strings.Contains(out, "using Docker") {
		t.Errorf("out %q calls:\n%s", out, c)
	}
}

// Windows and macOS: the machine. Missing → created rootless and started (with a yes).
func TestUpOnWindowsWithNoMachineCreatesItRootlessAndStartsIt(t *testing.T) {
	s := upScript()
	s.On("wsl --status", engine.Result{})
	s.On("podman machine inspect", engine.Result{Stderr: "Error: podman-machine-default: VM does not exist", ExitCode: 125})
	deps := stubbed(s)
	deps.GOOS, deps.LookPath = "windows", lookPath("podman", "winget")
	code, out, errOut := run(t, deps, "up", "--yes")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	c := calls(s)
	init := strings.Index(c, "podman machine init --rootful=false")
	start := strings.Index(c, "podman machine start")
	if init < 0 || start < 0 || start < init {
		t.Errorf("calls:\n%s", c)
	}
}

func TestUpOnWindowsWithNoMachineAndNoYesAsksBeforeCreating(t *testing.T) {
	s := upScript()
	s.On("podman machine inspect", engine.Result{Stderr: "VM does not exist", ExitCode: 125})
	deps := stubbed(s)
	deps.GOOS, deps.LookPath, deps.Interactive = "windows", lookPath("podman"), false
	code, _, errOut := run(t, deps, "up")
	if code != 2 || !strings.Contains(errOut, "--yes") || strings.Contains(calls(s), "machine init") {
		t.Errorf("exit %d stderr %q calls %q", code, errOut, s.Calls)
	}
}

// Review Focus 2: a stopped machine is started without a question.
func TestUpOnAStoppedMachineStartsItWithoutAsking(t *testing.T) {
	s := upScript()
	s.On("podman machine inspect", engine.Result{Stdout: "podman-machine-default|stopped|false|8192|4\n"})
	deps := stubbed(s)
	deps.GOOS, deps.LookPath, deps.Interactive = "darwin", lookPath("podman"), false
	code, out, errOut := run(t, deps, "up")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if !strings.Contains(calls(s), "podman machine start") || strings.Contains(calls(s), "machine init") {
		t.Errorf("calls:\n%s", calls(s))
	}
}

// Review Focus 3: rootful is offered a fix; with --yes it is done (stop, set, start).
func TestUpOnARootfulMachineWithYesMakesItRootless(t *testing.T) {
	s := upScript()
	s.On("wsl --status", engine.Result{})
	s.On("podman machine inspect", engine.Result{Stdout: "podman-machine-default|running|true|2048|10\n"})
	s.On("wsl -d podman-machine-default -e free -m", engine.Result{Stdout: "Mem: 15688 1 1\n"})
	deps := stubbed(s)
	deps.GOOS, deps.LookPath = "windows", lookPath("podman")
	code, out, errOut := run(t, deps, "up", "--yes")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	c := calls(s)
	stop, set, start := strings.Index(c, "podman machine stop"), strings.Index(c, "podman machine set --rootful=false"), strings.Index(c, "podman machine start")
	if stop < 0 || set < stop || start < set {
		t.Errorf("calls:\n%s", c)
	}
}

func TestUpOnARootfulMachineDeclinedContinuesWithAWarning(t *testing.T) {
	s := upScript()
	s.On("wsl --status", engine.Result{})
	s.On("podman machine inspect", engine.Result{Stdout: "podman-machine-default|running|true|2048|10\n"})
	s.On("wsl -d podman-machine-default -e free -m", engine.Result{Stdout: "Mem: 15688 1 1\n"})
	deps := stubbed(s)
	deps.GOOS, deps.LookPath, deps.Interactive = "windows", lookPath("podman"), true
	deps.Stdin = strings.NewReader("n\n")
	code, out, errOut := run(t, deps, "up")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if strings.Contains(calls(s), "machine set") || !strings.Contains(errOut+out, "rootful") {
		t.Errorf("calls %q out %q err %q", s.Calls, out, errOut)
	}
}

// Review Focus 4: a small WSL VM is said once, and the start is not refused.
func TestUpOnASmallWSLMachineSaysHowToGiveItMemory(t *testing.T) {
	s := upScript()
	s.On("wsl --status", engine.Result{})
	s.On("podman machine inspect", engine.Result{Stdout: "podman-machine-default|running|false|2048|10\n"})
	s.On("wsl -d podman-machine-default -e free -m", engine.Result{Stdout: "Mem: 4096 1 1\n"})
	deps := stubbed(s)
	deps.GOOS, deps.LookPath = "windows", lookPath("podman")
	// The limit is pinned, so the verdict does not depend on the RAM of the machine running
	// this test (with the machine measured at 4096 MB, a derived limit would be 2048m).
	env := map[string]string{"YAWBLE_IMAGE": testImage, "YAWBLE_MEMORY": "8192m"}
	deps.Env = func(k string) string { return env[k] }
	code, _, errOut := run(t, deps, "up", "--yes")
	if code != 0 || !strings.Contains(errOut, ".wslconfig") || !strings.Contains(errOut, "wsl --shutdown") {
		t.Errorf("exit %d stderr %q", code, errOut)
	}
}

// On Windows, WSL missing is "print the wsl --install line and stop", before any init.
func TestUpOnWindowsWithoutWSLStopsNamingWslInstall(t *testing.T) {
	s := upScript()
	s.On("wsl --status", engine.Result{Stderr: "'wsl' is not recognized", ExitCode: 1})
	s.On("podman machine inspect", engine.Result{Stderr: "VM does not exist", ExitCode: 125})
	deps := stubbed(s)
	deps.GOOS, deps.LookPath = "windows", lookPath("podman")
	code, _, errOut := run(t, deps, "up", "--yes")
	// `wsl --install` refuses to run over a remote
	// session (PowerShell Direct, SSH), elevated or not, so the message says where to run it.
	if code != 1 || !strings.Contains(errOut, "wsl --install") || !strings.Contains(errOut, "not over a remote session") || strings.Contains(calls(s), "machine init") {
		t.Errorf("exit %d stderr %q calls %q", code, errOut, s.Calls)
	}
}

// Limits default from the machine's RAM. On macOS and Windows that is the Podman
// machine, not the host, and it is known only once the machine runs.
func TestUpOnMacDerivesMemoryAndCPUsFromTheRunningMachine(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman machine inspect", engine.Result{Stdout: "podman-machine-default|running|false|8192|4\n"})
	s.On("podman container inspect", engine.Result{Stderr: "no such container", ExitCode: 125})
	s.On("podman image exists", engine.Result{})
	deps := stubbed(s)
	deps.GOOS, deps.LookPath = "darwin", lookPath("podman")
	code, _, errOut := run(t, deps, "up")
	if code != 0 {
		t.Fatalf("exit %d: %s", code, errOut)
	}
	c := calls(s)
	if !strings.Contains(c, "--memory 4096m --cpus 4") {
		t.Errorf("the run should use half the machine's 8192 MB and its 4 CPUs:\n%s", c)
	}
	if strings.Contains(errOut, "could not be measured") {
		t.Errorf("nothing was unmeasured: %q", errOut)
	}
}

func TestUpOnMacCreatesTheMachineWithHalfTheHostsMemoryCapped(t *testing.T) {
	s := upScript()
	s.OnSequence("podman machine inspect",
		engine.Result{Stderr: "VM does not exist", ExitCode: 125},
		engine.Result{Stdout: "podman-machine-default|running|false|12288|8\n"},
	)
	s.On("sysctl -n hw.memsize", engine.Result{Stdout: "68719476736\n"}) // 64 GB
	deps := stubbed(s)
	deps.GOOS, deps.LookPath = "darwin", lookPath("podman")
	code, _, errOut := run(t, deps, "up", "--yes")
	if code != 0 {
		t.Fatalf("exit %d: %s", code, errOut)
	}
	if !strings.Contains(calls(s), "podman machine init --rootful=false --memory 12288") {
		t.Errorf("calls:\n%s", calls(s))
	}
}

func TestUpOnLinuxNeverAsksAboutAMachine(t *testing.T) {
	s := upScript()
	deps := stubbed(s)
	deps.LookPath = lookPath("podman")
	if code, _, errOut := run(t, deps, "up"); code != 0 {
		t.Fatalf("exit %d %s", code, errOut)
	}
	if strings.Contains(calls(s), "podman machine") {
		t.Errorf("calls:\n%s", calls(s))
	}
}

var _ = cli.Deps{}

// Measured in the same VM: with no memory configured, up warned that the machine's 5924 MB was
// under "the container limit" of 8192 MB (the unmeasured default), then started the container
// with half the machine, 2962 MB. A limit derived from the machine always fits it; only one a
// person set can be too big.
func TestUpDoesNotWarnAboutALimitItWillNotUse(t *testing.T) {
	s := engine.NewScripted()
	s.On("wsl --status", engine.Result{})
	s.On("podman machine inspect", engine.Result{Stdout: "podman-machine-default|running|false|2048|2\n"})
	s.On("wsl -d podman-machine-default -e free -m", engine.Result{Stdout: "Mem: 5924 1 1\n"})
	s.On("podman container inspect", engine.Result{Stderr: "no such container", ExitCode: 125})
	s.On("podman image exists", engine.Result{})
	deps := stubbed(s)
	deps.GOOS, deps.LookPath = "windows", lookPath("podman")
	code, _, errOut := run(t, deps, "up", "--yes")
	if code != 0 {
		t.Fatalf("exit %d: %s", code, errOut)
	}
	if strings.Contains(errOut, "memory-starved") || strings.Contains(errOut, "8192") {
		t.Errorf("warned about a limit it did not use: %q", errOut)
	}
	if !strings.Contains(calls(s), "--memory 2962m") {
		t.Errorf("calls:\n%s", calls(s))
	}
}

// Apple silicon only on macOS: an Intel Mac is told so before anything runs.
func TestUpOnAnIntelMacIsRefusedBeforeAnythingRuns(t *testing.T) {
	s := upScript()
	deps := stubbed(s)
	deps.GOOS, deps.GOARCH, deps.LookPath = "darwin", "amd64", lookPath()
	code, _, errOut := run(t, deps, "up", "--yes")
	if code != 1 || !strings.Contains(errOut, "Apple silicon") || len(s.Calls) != 0 {
		t.Errorf("exit %d stderr %q calls %q", code, errOut, s.Calls)
	}
}

// yawble installs no container engine. It uses what is there - Podman
// or Docker, asking when both are - and when neither is, it says where to get one, Podman first
// and recommended, and to run `yawble up` afterwards. Nothing is installed or run.
func TestUpWithNeitherEngineStopsWithLinksPodmanFirst(t *testing.T) {
	for goos, links := range map[string][]string{
		"windows": {"https://podman-desktop.io", "https://www.docker.com/products/docker-desktop"},
		"darwin":  {"https://podman-desktop.io", "https://www.docker.com/products/docker-desktop"},
		"linux":   {"https://podman.io/docs/installation", "https://docs.docker.com/engine/install"},
	} {
		s := upScript()
		deps := stubbed(s)
		deps.GOOS, deps.LookPath = goos, lookPath("winget", "brew", "apt-get")
		code, _, errOut := run(t, deps, "up", "--yes")
		if code != 1 || len(s.Calls) != 0 {
			t.Errorf("%s: exit %d calls %q", goos, code, s.Calls)
		}
		podman, docker := strings.Index(errOut, links[0]), strings.Index(errOut, links[1])
		if podman < 0 || docker < 0 || docker < podman || !strings.Contains(errOut, "recommended") || !strings.Contains(errOut, "yawble up") {
			t.Errorf("%s: stderr %q", goos, errOut)
		}
	}
}

// Only Docker: it is used, its limits come from `docker info`, and Podman and WSL are never asked.
func TestUpWithOnlyDockerUsesDockerAndItsOwnLimits(t *testing.T) {
	s := engine.NewScripted()
	s.On("docker info", engine.Result{Stdout: "8589934592|4\n"}) // 8 GB, 4 CPUs
	s.On("docker container inspect", engine.Result{Stderr: "Error: No such container: yawble", ExitCode: 1})
	s.On("docker image inspect", engine.Result{})
	deps := stubbed(s)
	deps.GOOS, deps.LookPath = "windows", lookPath("docker")
	code, out, errOut := run(t, deps, "up", "--yes")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	c := calls(s)
	if strings.Contains(c, "podman") || strings.Contains(c, "wsl") {
		t.Errorf("Podman or WSL was asked:\n%s", c)
	}
	if !strings.Contains(c, "--memory 4096m --cpus 4") || !strings.Contains(out, "Docker") {
		t.Errorf("out %q calls:\n%s", out, c)
	}
}

// Both: the person chooses, and the choice is kept in yawble's config for every later command.
func TestUpWithBothEnginesAsksAndRemembersTheChoice(t *testing.T) {
	for answer, want := range map[string]string{"2\n": "docker", "\n": "podman", "1\n": "podman"} {
		s := engine.NewScripted()
		s.On("docker info", engine.Result{Stdout: "8589934592|4\n"})
		s.On(engine.GitHubRegistry, engine.Result{})
		deps := stubbed(s)
		deps.LookPath = lookPath("podman", "docker")
		deps.Interactive, deps.Stdin = true, strings.NewReader(answer)
		deps.ConfigDir = t.TempDir()
		code, out, errOut := run(t, deps, "up", "--no-browser")
		if code != 0 {
			t.Fatalf("answer %q: exit %d: %s %s", answer, code, out, errOut)
		}
		if !strings.Contains(out, "Podman (recommended)") {
			t.Errorf("answer %q: the question %q", answer, out)
		}
		saved, _, err := config.Load(deps.ConfigDir, func(string) string { return "" })
		if err != nil || saved.Engine != want {
			t.Errorf("answer %q: saved engine %q err %v, want %q", answer, saved.Engine, err, want)
		}
		if saved.Image != "" {
			t.Errorf("answer %q: only the engine is saved, not the environment's image %q", answer, saved.Image)
		}
		other := map[string]string{"docker": "podman", "podman": "docker"}[want]
		if strings.Contains(calls(s), other+" run") || strings.Contains(calls(s), other+" start") {
			t.Errorf("answer %q: %s was used:\n%s", answer, other, calls(s))
		}
	}
}

func TestUpWithBothEnginesAndYesPicksPodman(t *testing.T) {
	s := upScript()
	deps := stubbed(s)
	deps.LookPath = lookPath("podman", "docker")
	if code, out, errOut := run(t, deps, "up", "--yes"); code != 0 || strings.Contains(calls(s), "docker ") {
		t.Errorf("exit %d out %q err %q calls %q", code, out, errOut, s.Calls)
	}
}

func TestUpWithBothEnginesAndNoTerminalRefusesNamingTheSetting(t *testing.T) {
	s := upScript()
	deps := stubbed(s)
	deps.LookPath, deps.Interactive = lookPath("podman", "docker"), false
	code, _, errOut := run(t, deps, "up")
	if code != 2 || !strings.Contains(errOut, "yawble config set engine") || len(s.Calls) != 0 {
		t.Errorf("exit %d stderr %q calls %q", code, errOut, s.Calls)
	}
}

// A configured engine that is not installed is said as such, with the way out; nothing else runs.
func TestUpWithAConfiguredEngineThatIsNotInstalledSaysSo(t *testing.T) {
	s := upScript()
	deps := stubbed(s)
	env := map[string]string{"YAWBLE_IMAGE": testImage, "YAWBLE_ENGINE": "docker"}
	deps.Env = func(k string) string { return env[k] }
	deps.LookPath = lookPath("podman")
	code, _, errOut := run(t, deps, "up", "--yes")
	if code != 1 || !strings.Contains(errOut, "docker") || !strings.Contains(errOut, "yawble config set engine podman") || len(s.Calls) != 0 {
		t.Errorf("exit %d stderr %q calls %q", code, errOut, s.Calls)
	}
}
