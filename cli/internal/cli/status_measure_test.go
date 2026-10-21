package cli_test

import (
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/engine"
)

// ONLY up, doctor AND restore NEED WHAT THE ENGINE HAS. On Windows with Podman, measuring is four
// processes (wsl --status, podman machine inspect, free, nproc); status spawns none of them.
func TestStatusMeasuresNoEngineCapacity(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman version", engine.Result{Stdout: "1.0\n"})
	s.On("podman container inspect", engine.Result{Stdout: "exited|" + testImage + "|\n"})
	deps := stubbed(s)
	deps.GOOS = "windows"

	code, out, _ := run(t, deps, "status")

	if code != 0 {
		t.Fatalf("exit %d\n%s", code, out)
	}
	for _, call := range s.Calls {
		if strings.Contains(call, "machine inspect") || strings.HasPrefix(call, "wsl ") || strings.Contains(call, "docker info") {
			t.Errorf("status measured the engine: %q", call)
		}
	}
}
