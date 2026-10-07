package cli

import "github.com/djlsystems/yawble/cli/internal/instance"

// FirstUpScreen lets the tests read the first up's screen for an engine they cannot script end
// to end (a Podman machine on macOS), with one worker and no running limit configured.
func FirstUpScreen(m instance.Machine, memoryMB, cpus int) string {
	return firstUpScreen(m, memoryMB, cpus, 1, 0)
}
