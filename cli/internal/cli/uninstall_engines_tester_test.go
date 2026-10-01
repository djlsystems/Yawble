package cli_test

import (
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/engine"
)

// Tester's check: Podman cannot answer and Docker holds nothing. The "no data volume was found"
// line names only the engine that answered, never Podman, and the exit is not clean.
func TestUninstallDataNeverSaysNoVolumeOnAnEngineThatCouldNotBeAsked(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman version", engine.Result{ExitCode: 125, Stderr: "Cannot connect to Podman"})
	dockerHoldsNothing(s)
	deps := stubbed(s)
	deps.LookPath = lookPath("podman", "docker")

	code, out, errOut := run(t, deps, "uninstall", "--data", "--yes")
	if code == 0 {
		t.Fatalf("clean exit with Podman unanswered: %s %s", out, errOut)
	}
	if !strings.Contains(out, "no data volume yawble-data was found on docker\n") {
		t.Errorf("Docker's empty answer not said:\n%s", out)
	}
	if strings.Contains(out, "found on podman") || strings.Contains(out, "podman: no Yawble") || strings.Contains(out, "removed volume") {
		t.Errorf("claimed something about Podman, which could not be asked:\n%s", out)
	}
	if !strings.Contains(errOut, "podman cannot be asked") {
		t.Errorf("Podman's failure not named: %q", errOut)
	}
}

// Tester's check: --data without --yes and without a terminal removes no volume on any engine.
func TestUninstallDataWithoutTheWordRemovesNoVolumeOnEitherEngine(t *testing.T) {
	s := engine.NewScripted()
	podmanHoldsNothing(s)
	dockerHoldsTheInstance(s)
	deps := stubbed(s)
	deps.LookPath = lookPath("podman", "docker")
	deps.Interactive = false
	if code, _, _ := run(t, deps, "uninstall", "--data"); code == 0 {
		t.Errorf("uninstall --data with no terminal and no --yes exited clean")
	}
	if c := calls(s); strings.Contains(c, "volume rm") || strings.Contains(c, "rm -f") {
		t.Errorf("removed without confirmation:\n%s", c)
	}
}
