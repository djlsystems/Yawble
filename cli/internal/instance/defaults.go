// Package instance is what the verbs do to an engine: which objects exist, in which order they
// are made, what is idempotent. Nothing here formats for a terminal beyond one-line progress.
package instance

import (
	"fmt"
	"strconv"
	"strings"

	"github.com/djlsystems/yawble/cli/internal/config"
)

const (
	PodName       = "yawble"
	VolumeName    = "yawble-data"
	ContainerName = "yawble"
	// ContainerPort is what the Host listens on inside the container (ASPNETCORE_URLS in the image).
	ContainerPort = 8080
)

// Settings is what one run uses, every default already applied. EnvFileHash is a digest of the
// env file's content (never the content), so a changed key is a changed setting without the
// keys themselves ever landing in a label or a log. Memory and CPUs are each worker's; control
// has its own fixed allowance (ControlMemory, ControlCPUs).
type Settings struct {
	Port        int
	Memory      string
	CPUs        int
	MaxRunning  int
	Image       string
	EnvFile     string
	EnvFileHash string
	// Workers is how many worker containers run beside control, 1 or more.
	Workers int
	// WorkerImage is the config's override of the worker image; "" derives it from Image.
	WorkerImage string
	// KeyFile is the worker key's env file and KeyHash a digest of the key (never the key), so a
	// new key replaces the containers. Empty until `up` has made the key.
	KeyFile, KeyHash string
	// RunsShown is how many runs at once the first up's size screen just told the person, so the
	// started line repeats that number; 0 on every other up.
	RunsShown int
}

// EnvFiles are the env files every instance container is given, in order: the env file, then
// the worker key's.
func (s Settings) EnvFiles() []string {
	var files []string
	for _, f := range []string{s.EnvFile, s.KeyFile} {
		if f != "" {
			files = append(files, f)
		}
	}
	return files
}

// Machine is what the engine the container runs in has: Docker's VM or host, the Podman
// machine, or this computer under Podman on Linux. Measured=false means the engine could not
// say, and Defaults then says which constants it used instead. Source names where the figures
// came from ("docker info", "podman machine", "this computer"); Kind says how to give it more.
type Machine struct {
	MemoryBytes int64
	CPUs        int
	Measured    bool
	Source      string
	Kind        EngineKind
}

// MemoryMB is the machine's memory in megabytes.
func (m Machine) MemoryMB() int { return int(m.MemoryBytes >> 20) }

// Memory is expressed in MEGABYTES so a small board is not rounded to "0g" (Important 9 of
// the first review). The cap is 12 GB; a machine below 2 GB gets half of what it has.
const (
	memoryCapBytes   = int64(12) << 30
	cpuCap           = 8
	unmeasuredMemory = "8192m"
	unmeasuredCPUs   = 4
)

// Defaults turns the config and the machine into the settings a run uses. Every derived value
// comes from a measurement; when there is none, a stated constant is used and a note says so,
// because a number that looks measured and was not is the thing this product refuses to do.
func Defaults(c config.Config, m Machine, pinned string) (Settings, []string) {
	var notes []string
	s := Settings{Port: config.DefaultPort, Memory: c.Memory, CPUs: c.CPUs, MaxRunning: c.MaxRunning, Image: c.Image, Workers: c.WorkerCount(), WorkerImage: c.WorkerImage}
	if c.Port != 0 {
		s.Port = c.Port
	}
	if s.Image == "" {
		s.Image = pinned
	}
	memoryMB, cpus := Proposed(m)
	if s.Memory == "" {
		if m.Measured {
			s.Memory = fmt.Sprintf("%dm", memoryMB)
		} else {
			s.Memory = unmeasuredMemory
			notes = append(notes, "memory: not measured - the engine could not say how much it has; using "+unmeasuredMemory+" (yawble config set memory <size> to choose)")
		}
	}
	if s.CPUs == 0 {
		if m.Measured {
			s.CPUs = cpus
		} else {
			s.CPUs = unmeasuredCPUs
			notes = append(notes, fmt.Sprintf("cpus: not measured - the engine could not say how many it has; using %d (yawble config set cpus <n> to choose)", unmeasuredCPUs))
		}
	}
	// maxRunning 0 stays 0: the Host's own default applies, and `up` passes no Wip__MaxRunning,
	// so that default is the Host's to name (`yawble doctor` reads it; nothing here computes it).
	return s, notes
}

// Proposed is the one rule for every engine, for each worker: half the engine's memory, capped
// at 12 GB and at what is left beside control's allowance, and the engine's CPUs, capped at 8.
// An engine too small to hold control beside anything keeps the half. Zero for a machine that
// was not measured.
func Proposed(m Machine) (memoryMB, cpus int) {
	if !m.Measured {
		return 0, 0
	}
	half := m.MemoryBytes / 2
	if half > memoryCapBytes {
		half = memoryCapBytes
	}
	if left := m.MemoryBytes - int64(ControlMemoryMB())<<20; left > 0 && left < half {
		half = left
	}
	cpus = m.CPUs
	if cpus > cpuCap {
		cpus = cpuCap
	}
	return int(half >> 20), cpus
}

// MemoryMB reads a --memory size (12288m, 6g, 512M) as megabytes; 0 when unset or unreadable.
func MemoryMB(size string) int {
	size = strings.TrimSpace(size)
	if size == "" {
		return 0
	}
	unit := size[len(size)-1]
	n, err := strconv.Atoi(size[:len(size)-1])
	if err != nil || n < 0 {
		return 0
	}
	switch unit {
	case 'm', 'M':
		return n
	case 'g', 'G':
		return n * 1024
	case 'k', 'K':
		return n / 1024
	}
	return 0
}
