package config_test

import (
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/config"
)

func noEnv(string) string { return "" }

func TestAMissingFileIsTheZeroConfigAndIsNotCreated(t *testing.T) {
	dir := t.TempDir()
	c, path, err := config.Load(dir, noEnv)
	if err != nil {
		t.Fatal(err)
	}
	if c != (config.Config{}) {
		t.Errorf("got %+v", c)
	}
	if _, statErr := os.Stat(path); !os.IsNotExist(statErr) {
		t.Errorf("Load created %s", path)
	}
}

func TestEnvironmentWinsOverTheFile(t *testing.T) {
	dir := t.TempDir()
	if _, err := config.Save(dir, config.Config{Port: 9000, Engine: "podman"}); err != nil {
		t.Fatal(err)
	}
	env := map[string]string{"YAWBLE_PORT": "9100"}
	c, _, err := config.Load(dir, func(k string) string { return env[k] })
	if err != nil {
		t.Fatal(err)
	}
	if c.Port != 9100 || c.Engine != "podman" {
		t.Errorf("got %+v", c)
	}
}

func TestSetValidatesEachKey(t *testing.T) {
	cases := []struct{ key, value, wantErr string }{
		{"port", "abc", "port"},
		{"port", "70000", "1 and 65535"},
		{"cpus", "-1", "cpus"},
		{"memory", "12", "12g"},
		{"engine", "lxc", "podman or docker"},
		{"colour", "red", "unknown key"},
	}
	for _, tc := range cases {
		var c config.Config
		err := c.Set(tc.key, tc.value)
		if err == nil || !strings.Contains(err.Error(), tc.wantErr) {
			t.Errorf("Set(%s,%s): err %v, want it to mention %q", tc.key, tc.value, err, tc.wantErr)
		}
	}
	var c config.Config
	for _, ok := range [][2]string{{"port", "8081"}, {"memory", "12g"}, {"cpus", "4"}, {"maxRunning", "4"}, {"engine", "docker"}, {"image", "ghcr.io/x/y:1"}} {
		if err := c.Set(ok[0], ok[1]); err != nil {
			t.Errorf("Set(%s,%s): %v", ok[0], ok[1], err)
		}
	}
}

func TestACorruptFileIsAnErrorNamingThePathAndIsLeftAlone(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "config.toml")
	if err := os.WriteFile(path, []byte("port = [unterminated"), 0o644); err != nil {
		t.Fatal(err)
	}
	_, _, err := config.Load(dir, noEnv)
	if err == nil || !strings.Contains(err.Error(), path) {
		t.Fatalf("err %v should name %s", err, path)
	}
	after, _ := os.ReadFile(path)
	if string(after) != "port = [unterminated" {
		t.Error("Load rewrote the corrupt file")
	}
}

// Important 8 from the review: a hand-edited file goes through the same rules as `set`, or
// `memory = "12"` reaches podman as a 12-byte limit.
func TestAFileValueIsValidatedOnLoadNamingThePathAndKey(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "config.toml")
	if err := os.WriteFile(path, []byte("memory = \"12\"\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	_, _, err := config.Load(dir, noEnv)
	if err == nil || !strings.Contains(err.Error(), path) || !strings.Contains(err.Error(), "memory") {
		t.Errorf("err %v", err)
	}
}

func TestAKeyTheFileDoesNotHaveIsAnErrorNotSilence(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "config.toml")
	if err := os.WriteFile(path, []byte("max_running = 3\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	_, _, err := config.Load(dir, noEnv)
	if err == nil || !strings.Contains(err.Error(), "max_running") || !strings.Contains(err.Error(), "maxRunning") {
		t.Errorf("err %v should name the typo and the real key", err)
	}
}

func TestSaveRoundTrips(t *testing.T) {
	dir := t.TempDir()
	want := config.Config{Engine: "podman", Port: 8082, Memory: "6g", CPUs: 3, MaxRunning: 2, Image: "ghcr.io/djlsystems/yawble:2026.09.24.1"}
	if _, err := config.Save(dir, want); err != nil {
		t.Fatal(err)
	}
	got, _, err := config.Load(dir, noEnv)
	if err != nil || got != want {
		t.Errorf("got %+v err %v", got, err)
	}
}

func TestChannelIsLatestByDefaultAndStableWhenChosen(t *testing.T) {
	var c config.Config
	if got, _ := c.Get("channel"); got != "latest" {
		t.Errorf("default channel %q", got)
	}
	if err := c.Set("channel", "stable"); err != nil || c.Channel != "stable" {
		t.Errorf("stable: %v %q", err, c.Channel)
	}
	if err := c.Set("channel", "beta"); err == nil || !strings.Contains(err.Error(), "latest") || !strings.Contains(err.Error(), "stable") {
		t.Errorf("beta was accepted: %v", err)
	}
	dir := t.TempDir()
	env := func(k string) string {
		if k == "YAWBLE_CHANNEL" {
			return "stable"
		}
		return ""
	}
	if loaded, _, err := config.Load(dir, env); err != nil || loaded.Channel != "stable" {
		t.Errorf("YAWBLE_CHANNEL: %v %q", err, loaded.Channel)
	}
}
