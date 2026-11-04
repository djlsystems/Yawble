package instance

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"regexp"
	"slices"
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

// controllerRefused is a runtime naming a controller it cannot use: crun's "controller `pids` is
// not available under .../cgroup.controllers" (exit 126, measured on a rootless WSL Podman machine
// given --pids-limit), and runc's "controller \"pids\" not available" / "can't set pids limit"
// and "cannot set memory limit" under Docker, whose CLI prints a backslash before each double quote.
// The name is the first group.
var controllerRefused = regexp.MustCompile("controller \\\\?[`'\"]?(memory|cpu|cpuset|pids|io)\\\\?[`'\"]? (?:is )?not (?:available|enabled|supported)|(?:can't|cannot|unable to) set (memory|cpu|pids) limit")

// cgroupFiles are the cgroup files a runtime names when it cannot open one, by the controller
// whose limit it is.
var cgroupFiles = []struct{ file, controller string }{
	{"memory.", "memory"}, {"cpu.max", "cpu"}, {"cpu.weight", "cpu"}, {"pids.max", "pids"},
}

// CgroupError says whether an engine's error is the cgroup controllers not being delegated:
// a limit the engine cannot apply. Such an error is never a port's.
func CgroupError(err error) bool {
	if err == nil {
		return false
	}
	text := strings.ToLower(err.Error())
	if controllerRefused.MatchString(text) {
		return true
	}
	for _, mark := range cgroupMarks {
		if strings.Contains(text, mark) {
			return true
		}
	}
	return false
}

// refusedControllers is the controllers a cgroup error names, in LimitControllers' order; none
// when it names none.
func refusedControllers(err error) []string {
	if err == nil {
		return nil
	}
	text := strings.ToLower(err.Error())
	named := map[string]bool{}
	for _, m := range controllerRefused.FindAllStringSubmatch(text, -1) {
		named[m[1]+m[2]] = true
	}
	for _, f := range cgroupFiles {
		if strings.Contains(text, "`"+f.file) || strings.Contains(text, "/"+f.file) {
			named[f.controller] = true
		}
	}
	var out []string
	for _, c := range engine.LimitControllers {
		if named[c] {
			out = append(out, c)
		}
	}
	return out
}

// LimitsFix is how to give the engine the controllers, for the engine detected; it names no
// operating system. Shared by `up` and the doctor row so the two cannot drift.
func LimitsFix(engineName string) string {
	if engineName == "docker" {
		return "give Docker the memory, cpu and pids cgroup controllers (rootless Docker: delegate them to your user's systemd session, Delegate=cpu memory pids for user@.service); `docker info` then reports MemoryLimit, CpuCfsQuota and PidsLimit true. Then: yawble up"
	}
	return "delegate the memory, cpu and pids cgroup controllers to Podman: enable them in each cgroup.subtree_control from /sys/fs/cgroup down to the cgroup Podman runs in (on a Podman machine, inside it: podman machine ssh), or run Podman in systemd's user session with cgroup_manager = \"systemd\"; `podman info` then lists them under cgroupControllers. Then: yawble up"
}

// limitNames are the limits each controller carries, as a person reads them.
var limitNames = map[string]string{"memory": "memory", "cpu": "CPU", "pids": "process (pids)"}

// limitsOf is the limits the missing controllers carry: "memory and CPU limits", "process (pids)
// limit".
func LimitsOf(missing []string) string {
	var names []string
	for _, c := range missing {
		if n, ok := limitNames[c]; ok {
			names = append(names, n)
		}
	}
	switch len(names) {
	case 0:
		return "limits"
	case 1:
		return names[0] + " limit"
	}
	return strings.Join(names[:len(names)-1], ", ") + " and " + names[len(names)-1] + " limits"
}

// NoLimitsLine is the warning `up` prints when it starts the instance without the containers'
// limits. missing is the controllers the engine reported missing, or its error named; none when
// neither said which. Whichever is missing, every limit Yawble sets is dropped.
func NoLimitsLine(engineName string, missing []string) string {
	if len(missing) == 0 {
		return noLimitsWarning(engineName, "the engine could not apply the containers' limits because its cgroup controllers are not delegated")
	}
	return noLimitsWarning(engineName, fmt.Sprintf("%s info reports no %s cgroup controller, so it cannot apply the containers' %s", engineName, strings.Join(missing, ", "), LimitsOf(missing)))
}

// refusedLine is NoLimitsLine for a run or start the engine refused: the controllers its error
// names, said as the engine's refusal, not as its report.
func refusedLine(engineName string, err error) string {
	refused := refusedControllers(err)
	if len(refused) == 0 {
		return NoLimitsLine(engineName, nil)
	}
	return noLimitsWarning(engineName, fmt.Sprintf("the engine refused the containers' %s: no %s cgroup controller is delegated to it", LimitsOf(refused), strings.Join(refused, ", ")))
}

func noLimitsWarning(engineName, why string) string {
	return "warning: " + why + "; starting WITHOUT the containers' limits: no memory or CPU limit is applied to any container, and the Host admits runs by its run limit alone. To have the limits, " + LimitsFix(engineName)
}

// UnmeasuredLine is the warning `up` prints when the instance runs without the containers'
// limits and the engine's report of its controllers cannot be read: the cause is not measured.
func UnmeasuredLine(reason string) string {
	return "warning: the instance runs without container limits (no memory or CPU limit is applied to any container), and the cause could not be measured: " + reason + ". When the engine answers, yawble doctor's cgroups row says what is missing; yawble up then applies the limits if it can"
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
// keeps what the existing container was made with, and says so when that is without limits (a
// first run that the engine refuses for want of them degrades then).
func decideLimits(ctx context.Context, e engine.Engine, label string, out io.Writer) bool {
	c := e.Cgroups(ctx)
	if !c.Measured {
		if NoLimitsOf(label) {
			fmt.Fprintln(out, UnmeasuredLine(c.Reason))
			return true
		}
		return false
	}
	missing := c.Missing()
	if len(missing) == 0 {
		return false
	}
	fmt.Fprintln(out, NoLimitsLine(e.Name(), missing))
	return true
}

// CgroupsSeen is the engine's report as a worker made without limits beside a control made with
// them records it: the limit controllers it has, or that it was not measured. The worker is made
// again only when this changes (or its settings do), never on every up.
func CgroupsSeen(c engine.Cgroups) string {
	if !c.Measured {
		return "not measured"
	}
	var have []string
	for _, want := range engine.LimitControllers {
		if slices.Contains(c.Controllers, want) {
			have = append(have, want)
		}
	}
	return "has: " + strings.Join(have, ",")
}
