package cli_test

import (
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/cli"
)

func TestConfigSetThenGet(t *testing.T) {
	dir := t.TempDir()
	if code, _, errOut := run(t, cli.Deps{ConfigDir: dir}, "config", "set", "port", "8090"); code != 0 {
		t.Fatalf("set: exit %d %s", code, errOut)
	}
	code, out, _ := run(t, cli.Deps{ConfigDir: dir}, "config", "get", "port")
	if code != 0 || strings.TrimSpace(out) != "8090" {
		t.Errorf("get: exit %d out %q", code, out)
	}
	code, out, _ = run(t, cli.Deps{ConfigDir: dir}, "config", "get", "--json")
	if code != 0 || !strings.Contains(out, `"port": 8090`) && !strings.Contains(out, `"port":8090`) {
		t.Errorf("get --json: exit %d out %q", code, out)
	}
}

func TestConfigSetABadValueExitsTwoAndWritesNothing(t *testing.T) {
	dir := t.TempDir()
	code, _, errOut := run(t, cli.Deps{ConfigDir: dir}, "config", "set", "port", "abc")
	if code != 2 || !strings.Contains(errOut, "port") {
		t.Errorf("exit %d stderr %q", code, errOut)
	}
	code, out, _ := run(t, cli.Deps{ConfigDir: dir}, "config", "get", "port")
	if code != 0 || strings.TrimSpace(out) != "8080" {
		t.Errorf("after a refused set, port should read the default 8080: exit %d out %q", code, out)
	}
}

func TestConfigSetDoesNotCopyAnEnvironmentOverrideIntoTheFile(t *testing.T) {
	dir := t.TempDir()
	env := map[string]string{"YAWBLE_PORT": "9999"}
	deps := cli.Deps{ConfigDir: dir, Env: func(k string) string { return env[k] }}
	if code, _, errOut := run(t, deps, "config", "set", "cpus", "2"); code != 0 {
		t.Fatalf("set: exit %d %s", code, errOut)
	}
	// With the environment gone, the file must not remember 9999.
	code, out, _ := run(t, cli.Deps{ConfigDir: dir}, "config", "get", "port")
	if code != 0 || strings.TrimSpace(out) != "8080" {
		t.Errorf("exit %d out %q", code, out)
	}
}
