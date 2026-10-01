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
// keys themselves ever landing in a label or a log.
type Settings struct {
	Port        int
	Memory      string
	CPUs        int
	MaxRunning  int
	Image       string
	EnvFile     string
	EnvFileHash string
}

// Machine is what could be measured about this computer. Measured=false means nothing could,
// and Defaults then says which constants it used instead.
type Machine struct {
	MemoryBytes int64
	CPUs        int
	Measured    bool
}

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
	s := Settings{Port: config.DefaultPort, Memory: c.Memory, CPUs: c.CPUs, MaxRunning: c.MaxRunning, Image: c.Image}
	if c.Port != 0 {
		s.Port = c.Port
	}
	if s.Image == "" {
		s.Image = pinned
	}
	if s.Memory == "" {
		if m.Measured {
			half := m.MemoryBytes / 2
			if half > memoryCapBytes {
				half = memoryCapBytes
			}
			s.Memory = fmt.Sprintf("%dm", half>>20)
		} else {
			s.Memory = unmeasuredMemory
			notes = append(notes, "memory: this machine's RAM could not be measured; using "+unmeasuredMemory+" (yawble config set memory <size> to choose)")
		}
	}
	if s.CPUs == 0 {
		if m.Measured {
			s.CPUs = m.CPUs
			if s.CPUs > cpuCap {
				s.CPUs = cpuCap
			}
		} else {
			s.CPUs = unmeasuredCPUs
			notes = append(notes, fmt.Sprintf("cpus: this machine's CPU count could not be measured; using %d (yawble config set cpus <n> to choose)", unmeasuredCPUs))
		}
	}
	// maxRunning 0 stays 0: the Host's own default applies, and `up` passes no Wip__MaxRunning,
	// so that default is the Host's to name (`yawble doctor` reads it; nothing here computes it).
	return s, notes
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
