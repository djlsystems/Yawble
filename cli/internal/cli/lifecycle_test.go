package cli_test

import (
	"encoding/json"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/cli"
	"github.com/djlsystems/yawble/cli/internal/engine"
)

func TestStatusJSONNamesTheContainerStateAndImage(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman version", engine.Result{Stdout: "5.4.0\n"})
	s.On("podman container inspect", engine.Result{Stdout: "exited\n"})
	env := map[string]string{"YAWBLE_IMAGE": "ghcr.io/djlsystems/yawble:2026.09.24.1"}
	code, out, errOut := run(t, cli.Deps{Runner: s, Env: func(k string) string { return env[k] }}, "status", "--json")
	if code != 0 {
		t.Fatalf("exit %d %s", code, errOut)
	}
	var got map[string]any
	if err := json.Unmarshal([]byte(out), &got); err != nil {
		t.Fatalf("not json: %v %s", err, out)
	}
	if got["container"] != "stopped" || got["engineVersion"] != "5.4.0" || got["image"] != env["YAWBLE_IMAGE"] {
		t.Errorf("got %v", got)
	}
}

func TestUpWithoutAnImageExitsOneAndNamesTheSetting(t *testing.T) {
	s := engine.NewScripted()
	code, _, errOut := run(t, cli.Deps{Runner: s}, "up")
	if code != 1 || !strings.Contains(errOut, "config set image") {
		t.Errorf("exit %d stderr %q", code, errOut)
	}
	if len(s.Calls) != 0 {
		t.Errorf("engine was called: %q", s.Calls)
	}
}

// Important 5 from the review: the env file carries provider keys. A file other local users can
// read is refused with the fix named, before anything is started.
func TestUpRefusesAnEnvFileOtherUsersCanRead(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("file modes are not Unix modes on Windows")
	}
	dir := t.TempDir()
	envPath := filepath.Join(dir, "env")
	if err := os.WriteFile(envPath, []byte("ANTHROPIC_API_KEY=sk-test\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	s := engine.NewScripted()
	env := map[string]string{"YAWBLE_IMAGE": "ghcr.io/djlsystems/yawble:2026.09.24.1"}
	code, _, errOut := run(t, cli.Deps{ConfigDir: dir, Runner: s, Env: func(k string) string { return env[k] }}, "up")
	if code != 1 || !strings.Contains(errOut, "chmod 600") || !strings.Contains(errOut, envPath) {
		t.Errorf("exit %d stderr %q", code, errOut)
	}
	if len(s.Calls) != 0 {
		t.Errorf("engine was called before the refusal: %q", s.Calls)
	}
}

// `config set engine docker` drives Docker through the same commands.
func TestStatusWithDockerConfiguredAsksDocker(t *testing.T) {
	s := engine.NewScripted()
	s.On("docker version", engine.Result{Stdout: "27.1.0\n"})
	s.On("docker container inspect", engine.Result{Stdout: "exited|" + testImage + "|\n"})
	deps := stubbed(s)
	env := map[string]string{"YAWBLE_IMAGE": testImage, "YAWBLE_ENGINE": "docker"}
	deps.Env = func(k string) string { return env[k] }
	deps.LookPath = lookPath("docker")
	code, out, errOut := run(t, deps, "status", "--json")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if !strings.Contains(out, `"engine": "docker"`) || !strings.Contains(out, `"engineVersion": "27.1.0"`) {
		t.Errorf("out %s", out)
	}
	for _, c := range s.Calls {
		if strings.HasPrefix(c, "podman") {
			t.Errorf("podman was asked with docker configured: %q", c)
		}
	}
}

func TestDownOnAStoppedInstanceSaysSoAndExitsZero(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman container inspect", engine.Result{Stdout: "exited\n"})
	code, out, _ := run(t, cli.Deps{Runner: s}, "down")
	if code != 0 || !strings.Contains(out, "not running") {
		t.Errorf("exit %d out %q", code, out)
	}
}
