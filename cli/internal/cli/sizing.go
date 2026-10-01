package cli

import (
	"context"
	"fmt"
	"io"
	"strconv"
	"strings"

	"github.com/djlsystems/yawble/cli/internal/config"
	"github.com/djlsystems/yawble/cli/internal/instance"
	"github.com/djlsystems/yawble/cli/internal/machine"
)

// memoryFloorMB is the least memory the first-up screen accepts: the Host's own share
// (TenantSettings.HostReserveMb, 1024 MB) beside one run at its default allowance (2048 MB),
// rounded up to 4 GB. An engine with less than that is held to the proposal instead.
const memoryFloorMB = 4096

// engineCapacity is what the engine the container runs in has, asked of that engine: `docker
// info` under Docker on every OS (Docker Desktop's VM, or the Linux host), the Podman machine on
// macOS and Windows, and this computer under Podman on Linux. An engine that cannot answer is
// not measured; nothing is estimated in its place.
func engineCapacity(ctx context.Context, deps Deps, name string) instance.Machine {
	if name == "docker" {
		return dockerMachine(ctx, runnerOf(deps))
	}
	goos := goosOf(deps)
	if goos != "darwin" && goos != "windows" {
		return instance.Measure()
	}
	info, err := machine.Inspect(ctx, runnerOf(deps), goos)
	if err != nil {
		return instance.Machine{}
	}
	return machineOf(info)
}

// sizeFirstUp is the first `up`'s choice of the container's memory and CPUs, made when yawble's
// config holds neither. At a terminal it shows what the engine has, the proposal and the running
// limit the Host's rule derives from it, and asks for each value; with --yes or no terminal it
// takes the proposal and says so. Either way the answer is saved, so later ups do not ask. An
// engine that was not measured is not asked about: Defaults says so, and nothing is saved.
func sizeFirstUp(deps Deps, c config.Config, m instance.Machine, yes bool, out io.Writer) (config.Config, error) {
	if c.Memory != "" || c.CPUs != 0 || !m.Measured {
		return c, nil
	}
	memoryMB, cpus := instance.Proposed(m)
	changeHint := "yawble config set memory <size> and yawble config set cpus <n> change them, then yawble up"
	if yes || !deps.Interactive || deps.Stdin == nil {
		if err := saveSize(deps, memoryMB, cpus); err != nil {
			return c, err
		}
		fmt.Fprintf(out, "memory %dm and cpus %d for the container: half of the %d MB and the %d CPUs the engine has (%s), at most 12 GB and 8 CPUs; saved in yawble's config (%s)\n",
			memoryMB, cpus, m.MemoryMB(), m.CPUs, m.Source, changeHint)
		c.Memory, c.CPUs = fmt.Sprintf("%dm", memoryMB), cpus
		return c, nil
	}

	floor := memoryFloorMB
	if m.MemoryMB() < floor {
		floor = memoryMB
	}
	fmt.Fprint(out, firstUpScreen(m, memoryMB, cpus, c.MaxRunning))
	memoryMB = askNumber(deps, out, fmt.Sprintf("Memory in MB (%d to %d) [%d]: ", floor, m.MemoryMB(), memoryMB), memoryMB,
		parseMemoryMB, func(v int) string {
			switch {
			case v > m.MemoryMB():
				return fmt.Sprintf("%d MB is more than the engine has; the most is %d MB", v, m.MemoryMB())
			case v < floor:
				return fmt.Sprintf("%d MB is below the floor of %d MB that leaves the Host a usable share; the least is %d MB", v, floor, floor)
			}
			return ""
		})
	cpus = askNumber(deps, out, fmt.Sprintf("CPUs (1 to %d) [%d]: ", m.CPUs, cpus), cpus,
		parseCount, func(v int) string {
			switch {
			case v > m.CPUs:
				return fmt.Sprintf("%d CPUs is more than the engine has; the most is %d", v, m.CPUs)
			case v < 1:
				return "the least is 1 CPU"
			}
			return ""
		})
	if err := saveSize(deps, memoryMB, cpus); err != nil {
		return c, err
	}
	fmt.Fprintf(out, "saved memory %dm and cpus %d in yawble's config; the Host's rule derives a running limit of %d from them (%s)\n",
		memoryMB, cpus, instance.DerivedRunLimit(memoryMB, cpus, instance.HostDefaultMemoryPerRunMb), changeHint)
	c.Memory, c.CPUs = fmt.Sprintf("%dm", memoryMB), cpus
	return c, nil
}

// firstUpScreen is the one short screen before the questions. The running limit on it is the
// only CLI-derived limit anywhere, and it says so: `doctor` reports the Host's own answer.
func firstUpScreen(m instance.Machine, memoryMB, cpus, maxRunning int) string {
	limit := fmt.Sprintf("%d at once, derived from the Host's rule: the smaller of CPUs - 1 and memory / wip.memoryPerRunMb (the Host's default, %d MB). Not asked; yawble config set maxRunning overrides it",
		instance.DerivedRunLimit(memoryMB, cpus, instance.HostDefaultMemoryPerRunMb), instance.HostDefaultMemoryPerRunMb)
	if maxRunning > 0 {
		limit += fmt.Sprintf("; it is set to %d there now", maxRunning)
	}
	return fmt.Sprintf("How much of the engine should Yawble's container get? (asked once)\n"+
		"  the engine has   %d MB memory, %d CPUs (%s)\n"+
		"  proposed         %d MB memory, %d CPUs (half the engine's memory up to 12 GB; its CPUs up to 8)\n"+
		"  running limit    %s\n"+
		"Press Enter to accept a value, or type another.\n",
		m.MemoryMB(), m.CPUs, m.Source, memoryMB, cpus, limit)
}

// askNumber asks until the answer is accepted: Enter takes the proposal, an unreadable or
// out-of-bounds answer is refused with the bound named and asked again. When input ends the
// proposal stands, so a closed stdin never loops.
func askNumber(deps Deps, out io.Writer, prompt string, proposal int, parse func(string) (int, bool), refuse func(int) string) int {
	for {
		fmt.Fprint(out, prompt)
		line, err := readLine(deps.Stdin)
		answer := strings.TrimSpace(line)
		if answer == "" {
			if err != nil {
				fmt.Fprintln(out)
			}
			return proposal
		}
		v, ok := parse(answer)
		reason := fmt.Sprintf("%q is not a whole number", answer)
		if ok {
			reason = refuse(v)
		}
		if reason == "" {
			return v
		}
		fmt.Fprintln(out, "  refused: "+reason)
		if err != nil {
			fmt.Fprintf(out, "  no more input; keeping %d\n", proposal)
			return proposal
		}
	}
}

// parseMemoryMB reads megabytes typed at the prompt: 8192, 8192m, 8g, 8 GB.
func parseMemoryMB(s string) (int, bool) {
	s = strings.ToLower(strings.ReplaceAll(s, " ", ""))
	s = strings.TrimSuffix(s, "b")
	if n, err := strconv.Atoi(s); err == nil {
		return n, n >= 0
	}
	if n := instance.MemoryMB(s); n > 0 {
		return n, true
	}
	return 0, false
}

func parseCount(s string) (int, bool) {
	n, err := strconv.Atoi(s)
	return n, err == nil && n >= 0
}

// saveSize writes only memory and cpus into yawble's config file, read without the environment
// so a YAWBLE_* override in force now is not written into it.
func saveSize(deps Deps, memoryMB, cpus int) error {
	raw, _, err := config.Load(deps.ConfigDir, func(string) string { return "" })
	if err != nil {
		return err
	}
	raw.Memory, raw.CPUs = fmt.Sprintf("%dm", memoryMB), cpus
	_, err = config.Save(deps.ConfigDir, raw)
	return err
}

// overEngine names each configured value above what the engine has now, with the fix. A value
// the engine cannot give is warned about, never silently used.
func overEngine(c config.Config, m instance.Machine) []string {
	if !m.Measured {
		return nil
	}
	var warnings []string
	if mb := instance.MemoryMB(c.Memory); mb > m.MemoryMB() {
		warnings = append(warnings, fmt.Sprintf("memory %s in yawble's config is more than the engine has (%d MB, %s); the container cannot get it: yawble config set memory %dm or less, then yawble up", c.Memory, m.MemoryMB(), m.Source, m.MemoryMB()))
	}
	if c.CPUs > m.CPUs {
		warnings = append(warnings, fmt.Sprintf("cpus %d in yawble's config is more than the engine has (%d, %s); the container cannot get them: yawble config set cpus %d or less, then yawble up", c.CPUs, m.CPUs, m.Source, m.CPUs))
	}
	return warnings
}
