package cli_test

import (
	"strings"
	"testing"
	"time"

	"github.com/djlsystems/yawble/cli/internal/engine"
)

// D2: on Docker nothing is run by hand, yet a healthy record whose last check is older than
// three intervals (a paused daemon, or a VM clock behind this computer's) warns that "run by hand
// it passes" and points at rootless Podman's systemd --user timers.
func TestDockerDoctorNeverClaimsAHandRun(t *testing.T) {
	s := engine.NewScripted()
	s.On("docker version", engine.Result{Stdout: "27.0.0\n"})
	s.On("docker container inspect", engine.Result{Stdout: "running|" + testImage + "|" + currentLabel() + "\n"})
	scriptWorker(s, "docker", "running", settingsForImage(testImage), 1)
	s.On("docker container inspect yawble-worker-1", engine.Result{Stdout: inspectDoc(18 * time.Minute)})
	deps := stubbed(s)
	deps.Env = func(k string) string {
		return map[string]string{"YAWBLE_IMAGE": testImage, "YAWBLE_ENGINE": "docker"}[k]
	}
	deps.Now = func() time.Time { return timerNow }
	_, out, _ := run(t, deps, "doctor", "--details")
	for _, bad := range []string{"run by hand it passes", "rootless Podman", "`yawble up` runs the checks itself"} {
		if strings.Contains(out, bad) {
			t.Errorf("docker output says %q:\n%s", bad, out)
		}
	}
}
