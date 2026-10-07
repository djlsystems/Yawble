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
// rounded up to 4 GB. A proposal below it (an engine under 8 GB) lowers the floor to the
// proposal, so the default the prompt offers is always one it accepts.
const memoryFloorMB = 4096

// cpuFloor is the fewest CPUs the first-up screen accepts.
const cpuFloor = 1

// floors is the least memory and CPUs the first-up prompt accepts for this proposal: the fixed
// floors, lowered to the proposal where it is below them, so Enter never saves a value the same
// prompt would refuse if typed.
func floors(memoryMB, cpus int) (int, int) {
	return min(memoryFloorMB, memoryMB), min(cpuFloor, cpus)
}

// engineCapacity is what the engine the container runs in has, asked of that engine: `docker
// info` under Docker on every OS (Docker Desktop's VM, or the Linux host), the Podman machine on
// macOS and Windows, and this computer under Podman on Linux. An engine that cannot answer is
// not measured; nothing is estimated in its place.
func engineCapacity(ctx context.Context, deps Deps, name string) instance.Machine {
	if name == "docker" {
		return dockerMachine(ctx, runnerOf(deps), goosOf(deps))
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
// config holds neither. At a terminal it shows what the engine has, whose share of this computer
// that is, the proposal and how many agent runs it lets work at once, and asks for each value;
// with --yes or no terminal it takes the proposal and says so. Either way the answer is saved, so
// later ups do not ask, and the number of runs it said is returned for the started line to repeat
// (0 when it said none). An engine that was not measured is not asked about: Defaults says so,
// and nothing is saved.
func sizeFirstUp(deps Deps, c config.Config, m instance.Machine, yes bool, out io.Writer) (config.Config, int, error) {
	if c.Memory != "" || c.CPUs != 0 || !m.Measured {
		return c, 0, nil
	}
	memoryMB, cpus := instance.Proposed(m)
	changeHint := "yawble config set memory <size> and yawble config set cpus <n> change them, then yawble up"
	if yes || !deps.Interactive || deps.Stdin == nil {
		if err := saveSize(deps, memoryMB, cpus); err != nil {
			return c, 0, err
		}
		runs, sentence := instance.RunsSentence(memoryMB, cpus, c.WorkerCount(), c.MaxRunning)
		fmt.Fprintf(out, "memory %dm and cpus %d for the container: half of the %d MB and the %d CPUs available (%s), at most 12 GB and 8 CPUs; saved in yawble's config (%s)\n%s\n%s\n",
			memoryMB, cpus, m.MemoryMB(), m.CPUs, instance.EngineShare(m.Kind), changeHint, capitalize(instance.MoreForEngine(m.Kind)), sentence)
		c.Memory, c.CPUs = fmt.Sprintf("%dm", memoryMB), cpus
		return c, runs, nil
	}

	floor, leastCPUs := floors(memoryMB, cpus)
	most := workerMemoryMax(m)
	fmt.Fprint(out, firstUpScreen(m, memoryMB, cpus, c.WorkerCount(), c.MaxRunning))
	more := capitalize(instance.MoreForEngine(m.Kind))
	share := instance.ShareOf(m.Kind)
	memoryMB = askNumber(deps, out, fmt.Sprintf("Memory in MB (%d to %d) [%d]: ", floor, most, memoryMB), memoryMB,
		parseMemoryMB, func(v int) string {
			switch {
			case v > most && most < m.MemoryMB():
				return fmt.Sprintf("%d MB, with control's %d MB, is more than %s; the most is %d MB. %s", v, instance.ControlMemoryMB(), share, most, more)
			case v > most:
				return fmt.Sprintf("%d MB is more than %s; the most is %d MB. %s", v, share, most, more)
			case v < floor && floor < memoryFloorMB:
				return fmt.Sprintf("%d MB is below the floor of %d MB, the proposal for an engine under 8 GB; the least is %d MB", v, floor, floor)
			case v < floor:
				return fmt.Sprintf("%d MB is below the floor of %d MB that leaves the Host a usable share; the least is %d MB", v, floor, floor)
			}
			return ""
		})
	cpus = askNumber(deps, out, fmt.Sprintf("CPUs (%d to %d) [%d]: ", leastCPUs, m.CPUs, cpus), cpus,
		parseCount, func(v int) string {
			switch {
			case v > m.CPUs:
				return fmt.Sprintf("%d CPUs is more than %s; the most is %d. %s", v, share, m.CPUs, more)
			case v < leastCPUs:
				return fmt.Sprintf("the least is %d CPU", leastCPUs)
			}
			return ""
		})
	if err := saveSize(deps, memoryMB, cpus); err != nil {
		return c, 0, err
	}
	runs, sentence := instance.RunsSentence(memoryMB, cpus, c.WorkerCount(), c.MaxRunning)
	fmt.Fprintf(out, "saved memory %dm and cpus %d in yawble's config (%s)\n%s\n", memoryMB, cpus, changeHint, sentence)
	c.Memory, c.CPUs = fmt.Sprintf("%dm", memoryMB), cpus
	return c, runs, nil
}

// workerMemoryMax is the most memory a worker may ask for on the first up: the engine's, less
// control's fixed allowance. An engine too small for that keeps its whole memory as the most.
func workerMemoryMax(m instance.Machine) int {
	if most := m.MemoryMB() - instance.ControlMemoryMB(); most > 0 {
		return most
	}
	return m.MemoryMB()
}

// firstUpScreen is the one short screen before the questions: what is available and whose share
// of this computer it is, where to give it more, the proposal, and in one sentence how many agent
// runs that lets work at once. That number is the only CLI-derived limit anywhere; `doctor`
// reports the Host's own answer.
func firstUpScreen(m instance.Machine, memoryMB, cpus, workers, maxRunning int) string {
	_, sentence := instance.RunsSentence(memoryMB, cpus, workers, maxRunning)
	return fmt.Sprintf("How much of this computer should Yawble's worker get? (asked once)\n"+
		"  available        %d MB memory and %d CPUs: %s\n"+
		"                   %s\n"+
		"  control takes    %d MB memory, %d CPUs (fixed), so a worker may have up to %d MB\n"+
		"  proposed         %d MB memory, %d CPUs (half the memory available, up to 12 GB; the CPUs, up to 8)\n"+
		"%s\n"+
		"Press Enter to accept a value, or type another.\n",
		m.MemoryMB(), m.CPUs, instance.EngineShare(m.Kind), capitalize(instance.MoreForEngine(m.Kind)),
		instance.ControlMemoryMB(), instance.ControlCPUs, workerMemoryMax(m), memoryMB, cpus, sentence)
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

func capitalize(s string) string {
	if s == "" {
		return s
	}
	return strings.ToUpper(s[:1]) + s[1:]
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
		warnings = append(warnings, fmt.Sprintf("memory %s in yawble's config is more than %s (%d MB); the container cannot get it: yawble config set memory %dm or less, then yawble up; or %s", c.Memory, instance.ShareOf(m.Kind), m.MemoryMB(), m.MemoryMB(), instance.MoreForEngine(m.Kind)))
	}
	if c.CPUs > m.CPUs {
		warnings = append(warnings, fmt.Sprintf("cpus %d in yawble's config is more than %s (%d CPUs); the container cannot get them: yawble config set cpus %d or less, then yawble up; or %s", c.CPUs, instance.ShareOf(m.Kind), m.CPUs, m.CPUs, instance.MoreForEngine(m.Kind)))
	}
	return warnings
}
