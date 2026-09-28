package engine

import (
	"context"
	"fmt"
	"io"
	"strconv"
	"strings"
	"time"
)

// docker is the second implementation of Engine. Docker has no pods, so the "pod" is a
// user-defined network the containers share by name and the port is published on the Host
// container itself. Everything else is the same verbs with a different program name.
type docker struct{ r Runner }

func NewDocker(r Runner) Engine { return docker{r} }

func (docker) Name() string { return "docker" }

const dockerInspectFormat = "{{.State.Status}}|{{.Config.Image}}|{{index .Config.Labels \"" + SettingsLabel + "\"}}"

func (d docker) run(ctx context.Context, args ...string) (Result, error) {
	res, err := d.r.Run(ctx, "docker", args...)
	if err != nil {
		return res, &NotRunnable{Err: err}
	}
	if res.ExitCode != 0 {
		return res, fmt.Errorf("docker %s: %s (exit %d)", strings.Join(args[:min(2, len(args))], " "), strings.TrimSpace(res.Stderr), res.ExitCode)
	}
	return res, nil
}

// exists is `docker <kind> inspect <name>`: Docker has no `exists` verb, and inspect exits 1 for
// a missing object on stderr as "No such ..." or, from Docker 29 for a network, "... not found".
// Any other failure is an error.
func (d docker) exists(ctx context.Context, kind, name string) (bool, error) {
	res, err := d.r.Run(ctx, "docker", kind, "inspect", name)
	if err != nil {
		return false, &NotRunnable{Err: err}
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

func (d docker) Version(ctx context.Context) (string, error) {
	res, err := d.run(ctx, "version", "--format", "{{.Client.Version}}")
	return strings.TrimSpace(res.Stdout), err
}

func (d docker) VolumeExists(ctx context.Context, name string) (bool, error) {
	return d.exists(ctx, "volume", name)
}

func (d docker) CreateVolume(ctx context.Context, name string) error {
	_, err := d.run(ctx, "volume", "create", name)
	return err
}

func (d docker) PodExists(ctx context.Context, name string) (bool, error) {
	return d.exists(ctx, "network", name)
}

// CreatePod makes the network the Host and the tunnel sidecar share. The port is published by
// Run, since a Docker network publishes nothing.
// PodCurrent: the network has no layout that changed, so every one is current.
func (docker) PodCurrent(context.Context, string) (bool, error) { return true, nil }

func (d docker) CreatePod(ctx context.Context, name string, _, _ int) error {
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
		return ContainerInfo{State: StateAbsent}, &NotRunnable{Err: err}
	}
	if res.ExitCode != 0 {
		if missing(res.Stderr) {
			return ContainerInfo{State: StateAbsent}, nil
		}
		return ContainerInfo{State: StateAbsent}, fmt.Errorf("docker container inspect %s: %s (exit %d)", name, strings.TrimSpace(res.Stderr), res.ExitCode)
	}
	return parseInspect(res.Stdout), nil
}

func (d docker) Remove(ctx context.Context, name string) error {
	_, err := d.run(ctx, "rm", "-f", name)
	return err
}

func (d docker) Exec(ctx context.Context, name string, args ...string) (Result, error) {
	return d.run(ctx, append([]string{"exec", name}, args...)...)
}

func (d docker) ExecTo(ctx context.Context, name string, stdout io.Writer, args ...string) (Result, error) {
	return execTo(ctx, d.r, "docker", name, stdout, args)
}

func (d docker) CopyTo(ctx context.Context, name, src, dst string) error {
	_, err := d.run(ctx, "cp", src, name+":"+dst)
	return err
}

func (d docker) Run(ctx context.Context, s RunSpec) error {
	args := []string{"run", "-d", "--name", s.Name, "--network", s.Pod}
	if s.HostPort > 0 && s.ContainerPort > 0 {
		args = append(args, "-p", fmt.Sprintf("0.0.0.0:%d:%d", s.HostPort, s.ContainerPort))
	}
	args = append(args, "--restart", "unless-stopped")
	if s.Memory != "" {
		args = append(args, "--memory", s.Memory)
	}
	if s.CPUs > 0 {
		args = append(args, "--cpus", strconv.Itoa(s.CPUs))
	}
	for _, k := range sortedKeys(s.Env) {
		args = append(args, "-e", k+"="+s.Env[k])
	}
	for _, k := range sortedKeys(s.Labels) {
		args = append(args, "--label", k+"="+s.Labels[k])
	}
	if s.EnvFile != "" {
		args = append(args, "--env-file", s.EnvFile)
	}
	for _, v := range s.Volumes {
		args = append(args, "-v", v)
	}
	args = append(args, s.Image)
	args = append(args, s.Command...)
	_, err := d.run(ctx, args...)
	return err
}

func (d docker) Start(ctx context.Context, name string) error {
	_, err := d.run(ctx, "start", name)
	return err
}

func (d docker) Stop(ctx context.Context, name string) error {
	_, err := d.run(ctx, "stop", name)
	return err
}

func (d docker) ImagePresent(ctx context.Context, ref string) (bool, error) {
	return d.exists(ctx, "image", ref)
}

func (d docker) Pull(ctx context.Context, ref string, progress io.Writer) error {
	return d.stream(ctx, progress, "pull", ref)
}

func (d docker) Login(ctx context.Context, registry, user, password string) error {
	return login(ctx, d.r, "docker", registry, user, password)
}

func (d docker) RemoveImage(ctx context.Context, ref string) error {
	_, err := d.run(ctx, "rmi", ref)
	return err
}

func (d docker) RemoveVolume(ctx context.Context, name string) error {
	_, err := d.run(ctx, "volume", "rm", name)
	return err
}

func (d docker) FollowSince(ctx context.Context, name string, since time.Time, out io.Writer) error {
	return d.stream(ctx, out, "logs", "--follow", "--since", since.UTC().Format(time.RFC3339), name)
}

func (d docker) Logs(ctx context.Context, name string, follow bool, tail int, out io.Writer) error {
	args := []string{"logs"}
	if follow {
		args = append(args, "--follow")
	}
	if tail > 0 {
		args = append(args, "--tail", strconv.Itoa(tail))
	}
	return d.stream(ctx, out, append(args, name)...)
}

func (d docker) stream(ctx context.Context, out io.Writer, args ...string) error {
	streamer, ok := d.r.(Streamer)
	if !ok {
		res, err := d.run(ctx, args...)
		if err == nil {
			_, _ = io.WriteString(out, res.Stdout)
		}
		return err
	}
	code, err := streamer.Stream(ctx, out, "docker", args...)
	if err != nil {
		return &NotRunnable{Err: err}
	}
	if code != 0 {
		return fmt.Errorf("docker %s exited %d", args[0], code)
	}
	return nil
}

func (d docker) RunHelper(ctx context.Context, spec HelperSpec) (Result, error) {
	return runHelper(ctx, d.r, "docker", spec)
}
