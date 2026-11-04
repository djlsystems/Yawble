package instance

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"strings"

	"github.com/djlsystems/yawble/cli/internal/engine"
)

// cgroupMarks are the words of an engine error that is about the cgroup controllers, not about a
// port: a cgroup file the runtime could not open (crun on a rootless WSL Podman machine says
// "open `memory.max` for writing: No such file or directory"), the controllers themselves, or
// Docker refusing a limit its kernel cannot apply.
var cgroupMarks = []string{
	"memory.max", "memory.high", "memory.low", "memory.min", "memory.swap.max", "memory.oom.group",
	"cpu.max", "cpu.weight", "cpuset.cpus", "cpuset.mems", "pids.max", "io.max",
	"cgroup.subtree_control", "cgroup.controllers", "cgroup controller", "cgroups controller",
	"controllers are not", "does not support memory limit", "does not support cpu",
	"does not support pids limit", "cpu cfs", "nanocpus",
}

// CgroupError says whether an engine's error is the cgroup controllers not being delegated:
// a limit the engine cannot apply. Such an error is never a port's.
func CgroupError(err error) bool {
	if err == nil {
		return false
	}
	text := strings.ToLower(err.Error())
	for _, mark := range cgroupMarks {
		if strings.Contains(text, mark) {
			return true
		}
	}
	return false
}

// LimitsFix is how to give the engine the controllers, for the engine detected; it names no
// operating system. Shared by `up` and the doctor row so the two cannot drift.
func LimitsFix(engineName string) string {
	if engineName == "docker" {
		return "give Docker the memory, cpu and pids cgroup controllers (rootless Docker: delegate them to your user's systemd session, Delegate=cpu memory pids for user@.service); `docker info` then reports MemoryLimit, CpuCfsQuota and PidsLimit true. Then: yawble up"
	}
	return "delegate the memory, cpu and pids cgroup controllers to Podman: enable them in each cgroup.subtree_control from /sys/fs/cgroup down to the cgroup Podman runs in (on a Podman machine, inside it: podman machine ssh), or run Podman in systemd's user session with cgroup_manager = \"systemd\"; `podman info` then lists them under cgroupControllers. Then: yawble up"
}

// NoLimitsLine is the warning `up` prints when it starts the instance without the containers'
// memory and CPU limits. missing is what the engine reported missing; none when only the error
// said so.
func NoLimitsLine(engineName string, missing []string) string {
	why := "the engine could not apply the containers' memory and CPU limits because its cgroup controllers are not delegated"
	if len(missing) > 0 {
		why = fmt.Sprintf("%s info reports no %s cgroup controller, so it cannot apply the containers' memory and CPU limits", engineName, strings.Join(missing, ", "))
	}
	return "warning: " + why + "; starting WITHOUT them: no memory or CPU limit is applied to any container, and the Host admits runs by its run limit alone. To have the limits, " + LimitsFix(engineName)
}

// limitsError is a run or start the engine refused for want of the controllers, said as that:
// never as the port.
func limitsError(engineName string, err error) error {
	return fmt.Errorf("%w\nThe engine cannot apply the container's memory/CPU limit because the cgroup controllers (memory, cpu, pids) are not delegated to it; this is not the port. To fix it, %s", err, LimitsFix(engineName))
}

// limitsLabel is the part of a settings label that says the container was made without limits.
type limitsLabel struct {
	NoLimits bool `json:"noLimits,omitempty"`
}

// NoLimitsOf says whether a container's settings label records it was made without the memory
// and CPU limits.
func NoLimitsOf(label string) bool {
	var was limitsLabel
	return label != "" && json.Unmarshal([]byte(label), &was) == nil && was.NoLimits
}

// limitsChange names a change in whether the limits are applied, for `up`'s "settings changed".
func limitsChange(label string, s Settings) []string {
	was := NoLimitsOf(label)
	switch {
	case was && !s.NoLimits:
		return []string{"memory/CPU limits can be applied now"}
	case !was && s.NoLimits:
		return []string{"memory/CPU limits cannot be applied (cgroup controllers not delegated)"}
	}
	return nil
}

// limitsFor is whether the containers are made without limits, given control as it is: asked of
// the engine whenever control is not running or was made without them; a control running with
// them shows they apply.
func limitsFor(ctx context.Context, e engine.Engine, control engine.ContainerInfo, out io.Writer) bool {
	if control.State == engine.StateRunning && !NoLimitsOf(control.Label) {
		return false
	}
	return decideLimits(ctx, e, control.Label, out)
}

// decideLimits is whether `up` makes the containers without memory and CPU limits, from the
// engine's own report: missing controllers decide it, said once; a report that cannot be read
// keeps what the existing container was made with (a first run that the engine refuses for
// want of them degrades then).
func decideLimits(ctx context.Context, e engine.Engine, label string, out io.Writer) bool {
	c := e.Cgroups(ctx)
	if !c.Measured {
		return NoLimitsOf(label)
	}
	missing := c.Missing()
	if len(missing) == 0 {
		return false
	}
	fmt.Fprintln(out, NoLimitsLine(e.Name(), missing))
	return true
}
