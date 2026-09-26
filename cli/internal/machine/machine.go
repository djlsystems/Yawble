// Package machine reads and drives the Podman machine, the Linux VM Podman runs in on macOS and
// Windows. Everything goes through the engine's Runner, so each command line is scripted in
// tests and the live machine on the developer's computer is never touched by a test.
package machine

import (
	"context"
	"errors"
	"fmt"
	"strconv"
	"strings"

	"github.com/djlsystems/yawble/cli/internal/engine"
)

const DefaultName = "podman-machine-default"

// Info is what could be measured. Applies is false on Linux, where there is no machine.
type Info struct {
	Applies      bool
	Exists       bool
	Running      bool
	Rootful      bool
	Name         string
	CPUs         int
	MemoryMB     int    // the real bound: wsl free -m on Windows, the machine's setting on macOS
	MemorySource string // "wsl", "machine", or "" when not measured
	WSL          *bool  // Windows only: whether WSL answers at all
}

// ErrNoMachine names the state where Podman is installed and no machine has been created.
var ErrNoMachine = errors.New("no podman machine exists")

// inspectFormat is one line: name|state|rootful|memoryMB|cpus. Measured on Podman 6.0.2.
const inspectFormat = "{{.Name}}|{{.State}}|{{.Rootful}}|{{.Resources.Memory}}|{{.Resources.CPUs}}"

// Inspect answers the machine's facts. A machine that does not exist is not an error: it is
// Exists false, and `up` creates it. A podman that cannot answer at all is an error carrying
// its sentence, because "create the machine" would be the wrong advice for a broken install.
func Inspect(ctx context.Context, r engine.Runner, goos string) (Info, error) {
	if goos != "darwin" && goos != "windows" {
		return Info{}, nil
	}
	info := Info{Applies: true}
	if goos == "windows" {
		if res, err := r.Run(ctx, "wsl", "--status"); err == nil {
			ok := res.ExitCode == 0
			info.WSL = &ok
		} else {
			no := false
			info.WSL = &no
		}
	}

	res, err := r.Run(ctx, "podman", "machine", "inspect", "--format", inspectFormat)
	if err != nil {
		return info, &engine.NotRunnable{Err: err}
	}
	if res.ExitCode != 0 {
		lower := strings.ToLower(res.Stderr)
		if strings.Contains(lower, "does not exist") || strings.Contains(lower, "no such") || strings.Contains(lower, "not found") {
			return info, nil
		}
		return info, fmt.Errorf("podman machine inspect: %s (exit %d)", strings.TrimSpace(res.Stderr), res.ExitCode)
	}
	fields := strings.Split(strings.TrimSpace(strings.SplitN(res.Stdout, "\n", 2)[0]), "|")
	if len(fields) != 5 {
		return info, fmt.Errorf("podman machine inspect answered %q, not the five fields expected", strings.TrimSpace(res.Stdout))
	}
	info.Exists = true
	info.Name = fields[0]
	info.Running = strings.EqualFold(fields[1], "running")
	info.Rootful = strings.EqualFold(fields[2], "true")
	if mb, err := strconv.Atoi(fields[3]); err == nil && goos == "darwin" {
		// On macOS the machine's own setting is the bound. On Windows it is a stale default that
		// WSL ignores, so it is not reported as measured.
		info.MemoryMB, info.MemorySource = mb, "machine"
	}
	if cpus, err := strconv.Atoi(fields[4]); err == nil {
		info.CPUs = cpus
	}
	if goos == "windows" && info.Running {
		if mb := wslMemoryMB(ctx, r, info.Name); mb > 0 {
			info.MemoryMB, info.MemorySource = mb, "wsl"
		}
		// Podman's CPU figure is a default WSL ignores, like its memory one (measured: 2 in a
		// 4-processor VM). The machine's own count wins; podman's stands only when it cannot be read.
		if n := wslCPUs(ctx, r, info.Name); n > 0 {
			info.CPUs = n
		}
	}
	return info, nil
}

// wslMemoryMB is the VM's total memory as the VM itself reports it, or 0 when it cannot be read.
func wslMemoryMB(ctx context.Context, r engine.Runner, name string) int {
	res, err := r.Run(ctx, "wsl", "-d", name, "-e", "free", "-m")
	if err != nil || res.ExitCode != 0 {
		return 0
	}
	for _, line := range strings.Split(res.Stdout, "\n") {
		fields := strings.Fields(line)
		if len(fields) >= 2 && fields[0] == "Mem:" {
			if mb, err := strconv.Atoi(fields[1]); err == nil {
				return mb
			}
		}
	}
	return 0
}

// wslCPUs is the processor count the VM itself sees, or 0 when it cannot be read.
func wslCPUs(ctx context.Context, r engine.Runner, name string) int {
	res, err := r.Run(ctx, "wsl", "-d", name, "-e", "nproc")
	if err != nil || res.ExitCode != 0 {
		return 0
	}
	n, err := strconv.Atoi(strings.TrimSpace(res.Stdout))
	if err != nil || n <= 0 {
		return 0
	}
	return n
}

func run(ctx context.Context, r engine.Runner, args ...string) error {
	res, err := r.Run(ctx, "podman", args...)
	if err != nil {
		return &engine.NotRunnable{Err: err}
	}
	if res.ExitCode != 0 {
		return fmt.Errorf("podman %s: %s (exit %d)", strings.Join(args[:2], " "), strings.TrimSpace(res.Stderr), res.ExitCode)
	}
	return nil
}

// Init creates the machine rootless. Memory and CPUs are passed only when chosen: on Windows the
// WSL VM ignores them, and on macOS podman's defaults stand until a person chooses.
func Init(ctx context.Context, r engine.Runner, memoryMB, cpus int) error {
	args := []string{"machine", "init", "--rootful=false"}
	if memoryMB > 0 {
		args = append(args, "--memory", strconv.Itoa(memoryMB))
	}
	if cpus > 0 {
		args = append(args, "--cpus", strconv.Itoa(cpus))
	}
	return run(ctx, r, args...)
}

func Start(ctx context.Context, r engine.Runner) error { return run(ctx, r, "machine", "start") }

func Stop(ctx context.Context, r engine.Runner) error { return run(ctx, r, "machine", "stop") }

// SetRootless flips the machine to rootless. The machine must be stopped; the caller stops it
// first and starts it after, and says so, because the connection default alone is put back to
// rootful on every machine start.
func SetRootless(ctx context.Context, r engine.Runner) error {
	return run(ctx, r, "machine", "set", "--rootful=false")
}

// HostMemoryMB is the computer's own RAM in MB, which bounds what the machine can be given:
// CIM on Windows, sysctl on macOS. 0 on Linux, where there is no machine to size, and whenever
// it cannot be read: not measured, never guessed.
func HostMemoryMB(ctx context.Context, r engine.Runner, goos string) int {
	var res engine.Result
	var err error
	switch goos {
	case "windows":
		res, err = r.Run(ctx, "powershell", "-NoProfile", "-NonInteractive", "-Command", "(Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory")
	case "darwin":
		res, err = r.Run(ctx, "sysctl", "-n", "hw.memsize")
	default:
		return 0
	}
	if err != nil || res.ExitCode != 0 {
		return 0
	}
	bytes, err := strconv.ParseInt(strings.TrimSpace(res.Stdout), 10, 64)
	if err != nil || bytes <= 0 {
		return 0
	}
	return int(bytes >> 20)
}

// WSLAnswers says whether WSL is installed and answers `wsl --status`. `up` asks it on Windows
// before installing anything, because a missing WSL needs a restart and is the person's first step.
func WSLAnswers(ctx context.Context, r engine.Runner) bool {
	res, err := r.Run(ctx, "wsl", "--status")
	return err == nil && res.ExitCode == 0
}
