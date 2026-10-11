package engine_test

import (
	"errors"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/engine"
)

func onPath(names ...string) func(string) (string, error) {
	return func(name string) (string, error) {
		for _, n := range names {
			if n == name {
				return "/usr/bin/" + name, nil
			}
		}
		return "", errors.New("not found")
	}
}

func TestSelectPrefersPodmanThenDockerThenNothing(t *testing.T) {
	cases := []struct {
		configured string
		have       []string
		want       string
	}{
		{"", []string{"podman", "docker"}, "podman"},
		{"", []string{"docker"}, "docker"},
		{"", nil, ""},
		{"docker", []string{"podman", "docker"}, "docker"},
		{"podman", []string{"docker"}, "podman"}, // chosen explicitly: the preflight installs it
	}
	for _, tc := range cases {
		if got := engine.Select(tc.configured, onPath(tc.have...)); got != tc.want {
			t.Errorf("Select(%q, %v) = %q, want %q", tc.configured, tc.have, got, tc.want)
		}
	}
}

func TestNewEngineByName(t *testing.T) {
	s := engine.NewScripted()
	if e := engine.New("docker", s); e.Name() != "docker" {
		t.Errorf("docker: %q", e.Name())
	}
	if e := engine.New("podman", s); e.Name() != "podman" {
		t.Errorf("podman: %q", e.Name())
	}
	if e := engine.New("", s); e.Name() != "podman" {
		t.Errorf("empty falls back to podman: %q", e.Name())
	}
}
