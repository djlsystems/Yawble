package engine_test

import (
	"bytes"
	"context"
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/engine"
)

const secret = "ghp_secretvalue123"

// The token reaches the engine on stdin. A command line is visible to every process on the
// machine (ps, /proc/<pid>/cmdline), so it must never carry the token.
func TestLoginPassesThePasswordOnStdinNeverOnTheCommandLine(t *testing.T) {
	for _, name := range []string{"podman", "docker"} {
		s := engine.NewScripted()
		e := engine.New(name, s)
		if err := e.Login(context.Background(), "ghcr.io", "x-access-token", secret); err != nil {
			t.Fatalf("%s: %v", name, err)
		}
		want := name + " login ghcr.io -u x-access-token --password-stdin"
		if len(s.Calls) != 1 || s.Calls[0] != want {
			t.Errorf("%s calls %q, want %q", name, s.Calls, want)
		}
		if len(s.Inputs) != 1 || s.Inputs[0] != secret {
			t.Errorf("%s stdin %q", name, s.Inputs)
		}
		for _, c := range s.Calls {
			if strings.Contains(c, secret) {
				t.Errorf("%s: the token is on a command line: %q", name, c)
			}
		}
	}
}

func TestAFailedLoginIsAnErrorThatDoesNotRepeatTheToken(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman login", engine.Result{Stderr: "Error: logging into \"ghcr.io\": invalid username/password", ExitCode: 125})
	err := engine.NewPodman(s).Login(context.Background(), "ghcr.io", "x-access-token", secret)
	if err == nil || !strings.Contains(err.Error(), "invalid username/password") {
		t.Fatalf("err %v", err)
	}
	if strings.Contains(err.Error(), secret) {
		t.Errorf("the error carries the token: %v", err)
	}
}

func TestWithATokenAGhcrPullLogsInOnceFirst(t *testing.T) {
	s := engine.NewScripted()
	e := engine.WithRegistryToken(engine.NewPodman(s), secret)
	var out bytes.Buffer
	for range 2 {
		if err := e.Pull(context.Background(), "ghcr.io/djlsystems/yawble:2026.09.25.1", &out); err != nil {
			t.Fatal(err)
		}
	}
	want := []string{
		"podman login ghcr.io -u x-access-token --password-stdin",
		"podman pull ghcr.io/djlsystems/yawble:2026.09.25.1",
		"podman pull ghcr.io/djlsystems/yawble:2026.09.25.1",
	}
	if strings.Join(s.Calls, "\n") != strings.Join(want, "\n") {
		t.Errorf("calls:\n%s\nwant:\n%s", strings.Join(s.Calls, "\n"), strings.Join(want, "\n"))
	}
}

// The token is a GitHub credential. Another registry (a tunnel sidecar on docker.io) never sees it.
func TestWithATokenAPullFromAnotherRegistryDoesNotLogIn(t *testing.T) {
	s := engine.NewScripted()
	e := engine.WithRegistryToken(engine.NewPodman(s), secret)
	for _, ref := range []string{"docker.io/cloudflare/cloudflared:latest", "cloudflare/cloudflared", "ghcr.io.evil.example/x:1"} {
		if err := e.Pull(context.Background(), ref, &bytes.Buffer{}); err != nil {
			t.Fatal(err)
		}
	}
	for _, c := range s.Calls {
		if strings.Contains(c, "login") {
			t.Errorf("logged in for another registry: %q", c)
		}
	}
}

func TestWithNoTokenNothingLogsIn(t *testing.T) {
	s := engine.NewScripted()
	e := engine.WithRegistryToken(engine.NewPodman(s), "")
	if err := e.Pull(context.Background(), "ghcr.io/djlsystems/yawble:1", &bytes.Buffer{}); err != nil {
		t.Fatal(err)
	}
	if strings.Join(s.Calls, "\n") != "podman pull ghcr.io/djlsystems/yawble:1" {
		t.Errorf("calls %q", s.Calls)
	}
}

func TestAFailedLoginStopsThePullAndNamesTheToken(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman login", engine.Result{Stderr: "Error: invalid username/password", ExitCode: 125})
	e := engine.WithRegistryToken(engine.NewPodman(s), secret)
	err := e.Pull(context.Background(), "ghcr.io/djlsystems/yawble:1", &bytes.Buffer{})
	if err == nil || !strings.Contains(err.Error(), "GH_TOKEN") || !strings.Contains(err.Error(), "read:packages") {
		t.Fatalf("err %v", err)
	}
	for _, c := range s.Calls {
		if strings.HasPrefix(c, "podman pull") {
			t.Errorf("pulled after a failed login: %q", c)
		}
	}
}
