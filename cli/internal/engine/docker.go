package engine

import (
	"context"
	"fmt"
	"strings"
)

// docker is the second implementation of Engine. Docker has no pods, so the "pod" is a
// user-defined network the containers share by name and the port is published on the Host
// container itself. The verbs it spells as Podman does are on base; here is what differs.
type docker struct{ base }

func NewDocker(r Runner) Engine { return docker{base{program: "docker", r: r}} }

const dockerInspectFormat = "{{.State.Status}}|{{.Config.Image}}|{{index .Config.Labels \"" + SettingsLabel + "\"}}"

// exists is `docker <kind> inspect <name>`: Docker has no `exists` verb, and inspect exits 1 for
// a missing object on stderr as "No such ..." or, from Docker 29 for a network, "... not found".
// Any other failure is an error.
func (d docker) exists(ctx context.Context, kind, name string) (bool, error) {
	res, err := d.r.Run(ctx, "docker", kind, "inspect", name)
	if err != nil {
		return false, &NotRunnable{Program: "docker", Err: err}
	}
	if res.ExitCode == 0 {
		return true, nil
	}
	if missing(res.Stderr) {
		return false, nil
	}
	return false, fmt.Errorf("docker %s inspect %s: %s (exit %d)", kind, name, strings.TrimSpace(res.Stderr), res.ExitCode)
}

// missing is Docker saying the object does not exist, in either wording it uses.
func missing(stderr string) bool {
	lower := strings.ToLower(stderr)
	return strings.Contains(lower, "no such") || strings.Contains(lower, "not found")
}

func (d docker) VolumeExists(ctx context.Context, name string) (bool, error) {
	return d.exists(ctx, "volume", name)
}

func (d docker) PodExists(ctx context.Context, name string) (bool, error) {
	return d.exists(ctx, "network", name)
}

// CreatePod makes the network the Host and the tunnel sidecar share. The port is published by
// Run, since a Docker network publishes nothing.
// PodCurrent: the network has no layout that changed, so every one is current.
func (docker) PodCurrent(context.Context, string) (bool, error) { return true, nil }

func (d docker) CreatePod(ctx context.Context, name string, _, _ int, _ ...int) error {
	_, err := d.run(ctx, "network", "create", name)
	return err
}

// StopPod and PodRunning: a Docker network runs no process and holds no port.
func (docker) StopPod(context.Context, string) error { return nil }

func (docker) PodRunning(context.Context, string) (bool, error) { return false, nil }

func (d docker) RemovePod(ctx context.Context, name string) error {
	_, err := d.run(ctx, "network", "rm", name)
	return err
}

func (d docker) ContainerState(ctx context.Context, name string) (State, error) {
	info, err := d.Inspect(ctx, name)
	return info.State, err
}

func (d docker) Inspect(ctx context.Context, name string) (ContainerInfo, error) {
	res, err := d.r.Run(ctx, "docker", "container", "inspect", "--format", dockerInspectFormat, name)
	if err != nil {
		return ContainerInfo{State: StateAbsent}, &NotRunnable{Program: "docker", Err: err}
	}
	if res.ExitCode != 0 {
		if missing(res.Stderr) {
			return ContainerInfo{State: StateAbsent}, nil
		}
		return ContainerInfo{State: StateAbsent}, fmt.Errorf("docker container inspect %s: %s (exit %d)", name, strings.TrimSpace(res.Stderr), res.ExitCode)
	}
	return parseInspect(res.Stdout), nil
}

// Health reads `.State.Health`, which Docker leaves nil for an image with no HEALTHCHECK: the
// guard keeps that from being a template error. A missing container is none.
func (d docker) Health(ctx context.Context, name string) (Health, error) {
	res, err := d.r.Run(ctx, "docker", "container", "inspect", "--format", "{{if .State.Health}}{{.State.Health.Status}}{{end}}", name)
	if err != nil {
		return HealthNone, &NotRunnable{Program: "docker", Err: err}
	}
	if res.ExitCode != 0 {
		if missing(res.Stderr) {
			return HealthNone, nil
		}
		return HealthNone, fmt.Errorf("docker container inspect %s: %s (exit %d)", name, strings.TrimSpace(res.Stderr), res.ExitCode)
	}
	return healthOf(res.Stdout), nil
}

// Run joins the pod's network, or with Network another container's namespace; a container that
// joins one publishes nothing (Docker refuses -p there), the namespace's owner does.
func (d docker) Run(ctx context.Context, s RunSpec) error {
	network := s.Pod
	if s.Network != "" {
		network = s.Network
	}
	args := []string{"run", "-d", "--name", s.Name, "--network", network}
	if s.HostPort > 0 && s.ContainerPort > 0 && !strings.HasPrefix(network, "container:") {
		args = append(args, "-p", fmt.Sprintf("0.0.0.0:%d:%d", s.HostPort, s.ContainerPort))
		for _, port := range s.PreviewPorts {
			args = append(args, "-p", fmt.Sprintf("0.0.0.0:%d:%d", port, port))
		}
	}
	_, err := d.run(ctx, append(args, runArgs(s)...)...)
	return err
}

func (d docker) ImagePresent(ctx context.Context, ref string) (bool, error) {
	return d.exists(ctx, "image", ref)
}

func (d docker) RemoveImage(ctx context.Context, ref string) error {
	_, err := d.run(ctx, "rmi", ref)
	return err
}
