package cli_test

import (
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/engine"
)

// The unattended first up names whose share of this computer it took half of, and where to give
// that engine more, as the size screen does.
func TestUnattendedFirstUpSaysWhereToGiveTheEngineMore(t *testing.T) {
	s := engine.NewScripted()
	s.On("docker info", engine.Result{Stdout: "12884901888|10|Docker Desktop\n"})
	s.On("docker container inspect", engine.Result{Stderr: "Error: No such container: yawble", ExitCode: 1})
	s.On("docker image inspect", engine.Result{})
	deps := stubbed(s)
	deps.GOOS, deps.LookPath = "darwin", lookPath("docker")
	deps.ConfigDir = t.TempDir()
	code, out, errOut := run(t, deps, "up", "--yes", "--no-browser")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if !strings.Contains(out, "Docker Desktop's share of this computer") || !strings.Contains(out, dockerDesktopMore) {
		t.Errorf("--yes should name the share and where to give it more:\n%s", out)
	}
}

// A refused answer names the engine as whose share of this computer it is, never "the engine".
func TestTheSizeRefusalsNameTheEnginesShareOfThisComputer(t *testing.T) {
	s := upScript()
	s.On("podman machine inspect", engine.Result{Stdout: "podman-machine-default|running|false|8192|4\n"})
	out := refusalsOf(t, s, "darwin", "podman", "9000\n\n6\n\n")
	for _, want := range []string{
		"refused: 9000 MB, with control's 1536 MB, is more than the Podman machine's share of this computer; the most is 6656 MB. ",
		"refused: 6 CPUs is more than the Podman machine's share of this computer; the most is 4. ",
	} {
		if !strings.Contains(out, want) {
			t.Errorf("out lacks %q:\n%s", want, out)
		}
	}
	if strings.Contains(out, "the engine has") {
		t.Errorf("a refusal says \"the engine has\":\n%s", out)
	}
}
