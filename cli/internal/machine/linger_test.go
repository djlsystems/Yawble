package machine_test

import (
	"context"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/machine"
)

func TestLingerIsAskedInsideTheMachineOrOnThisComputer(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman machine ssh podman-machine-default id -un", engine.Result{Stdout: "user\n"})
	s.On("podman machine ssh podman-machine-default loginctl show-user user --property=Linger --value", engine.Result{Stdout: "no\n"})
	user, linger, ok := machine.Linger(context.Background(), s, "podman-machine-default")
	if user != "user" || linger || !ok {
		t.Errorf("user %q linger %v ok %v", user, linger, ok)
	}

	s = engine.NewScripted()
	s.On("id -un", engine.Result{Stdout: "agent\n"})
	s.On("loginctl show-user agent --property=Linger --value", engine.Result{Stdout: "yes\n"})
	if user, linger, ok := machine.Linger(context.Background(), s, ""); user != "agent" || !linger || !ok {
		t.Errorf("user %q linger %v ok %v", user, linger, ok)
	}

	// Unreadable is not measured: no fix is named from it.
	s = engine.NewScripted()
	s.On("id -un", engine.Result{Stdout: "agent\n"})
	s.On("loginctl", engine.Result{Stderr: "Failed to connect to bus", ExitCode: 1})
	if _, _, ok := machine.Linger(context.Background(), s, ""); ok {
		t.Error("a loginctl that failed was read as measured")
	}
}
