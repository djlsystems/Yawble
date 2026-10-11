package cli_test

import (
	"fmt"
	"strconv"
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/engine"
)

// Re-check probes of the first-up floor: across small, boundary and large engines the default is
// inside the range the prompt states, the floor it states is the floor it enforces, and the
// unattended paths save the same in-range proposal; a small engine's saved proposal is not
// warned about by a later up or by doctor.

// recheckEngines are engine memory figures in bytes with their CPUs: small, odd, at and around
// the 8 GB boundary, and large.
var recheckEngines = []struct {
	bytes int64
	cpus  int
}{
	{1024 << 20, 1}, {2048 << 20, 1}, {3000 << 20, 2}, {4096 << 20, 2}, {6144 << 20, 4},
	{6442450943, 4}, {8191 << 20, 4}, {8192 << 20, 4}, {8193 << 20, 4}, {16384 << 20, 8},
	{32768 << 20, 16},
}

// lineWith is the text from the last prompt-shaped occurrence of marker (followed by a digit).
func lineWith(out, marker string) string {
	for i := strings.LastIndex(out, marker); i >= 0; i = strings.LastIndex(out[:i], marker) {
		rest := out[i:]
		if len(rest) > len(marker) && rest[len(marker)] >= '0' && rest[len(marker)] <= '9' {
			return rest
		}
	}
	return ""
}

func TestProbeRecheckDefaultInsideStatedRangeOnEveryPath(t *testing.T) {
	for _, e := range recheckEngines {
		info := fmt.Sprintf("%d|%d|Docker Desktop\n", e.bytes, e.cpus)
		name := fmt.Sprintf("%d bytes/%d CPUs", e.bytes, e.cpus)

		s := engine.NewScripted()
		s.On("docker info", engine.Result{Stdout: info})
		s.On("docker container inspect", engine.Result{Stderr: "Error: No such container: yawble", ExitCode: 1})
		s.On("docker image inspect", engine.Result{})
		out := refusalsOf(t, s, "darwin", "docker", "\n\n")
		var lo, hi, def, clo, chi, cdef int
		if _, err := fmtSscanf(lineWith(out, "Memory in MB ("), &lo, &hi, &def); err != nil {
			t.Fatalf("%s: memory prompt: %v\n%s", name, err, out)
		}
		if _, err := fmt.Sscanf(lineWith(out, "CPUs ("), "CPUs (%d to %d) [%d]:", &clo, &chi, &cdef); err != nil {
			t.Fatalf("%s: cpu prompt: %v\n%s", name, err, out)
		}
		if def < lo || def > hi || cdef < clo || cdef > chi {
			t.Errorf("%s: default outside range: memory %d in %d..%d, cpus %d in %d..%d", name, def, lo, hi, cdef, clo, chi)
		}
		if strings.Contains(out, "refused") {
			t.Errorf("%s: Enter refused:\n%s", name, out)
		}
		wantSaved := fmt.Sprintf("saved memory %dm and cpus %d", def, cdef)
		if !strings.Contains(out, wantSaved) {
			t.Errorf("%s: Enter did not save the default %q:\n%s", name, wantSaved, out)
		}
		wantFloor := 4096
		if def < 4096 {
			wantFloor = def
		}
		if lo != wantFloor {
			t.Errorf("%s: stated floor %d, want %d (proposal %d)", name, lo, wantFloor, def)
		}

		// The floor stated is the floor enforced: one below is refused naming it, the floor itself is accepted.
		if lo > 1 {
			s2 := engine.NewScripted()
			s2.On("docker info", engine.Result{Stdout: info})
			s2.On("docker container inspect", engine.Result{Stderr: "Error: No such container: yawble", ExitCode: 1})
			s2.On("docker image inspect", engine.Result{})
			out2 := refusalsOf(t, s2, "darwin", "docker", strconv.Itoa(lo-1)+"\n"+strconv.Itoa(lo)+"\n\n")
			refusal := fmt.Sprintf("refused: %d MB is below the floor of %d MB", lo-1, lo)
			if !strings.Contains(out2, refusal) || !strings.Contains(out2, fmt.Sprintf("the least is %d MB", lo)) {
				t.Errorf("%s: one below the stated floor not refused naming it:\n%s", name, out2)
			}
			if lo < 4096 && !strings.Contains(out2, "the proposal for an engine under 8 GB") {
				t.Errorf("%s: lowered floor refusal does not say why:\n%s", name, out2)
			}
			if lo == 4096 && !strings.Contains(out2, "that leaves Yawble a usable share") {
				t.Errorf("%s: 4 GB floor refusal wording changed:\n%s", name, out2)
			}
			if !strings.Contains(out2, fmt.Sprintf("saved memory %dm", lo)) {
				t.Errorf("%s: the stated floor itself was not accepted:\n%s", name, out2)
			}
		}

		// --yes and no terminal save the same in-range proposal without asking.
		for mode, args := range map[string][]string{"--yes": {"up", "--yes", "--no-browser"}, "no terminal": {"up", "--no-browser"}} {
			s3 := engine.NewScripted()
			s3.On("docker info", engine.Result{Stdout: info})
			s3.On("docker container inspect", engine.Result{Stderr: "Error: No such container: yawble", ExitCode: 1})
			s3.On("docker image inspect", engine.Result{})
			deps := stubbed(s3)
			deps.GOOS, deps.LookPath = "darwin", lookPath("docker")
			deps.ConfigDir = t.TempDir()
			deps.Interactive = mode == "--yes"
			deps.Stdin = strings.NewReader("1\n1\n")
			code, o, errOut := run(t, deps, args...)
			if code != 0 {
				t.Fatalf("%s %s: exit %d: %s %s", name, mode, code, o, errOut)
			}
			if strings.Contains(o, "Memory in MB") {
				t.Errorf("%s %s: asked:\n%s", name, mode, o)
			}
			if saved := savedConfig(t, deps.ConfigDir); saved.Memory != fmt.Sprintf("%dm", def) || saved.CPUs != cdef {
				t.Errorf("%s %s: saved %+v, want %dm/%d (the interactive default)", name, mode, saved, def, cdef)
			}
		}
	}
}

func TestProbeRecheckASmallEnginesSavedProposalIsNotWarnedByLaterUpOrDoctor(t *testing.T) {
	for _, e := range []struct{ mb, cpus, proposal int }{{4096, 2, 2048}, {6144, 4, 3072}, {2048, 1, 512}} {
		info := fmt.Sprintf("%d|%d|Docker Desktop\n", e.mb<<20, e.cpus)
		saved := fmt.Sprintf("engine = \"docker\"\nmemory = \"%dm\"\ncpus = %d\n", e.proposal, e.cpus)

		s := engine.NewScripted()
		s.On("docker info", engine.Result{Stdout: info})
		s.On("docker container inspect", engine.Result{Stderr: "Error: No such container: yawble", ExitCode: 1})
		s.On("docker image inspect", engine.Result{})
		deps := stubbed(s)
		deps.GOOS, deps.LookPath = "darwin", lookPath("docker")
		deps.ConfigDir = t.TempDir()
		writeConfig(t, deps.ConfigDir, saved)
		deps.Interactive, deps.Stdin = true, strings.NewReader("\n\n")
		code, out, errOut := run(t, deps, "up", "--no-browser")
		if code != 0 {
			t.Fatalf("%d MB: exit %d: %s %s", e.mb, code, out, errOut)
		}
		if strings.Contains(errOut, "warning:") || strings.Contains(out, "How much") {
			t.Errorf("%d MB: later up warned or asked about the saved proposal:\n%s\n%s", e.mb, out, errOut)
		}

		d := engine.NewScripted()
		d.On("docker version", engine.Result{Stdout: "29.8.0\n"})
		d.On("docker info", engine.Result{Stdout: info})
		d.On("docker container inspect", engine.Result{Stdout: "running|" + testImage + "|\n"})
		d.On("docker exec -e HARNESS_WORKER_KEY= yawble dotnet /app/Harness.Host.dll --doctor", engine.Result{Stdout: withWip(`{"mechanism":"none","perRunMb":null,"detail":"not set"}`)})
		dd := stubbed(d)
		dd.GOOS, dd.LookPath = "darwin", lookPath("docker")
		dd.ConfigDir = t.TempDir()
		writeConfig(t, dd.ConfigDir, saved)
		_, dout, _ := run(t, dd, "doctor", "--details")
		var row string
		for _, l := range strings.Split(dout, "\n") {
			if strings.Contains(l, "capacity") {
				row = l
			}
		}
		if !strings.HasPrefix(row, "info ") || strings.Contains(dout, "Docker Desktop's Settings") {
			t.Errorf("%d MB: doctor flagged the saved proposal: %q\n%s", e.mb, row, dout)
		}
	}
}
