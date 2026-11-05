package doctor

import (
	"fmt"
	"strings"

	"github.com/djlsystems/yawble/cli/internal/instance"
)

// CapacityCheck puts what the engine has beside what the container got. The engine's side is
// asked of the engine (docker info, the Podman machine, or this computer under Podman on Linux);
// the container's is the Host's own cgroup reading from its --doctor report. Either that could
// not be had is said, never estimated. A size the run asks for above what the engine has warns.
func CapacityCheck(m instance.Machine, s instance.Settings, r *HostReport) Check {
	const name = "capacity"
	engineSide := "engine has: not measured (the engine could not be asked)"
	if m.Measured {
		engineSide = fmt.Sprintf("engine has %d MB, %d CPUs (%s)", m.MemoryMB(), m.CPUs, m.Source)
	}
	detail := engineSide + "; " + containerGot(r)

	if m.Measured {
		var over, fixes []string
		if mb := instance.MemoryMB(s.Memory); mb > m.MemoryMB() {
			over = append(over, fmt.Sprintf("memory %s", s.Memory))
			fixes = append(fixes, fmt.Sprintf("yawble config set memory %dm", m.MemoryMB()))
		}
		if s.CPUs > m.CPUs {
			over = append(over, fmt.Sprintf("cpus %d", s.CPUs))
			fixes = append(fixes, fmt.Sprintf("yawble config set cpus %d", m.CPUs))
		}
		if len(over) > 0 {
			return Check{name, Warn, detail + "; yawble's config asks for " + strings.Join(over, " and ") + ", more than the engine has",
				strings.Join(fixes, "; ") + " (or less), then yawble up; or " + instance.MoreForEngine(m.Kind)}
		}
	}
	return Check{name, Info, detail, ""}
}

// containerGot is the container's memory limit and CPUs as the Host inside it reads them.
func containerGot(r *HostReport) string {
	if r == nil {
		return "the container got: not known (the Host could not be asked)"
	}
	if r.Wip == nil || r.Wip.Limit == nil {
		return "the container got: " + notKnownOlderHost
	}
	l := r.Wip.Limit
	memory := "no memory limit"
	if l.MemoryLimitMb != nil {
		memory = fmt.Sprintf("%d MB", *l.MemoryLimitMb)
	}
	// A count from the engine or the processors seen is not a limit the container got: its cgroup
	// does not say its CPUs, so say so and name where the running limit's count came from.
	if l.Cpus != nil {
		if from := countFrom(l, *l.Cpus); from != "" {
			return fmt.Sprintf("the container got %s and no CPU limit its cgroup says (the running limit counts %s)", memory, from)
		}
	}
	cpus := "CPUs not known"
	if l.Cpus != nil {
		cpus = fmt.Sprintf("%d CPUs", *l.Cpus)
	}
	return fmt.Sprintf("the container got %s, %s (the Host's cgroup reading)", memory, cpus)
}

// countFrom names a CPU count that did not come from the container's cgroup, in the Host's words;
// empty for the cgroup's own count and for an older Host that does not say its source.
func countFrom(l *WipLimit, cpus int) string {
	switch l.CpusSource {
	case "engine":
		return fmt.Sprintf("%d CPUs reported by the engine", cpus)
	case "processors":
		if l.Bound == "workers" {
			return fmt.Sprintf("%d processors the worker sees", cpus)
		}
		return fmt.Sprintf("%d processors the Host sees", cpus)
	}
	return ""
}
