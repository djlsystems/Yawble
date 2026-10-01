package instance

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
