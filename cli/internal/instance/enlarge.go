package instance

// EngineKind is what holds the engine's memory and CPUs, which decides how a person gives it
// more: a Podman machine, Docker Desktop's VM, another Docker VM, or a Linux host with no VM.
type EngineKind string

const (
	KindPodmanMachine EngineKind = "podman machine"
	KindDockerDesktop EngineKind = "docker desktop"
	KindDockerVM      EngineKind = "docker vm"
	KindLinux         EngineKind = "linux"
)

// MoreForEngine says how to give this kind of engine more memory and CPUs. It is the one
// wording for the first-up refusal, doctor's warning and a later up's warning, so they cannot
// drift apart. Empty for an engine that was not measured.
func MoreForEngine(kind EngineKind) string {
	switch kind {
	case KindPodmanMachine:
		return "to give the Podman machine more: podman machine stop, then podman machine set --memory <MB> --cpus <n>, then podman machine start"
	case KindDockerDesktop:
		return "to give Docker Desktop more: Docker Desktop's Settings > Resources"
	case KindDockerVM:
		return "to give Docker's VM more: change it in the tool that runs that VM"
	case KindLinux:
		return "on Linux there is no VM: this computer's own RAM and CPUs are the limit, and there is nothing to enlarge"
	}
	return ""
}
