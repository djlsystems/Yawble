//go:build linux

package instance

import (
	"bufio"
	"os"
	"runtime"
	"strconv"
	"strings"
)

// Measure reads MemTotal from /proc/meminfo and the CPU count from the runtime. Anything it
// cannot read leaves Measured false rather than half-filling the struct.
func Measure() Machine {
	f, err := os.Open("/proc/meminfo")
	if err != nil {
		return Machine{}
	}
	defer f.Close()
	sc := bufio.NewScanner(f)
	for sc.Scan() {
		fields := strings.Fields(sc.Text())
		if len(fields) >= 2 && fields[0] == "MemTotal:" {
			kb, err := strconv.ParseInt(fields[1], 10, 64)
			if err != nil {
				return Machine{}
			}
			return Machine{MemoryBytes: kb * 1024, CPUs: runtime.NumCPU(), Measured: true}
		}
	}
	return Machine{}
}
