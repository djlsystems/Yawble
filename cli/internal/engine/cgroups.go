package engine

import (
	"context"
	"encoding/json"
	"slices"
	"strings"
)

// LimitControllers are the cgroup controllers a container's limits need: memory for --memory,
// cpu for --cpus, pids for the engine's process limit. Without them the engine cannot apply the
// limit, and crun says so as a missing `memory.max` (measured on a rootless WSL Podman machine).
var LimitControllers = []string{"memory", "cpu", "pids"}

// Cgroups is the engine's own report of the cgroup controllers its containers get. Measured false
// is not measured, never "all there": Reason says why. Source is the command that answered.
type Cgroups struct {
	Measured    bool
	Controllers []string
	Source      string
	Reason      string
}

// Missing is the limit controllers the report lacks, in LimitControllers' order; none when not
// measured, because nothing is known to be missing.
func (c Cgroups) Missing() []string {
	if !c.Measured {
		return nil
	}
	var missing []string
	for _, want := range LimitControllers {
		if !slices.Contains(c.Controllers, want) {
			missing = append(missing, want)
		}
	}
	return missing
}

// podmanCgroups reads `podman info --format json`'s host.cgroupControllers. An answer with no
// such field (an older Podman, or something unreadable) is not measured; an empty list is
// measured, and is every controller missing.
func podmanCgroups(stdout string) Cgroups {
	const source = "podman info"
	var info struct {
		Host struct {
			CgroupControllers *[]string `json:"cgroupControllers"`
		} `json:"host"`
	}
	if err := json.Unmarshal([]byte(stdout), &info); err != nil {
		return Cgroups{Source: source, Reason: "podman info did not answer in JSON"}
	}
	if info.Host.CgroupControllers == nil {
		return Cgroups{Source: source, Reason: "podman info does not report its cgroup controllers"}
	}
	return Cgroups{Measured: true, Controllers: *info.Host.CgroupControllers, Source: source}
}

// dockerCgroups reads `docker info --format json`: Docker has no list of controllers, but says
// for each limit whether it can apply it (MemoryLimit, CpuCfsQuota, PidsLimit), which is the same
// fact. Any of the three absent is not measured.
func dockerCgroups(stdout string) Cgroups {
	const source = "docker info"
	var info struct {
		MemoryLimit *bool `json:"MemoryLimit"`
		CPUCfsQuota *bool `json:"CpuCfsQuota"`
		PidsLimit   *bool `json:"PidsLimit"`
	}
	if err := json.Unmarshal([]byte(stdout), &info); err != nil {
		return Cgroups{Source: source, Reason: "docker info did not answer in JSON"}
	}
	if info.MemoryLimit == nil || info.CPUCfsQuota == nil || info.PidsLimit == nil {
		return Cgroups{Source: source, Reason: "docker info does not say whether it can apply memory, CPU and process limits"}
	}
	var have []string
	for _, c := range []struct {
		name string
		ok   bool
	}{{"memory", *info.MemoryLimit}, {"cpu", *info.CPUCfsQuota}, {"pids", *info.PidsLimit}} {
		if c.ok {
			have = append(have, c.name)
		}
	}
	return Cgroups{Measured: true, Controllers: have, Source: source}
}

// cgroups runs `<program> info --format json` and reads it with parse. A failed info is not
// measured, with the engine's own sentence as the reason.
func (b base) cgroups(ctx context.Context, parse func(string) Cgroups) Cgroups {
	res, err := b.run(ctx, "info", "--format", "json")
	if err != nil {
		return Cgroups{Source: b.program + " info", Reason: err.Error()}
	}
	if strings.TrimSpace(res.Stdout) == "" {
		return Cgroups{Source: b.program + " info", Reason: b.program + " info answered nothing"}
	}
	return parse(res.Stdout)
}

func (p podman) Cgroups(ctx context.Context) Cgroups { return p.cgroups(ctx, podmanCgroups) }

func (d docker) Cgroups(ctx context.Context) Cgroups { return d.cgroups(ctx, dockerCgroups) }
