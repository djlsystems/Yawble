package cli_test

import (
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/engine"
)

// Independent probes of the first-up sizing: odd answers, engine answers missing a field, the
// engine kind's hint per OS, and a saved value exactly at the engine's figure.

func dockerEngine(info string) *engine.Scripted {
	s := engine.NewScripted()
	s.On("docker info", engine.Result{Stdout: info})
	s.On("docker container inspect", engine.Result{Stderr: "Error: No such container: yawble", ExitCode: 1})
	s.On("docker image inspect", engine.Result{})
	return s
}

func TestProbeOddAnswersAreRefusedOrReadAsSizes(t *testing.T) {
	s := dockerEngine("12884901888|10|Docker Desktop\n")
	out := refusalsOf(t, s, "darwin", "docker", "-5\nabc\n8.5g\n8 GB\n-1\n4.5\nfour\n 3 \n")
	for _, want := range []string{
		`refused: "-5" is not a whole number`,
		`refused: "abc" is not a whole number`,
		`refused: "8.5g" is not a whole number`,
		`refused: "-1" is not a whole number`,
		`refused: "4.5" is not a whole number`,
		`refused: "four" is not a whole number`,
		"saved memory 8192m and cpus 3",
	} {
		if !strings.Contains(out, want) {
			t.Errorf("out lacks %q:\n%s", want, out)
		}
	}
}

func TestProbeUnitsAtThePrompt(t *testing.T) {
	for in, want := range map[string]string{"8G\n\n": "saved memory 8192m", "8192m\n\n": "saved memory 8192m", "10g\n\n": "saved memory 10240m", "13g\n\n": "refused: 13312 MB, with control's 1536 MB, is more than Docker Desktop's share of this computer"} {
		out := refusalsOf(t, dockerEngine("12884901888|10|Docker Desktop\n"), "darwin", "docker", in)
		if !strings.Contains(out, want) {
			t.Errorf("%q: out lacks %q:\n%s", in, want, out)
		}
	}
}

func TestProbeDockerInfoMissingAFieldIsNotMeasured(t *testing.T) {
	for _, info := range []string{"|10|Docker Desktop\n", "12884901888||Docker Desktop\n", "<no value>|10\n", "12884901888\n", "\n", "0|10|x\n"} {
		s := dockerEngine(info)
		deps := stubbed(s)
		deps.GOOS, deps.LookPath = "darwin", lookPath("docker")
		deps.ConfigDir = t.TempDir()
		deps.Interactive, deps.Stdin = true, strings.NewReader("\n\n")
		code, out, errOut := run(t, deps, "up", "--no-browser")
		if code != 0 {
			t.Fatalf("%q: exit %d: %s %s", info, code, out, errOut)
		}
		if strings.Contains(out, "How much of the engine") {
			t.Errorf("%q: asked with an unmeasured engine:\n%s", info, out)
		}
		if !strings.Contains(errOut, "memory: not measured") || !strings.Contains(errOut, "cpus: not measured") {
			t.Errorf("%q: up did not say not measured: %s", info, errOut)
		}
		if saved := savedConfig(t, deps.ConfigDir); saved.Memory != "" || saved.CPUs != 0 {
			t.Errorf("%q: saved %+v", info, saved)
		}
	}
}

func TestProbeEngineKindHintPerOS(t *testing.T) {
	for _, c := range []struct{ goos, os, want string }{
		{"linux", "Ubuntu 24.04 LTS", linuxMore},
		{"linux", "", linuxMore},
		{"linux", "Docker Desktop", dockerDesktopMore},
		{"windows", "Docker Desktop", dockerDesktopMore},
		{"darwin", "", dockerDesktopMore},
		{"darwin", "Alpine Linux v3.20", "To give Docker's VM more: change it in the tool that runs that VM"},
	} {
		out := refusalsOf(t, dockerEngine("12884901888|10|"+c.os+"\n"), c.goos, "docker", "20000\n\n\n")
		if !strings.Contains(out, "the most is 10752 MB. "+c.want+"\n") {
			t.Errorf("%s/%q: out lacks %q:\n%s", c.goos, c.os, c.want, out)
		}
	}
}

// One worker at the engine's whole memory leaves control's allowance on top: that is warned, never
// refused, so an instance sized to the engine before control and workers still comes up.
func TestProbeASavedValueEqualToTheEnginesWarnsOnlyThatControlComesOnTop(t *testing.T) {
	_, deps := dockerUp("darwin")
	deps.ConfigDir = t.TempDir()
	writeConfig(t, deps.ConfigDir, "memory = \"12g\"\ncpus = 10\n")
	code, out, errOut := run(t, deps, "up", "--no-browser")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if strings.Count(errOut, "warning:") != 1 || !strings.Contains(errOut, "warning: 1 worker(s) × 12288 MB + control's 1536 MB = 13824 MB, more than the 12288 MB the engine has") {
		t.Errorf("want the one bound warning at the engine's own figure: %s", errOut)
	}
	if strings.Contains(out, "How much") {
		t.Errorf("asked:\n%s", out)
	}
}

func TestProbeTheMaxIsAcceptedAtThePrompt(t *testing.T) {
	out := refusalsOf(t, dockerEngine("12884901888|10|Docker Desktop\n"), "darwin", "docker", "10752\n10\n")
	if !strings.Contains(out, "saved memory 10752m and cpus 10") {
		t.Errorf("max refused:\n%s", out)
	}
}

// An engine with 6 GB proposes 3072 MB (half), but the floor is 4096 MB: Enter must not save a
// value the prompt itself would refuse when typed.
func TestProbeTheProposalIsNeverBelowTheFloorThePromptStates(t *testing.T) {
	for _, mem := range []string{"6442450944", "4294967296", "8053063680"} {
		out := refusalsOf(t, dockerEngine(mem+"|4|Docker Desktop\n"), "darwin", "docker", "\n\n")
		var prompt, saved string
		for _, l := range strings.Split(out, "\n") {
			if i := strings.Index(l, "Memory in MB ("); i >= 0 {
				prompt = l[i:]
			}
			if i := strings.Index(l, "saved memory "); i >= 0 {
				saved = l[i:]
			}
		}
		t.Logf("%s: %s | %s", mem, prompt, saved)
		var lo, hi, def int
		if _, err := fmtSscanf(prompt, &lo, &hi, &def); err != nil {
			t.Fatalf("prompt %q: %v", prompt, err)
		}
		if def < lo || def > hi {
			t.Errorf("engine %s bytes: the proposal %d is outside the stated range %d to %d, and Enter saves it: %s", mem, def, lo, hi, saved)
		}
	}
}

func TestProbeConfigSetMemoryAndCpusStillDriveALaterUp(t *testing.T) {
	s, deps := dockerUp("darwin")
	deps.ConfigDir = t.TempDir()
	for _, args := range [][]string{{"config", "set", "memory", "10G"}, {"config", "set", "cpus", "3"}} {
		if code, out, errOut := run(t, deps, args...); code != 0 {
			t.Fatalf("%v: exit %d: %s %s", args, code, out, errOut)
		}
	}
	deps.Interactive, deps.Stdin = true, strings.NewReader("\n\n")
	code, out, errOut := run(t, deps, "up", "--no-browser")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if strings.Contains(out, "How much") || strings.Contains(errOut, "warning:") {
		t.Errorf("asked or warned:\n%s\n%s", out, errOut)
	}
	if !strings.Contains(calls(s), "--memory 10G --cpus 3") && !strings.Contains(calls(s), "--memory 10g --cpus 3") {
		t.Errorf("calls:\n%s", calls(s))
	}
}

func TestProbeDoctorAtTheEnginesFigureIsInfoAndOverItGivesTheEngineHint(t *testing.T) {
	for _, c := range []struct{ goos, os, saved, verdict, want string }{
		{"darwin", "Docker Desktop", "engine = \"docker\"\nmemory = \"12288m\"\ncpus = 10\n", "info ", "engine has 12288 MB, 10 CPUs (docker info)"},
		{"linux", "Ubuntu 24.04 LTS", "engine = \"docker\"\nmemory = \"16g\"\ncpus = 4\n", "warn ", "; or on Linux there is no VM: this computer's own RAM and CPUs are the limit, and there is nothing to enlarge"},
	} {
		s := engine.NewScripted()
		s.On("docker version", engine.Result{Stdout: "29.8.0\n"})
		s.On("docker info", engine.Result{Stdout: "12884901888|10|" + c.os + "\n"})
		s.On("docker container inspect", engine.Result{Stdout: "running|" + testImage + "|\n"})
		s.On("docker exec -e HARNESS_WORKER_KEY= yawble dotnet /app/Harness.Host.dll --doctor", engine.Result{Stdout: withWip(`{"mechanism":"none","perRunMb":null,"detail":"not set"}`)})
		deps := stubbed(s)
		deps.GOOS, deps.LookPath = c.goos, lookPath("docker")
		deps.ConfigDir = t.TempDir()
		writeConfig(t, deps.ConfigDir, c.saved)
		_, out, _ := run(t, deps, "doctor", "--details")
		var rows []string
		for _, l := range strings.Split(out, "\n") {
			if strings.Contains(l, "capacity") {
				rows = append(rows, l)
			}
		}
		row := strings.Join(rows, "\n")
		idx := strings.Index(out, "capacity")
		tail := ""
		if idx >= 0 {
			tail = out[idx:]
			if j := strings.Index(tail, "\nok "); j > 0 {
				tail = tail[:j]
			}
		}
		if !strings.HasPrefix(strings.TrimSpace(row), strings.TrimSpace(c.verdict)) || !strings.Contains(tail, c.want) {
			t.Errorf("%s/%s: capacity row %q lacks %q / verdict %q:\n%s", c.goos, c.os, row, c.want, c.verdict, out)
		}
	}
}
