package cli_test

import (
	"os"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/cli"
	"github.com/djlsystems/yawble/cli/internal/config"
	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/instance"
)

// The first `up` shows what the engine has, proposes the container's memory and CPUs from it, and
// lets the person change them up to what the engine has; the answer is saved and later ups keep it.

const dockerInfo = "docker info --format {{.MemTotal}}|{{.NCPU}}"

// dockerUp is a fresh Docker instance whose engine has 12 GB and 10 CPUs.
func dockerUp(goos string) (*engine.Scripted, cli.Deps) {
	s := engine.NewScripted()
	s.On("docker info", engine.Result{Stdout: "12884901888|10\n"})
	s.On("docker container inspect", engine.Result{Stderr: "Error: No such container: yawble", ExitCode: 1})
	s.On("docker image inspect", engine.Result{})
	deps := stubbed(s)
	deps.GOOS, deps.LookPath = goos, lookPath("docker")
	return s, deps
}

func savedConfig(t *testing.T, dir string) config.Config {
	t.Helper()
	c, _, err := config.Load(dir, func(string) string { return "" })
	if err != nil {
		t.Fatal(err)
	}
	return c
}

func TestDockerIsMeasuredWithDockerInfoOnEveryOS(t *testing.T) {
	for _, goos := range []string{"linux", "darwin", "windows"} {
		s, deps := dockerUp(goos)
		deps.ConfigDir = t.TempDir()
		code, out, errOut := run(t, deps, "up", "--yes")
		if code != 0 {
			t.Fatalf("%s: exit %d: %s %s", goos, code, out, errOut)
		}
		c := calls(s)
		if !strings.Contains(c, dockerInfo) {
			t.Errorf("%s: docker info was not asked:\n%s", goos, c)
		}
		// Half of 12288 MB, and 10 CPUs capped at 8.
		if !strings.Contains(c, "--memory 6144m --cpus 8") {
			t.Errorf("%s: the run should get half the engine's memory and 8 of its CPUs:\n%s", goos, c)
		}
		if strings.Contains(errOut, "not measured") {
			t.Errorf("%s: the engine answered, yet up said not measured: %q", goos, errOut)
		}
	}
}

func TestAnEngineThatCannotAnswerIsNotMeasuredAndUpSaysSo(t *testing.T) {
	s := engine.NewScripted()
	s.On("docker info", engine.Result{Stderr: "Cannot connect to the Docker daemon", ExitCode: 1})
	s.On("docker container inspect", engine.Result{Stderr: "Error: No such container: yawble", ExitCode: 1})
	deps := stubbed(s)
	deps.GOOS, deps.LookPath = "darwin", lookPath("docker")
	deps.ConfigDir = t.TempDir()
	code, out, errOut := run(t, deps, "up", "--yes")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if !strings.Contains(errOut, "memory: not measured") || !strings.Contains(errOut, "cpus: not measured") {
		t.Errorf("up did not say the engine was not measured: %q", errOut)
	}
	if saved := savedConfig(t, deps.ConfigDir); saved.Memory != "" || saved.CPUs != 0 {
		t.Errorf("an unmeasured fallback was saved as a choice: %+v", saved)
	}
	if !strings.Contains(calls(s), "--memory 8192m --cpus 4") {
		t.Errorf("calls:\n%s", calls(s))
	}
}

func TestFirstUpShowsTheEngineAndTheProposalAndEnterAcceptsAndSaves(t *testing.T) {
	s, deps := dockerUp("darwin")
	deps.ConfigDir = t.TempDir()
	deps.Interactive, deps.Stdin = true, strings.NewReader("\n\n")
	code, out, errOut := run(t, deps, "up", "--no-browser")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	perRun := strconv.Itoa(instance.HostDefaultMemoryPerRunMb)
	for _, want := range []string{
		"the engine has   12288 MB memory, 10 CPUs (docker info)",
		"proposed         6144 MB memory, 8 CPUs",
		// min(8 - 1, 6144 / 2048) = 3, labelled as the Host's rule with its default allowance.
		"running limit    3 at once, derived from the Host's rule: the smaller of CPUs - 1 and memory / wip.memoryPerRunMb (the Host's default, " + perRun + " MB). Not asked",
		"Memory in MB (4096 to 12288) [6144]: ",
		"CPUs (1 to 10) [8]: ",
		"saved memory 6144m and cpus 8 in yawble's config",
	} {
		if !strings.Contains(out, want) {
			t.Errorf("the screen lacks %q:\n%s", want, out)
		}
	}
	if saved := savedConfig(t, deps.ConfigDir); saved.Memory != "6144m" || saved.CPUs != 8 {
		t.Errorf("saved %+v", saved)
	}
	if !strings.Contains(calls(s), "--memory 6144m --cpus 8") {
		t.Errorf("calls:\n%s", calls(s))
	}
}

func TestFirstUpAcceptsAValidChangeAndSavesIt(t *testing.T) {
	s, deps := dockerUp("windows")
	deps.ConfigDir = t.TempDir()
	deps.Interactive, deps.Stdin = true, strings.NewReader("8g\n4\n")
	code, out, errOut := run(t, deps, "up", "--no-browser")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if saved := savedConfig(t, deps.ConfigDir); saved.Memory != "8192m" || saved.CPUs != 4 {
		t.Errorf("saved %+v", saved)
	}
	if !strings.Contains(calls(s), "--memory 8192m --cpus 4") {
		t.Errorf("the run did not use the answers:\n%s", calls(s))
	}
	if !strings.Contains(out, "derives a running limit of 3 from them") {
		t.Errorf("out %s", out)
	}
}

func TestFirstUpRefusesOverTheMaximumAndUnderTheFloorNamingTheBound(t *testing.T) {
	_, deps := dockerUp("linux")
	deps.ConfigDir = t.TempDir()
	deps.Interactive, deps.Stdin = true, strings.NewReader("20000\n1000\nlots\n8192\n12\n0\n6\n")
	code, out, errOut := run(t, deps, "up", "--no-browser")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	for _, want := range []string{
		"refused: 20000 MB is more than the engine has; the most is 12288 MB",
		"refused: 1000 MB is below the floor of 4096 MB that leaves the Host a usable share; the least is 4096 MB",
		`refused: "lots" is not a whole number`,
		"refused: 12 CPUs is more than the engine has; the most is 10",
		"refused: the least is 1 CPU",
	} {
		if !strings.Contains(out, want) {
			t.Errorf("out lacks %q:\n%s", want, out)
		}
	}
	// Each refusal asks again.
	if n := strings.Count(out, "Memory in MB (4096 to 12288)"); n != 4 {
		t.Errorf("memory asked %d times, want 4:\n%s", n, out)
	}
	if saved := savedConfig(t, deps.ConfigDir); saved.Memory != "8192m" || saved.CPUs != 6 {
		t.Errorf("saved %+v", saved)
	}
}

func TestUnattendedFirstUpTakesTheDefaultsWithoutAsking(t *testing.T) {
	for name, set := range map[string]func(*cli.Deps) []string{
		"--yes": func(d *cli.Deps) []string {
			d.Interactive, d.Stdin = true, strings.NewReader("9000\n2\n")
			return []string{"up", "--yes", "--no-browser"}
		},
		"no terminal": func(d *cli.Deps) []string {
			d.Interactive, d.Stdin = false, strings.NewReader("9000\n2\n")
			return []string{"up"}
		},
	} {
		s, deps := dockerUp("darwin")
		deps.ConfigDir = t.TempDir()
		code, out, errOut := run(t, deps, set(&deps)...)
		if code != 0 {
			t.Fatalf("%s: exit %d: %s %s", name, code, out, errOut)
		}
		if strings.Contains(out, "How much of the engine") || strings.Contains(out, "Memory in MB") {
			t.Errorf("%s: asked:\n%s", name, out)
		}
		if !strings.Contains(out, "memory 6144m and cpus 8 for the container: half of the 12288 MB and the 10 CPUs the engine has (docker info)") ||
			!strings.Contains(out, "yawble config set memory <size> and yawble config set cpus <n> change them") {
			t.Errorf("%s: did not say what it chose and how to change it:\n%s", name, out)
		}
		if saved := savedConfig(t, deps.ConfigDir); saved.Memory != "6144m" || saved.CPUs != 8 {
			t.Errorf("%s: saved %+v", name, saved)
		}
		if !strings.Contains(calls(s), "--memory 6144m --cpus 8") {
			t.Errorf("%s: calls:\n%s", name, calls(s))
		}
	}
}

func writeConfig(t *testing.T, dir, body string) {
	t.Helper()
	if err := os.WriteFile(filepath.Join(dir, config.FileName), []byte(body), 0o644); err != nil {
		t.Fatal(err)
	}
}

func TestLaterUpsDoNotAskAndWarnAboutASavedValueOverTheEngine(t *testing.T) {
	for _, c := range []struct {
		saved string
		warn  []string
	}{
		{"memory = \"8g\"\ncpus = 4\n", nil},
		{"memory = \"16g\"\ncpus = 12\n", []string{
			"warning: memory 16g in yawble's config is more than the engine has (12288 MB, docker info)",
			"yawble config set memory 12288m or less",
			"warning: cpus 12 in yawble's config is more than the engine has (10, docker info)",
		}},
	} {
		_, deps := dockerUp("darwin")
		deps.ConfigDir = t.TempDir()
		writeConfig(t, deps.ConfigDir, c.saved)
		deps.Interactive, deps.Stdin = true, strings.NewReader("\n\n")
		code, out, errOut := run(t, deps, "up", "--no-browser")
		if code != 0 {
			t.Fatalf("exit %d: %s %s", code, out, errOut)
		}
		if strings.Contains(out, "How much of the engine") || strings.Contains(out, "saved memory") {
			t.Errorf("%q: a later up asked:\n%s", c.saved, out)
		}
		for _, w := range c.warn {
			if !strings.Contains(errOut, w) {
				t.Errorf("%q: stderr lacks %q: %s", c.saved, w, errOut)
			}
		}
		if c.warn == nil && strings.Contains(errOut, "warning:") {
			t.Errorf("%q: warned about a value the engine has: %s", c.saved, errOut)
		}
	}
}

// The Host's rule on the first-up screen uses the Host's own default per-run allowance, read
// here from the Host's source so the two cannot drift.
func TestTheHostsDefaultMemoryPerRunIsTheHostsOwn(t *testing.T) {
	src, err := os.ReadFile(filepath.Join("..", "..", "..", "src", "Harness.Host", "TenantSettings.cs"))
	if err != nil {
		t.Fatal(err)
	}
	m := regexp.MustCompile(`const int DefaultMemoryPerRunMb = (\d+);`).FindSubmatch(src)
	if m == nil {
		t.Fatal("TenantSettings.cs no longer declares DefaultMemoryPerRunMb")
	}
	if got, _ := strconv.Atoi(string(m[1])); got != instance.HostDefaultMemoryPerRunMb {
		t.Errorf("the Host's default is %d MB, the CLI's copy %d MB", got, instance.HostDefaultMemoryPerRunMb)
	}
}

func TestDoctorShowsTheEnginesCapacityBesideTheContainers(t *testing.T) {
	for _, c := range []struct{ saved, want, verdict string }{
		{"engine = \"docker\"\nmemory = \"8g\"\ncpus = 8\n", "engine has 12288 MB, 10 CPUs (docker info); the container got 8192 MB, 8 CPUs (the Host's cgroup reading)", "info "},
		{"engine = \"docker\"\nmemory = \"8g\"\ncpus = 12\n", "yawble's config asks for cpus 12, more than the engine has", "warn "},
	} {
		s := engine.NewScripted()
		s.On("docker version", engine.Result{Stdout: "29.8.0\n"})
		s.On("docker info", engine.Result{Stdout: "12884901888|10\n"})
		s.On("docker container inspect", engine.Result{Stdout: "running|" + testImage + "|\n"})
		s.On("docker exec yawble dotnet /app/Harness.Host.dll --doctor", engine.Result{Stdout: withWip(`{"mechanism":"none","perRunMb":null,"detail":"not set"}`)})
		deps := stubbed(s)
		deps.GOOS, deps.LookPath = "darwin", lookPath("docker")
		deps.ConfigDir = t.TempDir()
		writeConfig(t, deps.ConfigDir, c.saved)
		_, out, errOut := run(t, deps, "doctor")
		var row string
		for _, line := range strings.Split(out, "\n") {
			if strings.Contains(line, "capacity") {
				row = line
			}
		}
		if !strings.Contains(row, c.want) || !strings.HasPrefix(row, c.verdict) {
			t.Errorf("capacity row %q, want %s%q\n%s %s", row, c.verdict, c.want, out, errOut)
		}
	}
}
