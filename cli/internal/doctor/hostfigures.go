package doctor

import (
	"fmt"
	"strings"
)

// THE HOST'S FIGURES: the running limit and the memory one run gets are what the Host decided,
// read from its --doctor report and never computed here. The report carries the objects GET
// /api/wip answers with; a Host that does not send them, or a report that could not be had, is
// "not known". There is no estimate to fall back on.

// HostWip is the report's `wip` section.
type HostWip struct {
	Limit     *WipLimit  `json:"limit"`
	RunMemory *RunMemory `json:"runMemory"`
}

// WipLimit mirrors the Host's WipRunLimit: the limit, the bound that decided it (setting,
// configuration, cpu or memory), what each built-in bound allows, and the Host's sentence.
type WipLimit struct {
	Limit          int    `json:"limit"`
	Bound          string `json:"bound"`
	CPUBound       int    `json:"cpuBound"`
	Cpus           *int   `json:"cpus"`
	MemoryBound    *int   `json:"memoryBound"`
	MemoryLimitMb  *int64 `json:"memoryLimitMb"`
	MemoryPerRunMb int    `json:"memoryPerRunMb"`
	Reason         string `json:"reason"`
}

// RunMemory is how the Host limits one run's memory. Mechanism is "cgroup", "rlimit" or "none";
// PerRunMb is the figure applied, when there is one.
type RunMemory struct {
	Mechanism string `json:"mechanism"`
	PerRunMb  *int   `json:"perRunMb"`
	Detail    string `json:"detail"`
}

const notKnownOlderHost = "not known: this Host's report does not carry it; update to a newer Yawble image"

// runLimitRow is the Host's running limit and its reason. A limit set by a person above what
// the Host's own bounds allow warns: runs then contend for CPU and memory until the Host stops
// answering. Both sides of that comparison are the Host's figures.
func runLimitRow(r *HostReport) Check {
	const name = "running limit"
	if r.Wip == nil || r.Wip.Limit == nil {
		return Check{name, Skip, notKnownOlderHost, ""}
	}
	l := r.Wip.Limit
	limit := fmt.Sprint(l.Limit)
	if l.Limit == 0 {
		limit = "unlimited"
	}
	detail := fmt.Sprintf("%s, from the %s bound (the Host's answer: %s)", limit, l.Bound, l.Reason)
	if l.Bound == "setting" || l.Bound == "configuration" {
		allowed := l.CPUBound
		if l.MemoryBound != nil && *l.MemoryBound < allowed {
			allowed = *l.MemoryBound
		}
		if l.Limit == 0 || l.Limit > allowed {
			// In the default list, so in words a person can act on: where the limit was set,
			// not which of the Host's bounds it came from.
			where := "set in the board's settings"
			if l.Bound == "configuration" {
				where = "set with yawble config set maxRunning"
			}
			return Check{name, Warn, fmt.Sprintf("%s, %s; this computer allows %d", limit, where, allowed),
				"yawble config set maxRunning 0, then yawble up, to use the limit Yawble works out for this computer; or lower \"Agents running at once\" in the board's settings"}
		}
	}
	return Check{name, OK, detail, ""}
}

// runMemoryRow is how one run's memory is limited, in the Host's words.
func runMemoryRow(r *HostReport) Check {
	const name = "run memory"
	if r.Wip == nil || r.Wip.RunMemory == nil {
		return Check{name, Skip, notKnownOlderHost, ""}
	}
	m := r.Wip.RunMemory
	var text string
	switch m.Mechanism {
	case "cgroup":
		text = "cgroup"
		if m.PerRunMb != nil {
			text += fmt.Sprintf(", %d MB per run", *m.PerRunMb)
		}
	case "rlimit":
		text = "rlimit"
		if m.PerRunMb != nil {
			text += fmt.Sprintf(", %d MB per run", *m.PerRunMb)
		} else {
			text += ", figure not known"
		}
	case "none":
		// How this engine is, not something gone wrong: the Host's sentence would only repeat it.
		return Check{name, Info, "no per-run memory cap: this engine offers none, so runs share the worker's memory (a fact of the engine, not a fault)", ""}
	default:
		text = fmt.Sprintf("not known (the Host said %q)", m.Mechanism)
	}
	if d := strings.TrimSpace(m.Detail); d != "" {
		text += ": " + d
	}
	return Check{name, Info, text, ""}
}
