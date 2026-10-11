package engine

// Select names the engine to use: the configured one when set, else Podman if it is on PATH,
// else Docker if it is, else "" (neither installed: `up` says where to get one). A configured
// engine wins even when absent, because an explicit choice is not a guess to correct.
func Select(configured string, lookPath func(string) (string, error)) string {
	if configured != "" {
		return configured
	}
	if lookPath == nil {
		return "podman"
	}
	for _, name := range []string{"podman", "docker"} {
		if _, err := lookPath(name); err == nil {
			return name
		}
	}
	return ""
}

// New is the engine for a name. Anything but "docker" is Podman, the default.
func New(name string, r Runner) Engine { return NewFor(name, r, "") }

// NewFor is New for an operating system: on Windows, Podman's pod is made IPv4-only so WSL
// relays its port to localhost (see podman.CreatePod).
func NewFor(name string, r Runner, goos string) Engine {
	if name == "docker" {
		return NewDocker(r)
	}
	return podman{r: r, ipv4Pod: goos == "windows"}
}
