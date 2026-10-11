//go:build !linux

package instance

// Measure on a platform this step does not measure. macOS and Windows get their machine rules
// in later steps (the Podman machine is the bound there, not the host); until then nothing is
// measured and Defaults says so.
func Measure() Machine { return Machine{} }
