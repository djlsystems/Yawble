package cli_test

import (
	"crypto/sha256"
	"encoding/hex"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/config"
	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/instance"
)

// testWorkerKey is the worker key every test's config folder holds unless the test makes its own,
// so a scripted container's label can carry the hash a real `up` would have stamped.
var testWorkerKey = strings.Repeat("ab", 32)

func testKeyHash() string {
	sum := sha256.Sum256([]byte(testWorkerKey))
	return hex.EncodeToString(sum[:])[:12]
}

func seedWorkerKey(t *testing.T, dir string) {
	t.Helper()
	if err := os.WriteFile(filepath.Join(dir, config.WorkerKeyFileName), []byte("HARNESS_WORKER_KEY="+testWorkerKey+"\n"), 0o600); err != nil {
		t.Fatal(err)
	}
}

// settingsFor is what prepare() derives on this machine for an image, the test key included.
func settingsForImage(image string) instance.Settings {
	s, _ := instance.Defaults(config.Config{Image: image}, instance.Measure(), "")
	s.KeyHash = testKeyHash()
	return s
}

func inspectLine(program, name string) string {
	field := "{{.ImageName}}"
	if program == "docker" {
		field = "{{.Config.Image}}"
	}
	return program + " container inspect --format {{.State.Status}}|" + field + "|{{index .Config.Labels \"yawble.settings\"}} " + name
}

func healthLine(program, name string) string {
	if program == "docker" {
		return "docker container inspect --format {{if .State.Health}}{{.State.Health.Status}}{{end}} " + name
	}
	return "podman container inspect --format {{.State.Health.Status}} " + name
}

// scriptWorker answers worker i as a container in state ("running", "exited") made with st, and
// healthy: connected to control.
func scriptWorker(s *engine.Scripted, program, state string, st instance.Settings, i int) {
	ref, _ := st.WorkerRef()
	name := instance.WorkerName(i)
	s.On(inspectLine(program, name), engine.Result{Stdout: state + "|" + ref + "|" + instance.WorkerSettingsLabel(st, i, ref) + "\n"})
	s.On(healthLine(program, name), engine.Result{Stdout: "healthy\n"})
}

// scriptWorkers lists workers 1..n by label as the engine would.
func scriptWorkers(s *engine.Scripted, program string, n int) {
	var names []string
	for i := 1; i <= n; i++ {
		names = append(names, instance.WorkerName(i))
	}
	s.On(program+" ps -a --filter label=yawble.role=worker", engine.Result{Stdout: strings.Join(names, "\n") + "\n"})
}

var _ = strconv.Itoa
