package instance

import "fmt"

// HostDefaultMemoryPerRunMb is the Host's built-in wip.memoryPerRunMb: DefaultMemoryPerRunMb in
// src/Harness.Host/TenantSettings.cs, which TestTheHostsDefaultMemoryPerRunIsTheHostsOwn pins.
// The CLI never reads the live setting; it uses this only on the first-up screen, where the
// running limit is shown as derived, before any Host runs.
const HostDefaultMemoryPerRunMb = 2048

// DerivedRunLimit is the Host's own default rule (TenantSettings.Bounds), applied to a
// container's memory and CPUs: the smaller of max(1, CPUs - 1) and max(1, memory / per-run
// allowance). It is a derivation for the first-up screen; `doctor` reports the Host's answer.
func DerivedRunLimit(memoryMB, cpus, memoryPerRunMb int) int {
	cpuBound := cpus - 1
	if cpuBound < 1 {
		cpuBound = 1
	}
	if memoryPerRunMb < 1 {
		memoryPerRunMb = 1
	}
	memoryBound := memoryMB / memoryPerRunMb
	if memoryBound < 1 {
		memoryBound = 1
	}
	if memoryBound < cpuBound {
		return memoryBound
	}
	return cpuBound
}

// RunsSentence is how many agent runs can work at once, and why, in one sentence for a person:
// the number they configured, or the Host's default rule applied to each worker's memory and
// CPUs at the default allowance, summed over the workers as the Host sums them. The first up
// says it and its started line repeats the number, so the two cannot disagree.
func RunsSentence(memoryMB, cpus, workers, maxRunning int) (int, string) {
	if maxRunning > 0 {
		return maxRunning, fmt.Sprintf("Up to %s can work at once: the number you set with yawble config set maxRunning.", runs(maxRunning))
	}
	workers = max(workers, 1)
	each := DerivedRunLimit(memoryMB, cpus, HostDefaultMemoryPerRunMb)
	total := each * workers
	whose, spread := "the worker has", ""
	if workers > 1 {
		whose, spread = "each worker has", fmt.Sprintf(", %d on each of your %d workers", each, workers)
	}
	var why string
	switch {
	case max(1, memoryMB/HostDefaultMemoryPerRunMb) < max(1, cpus-1):
		why = fmt.Sprintf("each run is given %d MB of memory and %s %d MB", HostDefaultMemoryPerRunMb, whose, memoryMB)
	case cpus <= 1:
		why = fmt.Sprintf("%s 1 CPU", whose)
	default:
		why = fmt.Sprintf("%s %d CPUs and one is kept free for Yawble itself", whose, cpus)
	}
	return total, fmt.Sprintf("Up to %s can work at once%s, because %s.", runs(total), spread, why)
}

func runs(n int) string {
	if n == 1 {
		return "1 agent run"
	}
	return fmt.Sprintf("%d agent runs", n)
}
