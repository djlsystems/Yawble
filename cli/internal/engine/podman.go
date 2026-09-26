package engine

import (
	"context"
	"fmt"
	"io"
	"sort"
	"strconv"
	"strings"
	"time"
)

// podman maps each Engine method to one command line. The lines ARE the contract: the tests in
// podman_test.go and the instance tests assert them verbatim.
type podman struct {
	r Runner
	// ipv4Pod: the pod's network is pasta with -4 (Windows). See CreatePod.
	ipv4Pod bool
}

func NewPodman(r Runner) Engine { return podman{r: r} }

// podLayoutLabel marks a pod made with the Windows layout, so a pod from before it is known.
const (
	podLayoutLabel = "yawble.pod"
	podLayoutIPv4  = "pasta-ipv4"
)

func (podman) Name() string { return "podman" }

// inspectFormat prints three fields on one line. The label is the settings `up` stamped on the
// container; an absent label prints as an empty third field.
const inspectFormat = "{{.State.Status}}|{{.ImageName}}|{{index .Config.Labels \"" + SettingsLabel + "\"}}"

// run is every non-streaming call: one command line, and a non-zero exit becomes an error that
// carries podman's own sentence, because that sentence is usually the whole diagnosis.
func (p podman) run(ctx context.Context, args ...string) (Result, error) {
	res, err := p.r.Run(ctx, "podman", args...)
	if err != nil {
		return res, &NotRunnable{Err: err}
	}
	if res.ExitCode != 0 {
		return res, fmt.Errorf("podman %s: %s (exit %d)", strings.Join(args[:min(2, len(args))], " "), strings.TrimSpace(res.Stderr), res.ExitCode)
	}
	return res, nil
}

// exists is `podman <kind> exists <name>`: exit 0 yes, exit 1 no, anything else is a broken
// engine and is said so rather than read as "no".
func (p podman) exists(ctx context.Context, kind, name string) (bool, error) {
	res, err := p.r.Run(ctx, "podman", kind, "exists", name)
	if err != nil {
		return false, &NotRunnable{Err: err}
	}
	switch res.ExitCode {
	case 0:
		return true, nil
	case 1:
		return false, nil
	}
	return false, fmt.Errorf("podman %s exists %s: %s (exit %d)", kind, name, strings.TrimSpace(res.Stderr), res.ExitCode)
}

func (p podman) Version(ctx context.Context) (string, error) {
	res, err := p.run(ctx, "version", "--format", "{{.Client.Version}}")
	return strings.TrimSpace(res.Stdout), err
}

func (p podman) VolumeExists(ctx context.Context, name string) (bool, error) {
	return p.exists(ctx, "volume", name)
}

func (p podman) CreateVolume(ctx context.Context, name string) error {
	_, err := p.run(ctx, "volume", "create", name)
	return err
}

func (p podman) PodExists(ctx context.Context, name string) (bool, error) {
	return p.exists(ctx, "pod", name)
}

// CreatePod publishes on an explicit IPv4 address. A bare port makes pasta bind a dual-stack
// socket, and on Windows WSL relays IPv4 sockets only; the same form is harmless elsewhere.
// The port is BOUND when the pod's infra container starts, which is the first `run --pod`, not
// here; a taken port therefore surfaces on Run, and Up cleans up accordingly.
//
// ON WINDOWS THE ADDRESS IS NOT ENOUGH. With winget's Podman 5.8.3, pasta still listens dual-stack (`*:8080`) and http://localhost was refused; Podman 6.0.2 honours
// the address. `--network pasta:-4` makes pasta listen on 0.0.0.0 on both, and the container
// keeps IPv4 egress, which is all it uses. The label is how PodCurrent tells an older pod.
func (p podman) CreatePod(ctx context.Context, name string, hostPort, containerPort int) error {
	args := []string{"pod", "create", "--name", name}
	if p.ipv4Pod {
		args = append(args, "--network", "pasta:-4", "--label", podLayoutLabel+"="+podLayoutIPv4)
	}
	args = append(args, "-p", fmt.Sprintf("0.0.0.0:%d:%d", hostPort, containerPort))
	_, err := p.run(ctx, args...)
	return err
}

// PodCurrent says whether an existing pod was made the way CreatePod makes one now. Only the
// Windows layout is checked; anywhere else every pod is current and nothing is run.
func (p podman) PodCurrent(ctx context.Context, name string) (bool, error) {
	if !p.ipv4Pod {
		return true, nil
	}
	out, err := p.run(ctx, "pod", "inspect", name, "--format", `{{index .Labels "`+podLayoutLabel+`"}}`)
	if err != nil {
		return false, err
	}
	return strings.TrimSpace(out.Stdout) == podLayoutIPv4, nil
}

// StopPod stops the pod's infra container, the one that holds the published port. Not `podman pod
// stop`: measured on Podman 6.0.2, that leaves the infra running when the member container has
// already exited, and the pod stays Degraded with the port held. Starting the member container
// later starts the infra again.
func (p podman) StopPod(ctx context.Context, name string) error {
	if running, err := p.PodRunning(ctx, name); err != nil || !running {
		return err
	}
	out, err := p.run(ctx, "pod", "inspect", name, "--format", "{{.InfraContainerID}}")
	if err != nil {
		return err
	}
	if infra := strings.TrimSpace(out.Stdout); infra != "" {
		_, err = p.run(ctx, "stop", infra)
		return err
	}
	_, err = p.run(ctx, "pod", "stop", name)
	return err
}

// PodRunning reads the pod's state: Running, or Degraded while a member container is stopped and
// the infra still holds the port. A missing pod is not running.
func (p podman) PodRunning(ctx context.Context, name string) (bool, error) {
	exists, err := p.PodExists(ctx, name)
	if err != nil || !exists {
		return false, err
	}
	out, err := p.run(ctx, "pod", "inspect", name, "--format", "{{.State}}")
	if err != nil {
		return false, err
	}
	state := strings.ToLower(strings.TrimSpace(out.Stdout))
	return state == "running" || state == "degraded", nil
}

func (p podman) RemovePod(ctx context.Context, name string) error {
	_, err := p.run(ctx, "pod", "rm", "-f", name)
	return err
}

func (p podman) ContainerState(ctx context.Context, name string) (State, error) {
	info, err := p.Inspect(ctx, name)
	return info.State, err
}

// Inspect answers state, image and the settings label in one call. "no such" is Absent; any
// other failure is an error. An answer that is not three fields (an older scripted test, an
// unexpected podman) is read as state only.
func (p podman) Inspect(ctx context.Context, name string) (ContainerInfo, error) {
	res, err := p.r.Run(ctx, "podman", "container", "inspect", "--format", inspectFormat, name)
	if err != nil {
		return ContainerInfo{State: StateAbsent}, &NotRunnable{Err: err}
	}
	if res.ExitCode != 0 {
		if strings.Contains(strings.ToLower(res.Stderr), "no such") {
			return ContainerInfo{State: StateAbsent}, nil
		}
		return ContainerInfo{State: StateAbsent}, fmt.Errorf("podman container inspect %s: %s (exit %d)", name, strings.TrimSpace(res.Stderr), res.ExitCode)
	}
	return parseInspect(res.Stdout), nil
}

// parseInspect reads "state|image|label". An existing container always has a state, so a blank
// answer is read as absent rather than as a stopped container with no name.
func parseInspect(stdout string) ContainerInfo {
	trimmed := strings.TrimSpace(stdout)
	if trimmed == "" {
		return ContainerInfo{State: StateAbsent}
	}
	fields := strings.SplitN(trimmed, "|", 3)
	info := ContainerInfo{State: StateStopped}
	if strings.TrimSpace(fields[0]) == "running" {
		info.State = StateRunning
	}
	if len(fields) == 3 {
		info.Image = strings.TrimSpace(fields[1])
		info.Label = strings.TrimSpace(fields[2])
	}
	return info
}

func (p podman) Remove(ctx context.Context, name string) error {
	_, err := p.run(ctx, "rm", "-f", name)
	return err
}

func (p podman) Exec(ctx context.Context, name string, args ...string) (Result, error) {
	return p.run(ctx, append([]string{"exec", name}, args...)...)
}

func (p podman) Run(ctx context.Context, s RunSpec) error {
	args := []string{"run", "-d", "--name", s.Name, "--pod", s.Pod, "--restart", "unless-stopped"}
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
	_, err := p.run(ctx, args...)
	return err
}

func sortedKeys(m map[string]string) []string {
	keys := make([]string, 0, len(m))
	for k := range m {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	return keys
}

func (p podman) Start(ctx context.Context, name string) error {
	_, err := p.run(ctx, "start", name)
	return err
}

func (p podman) Stop(ctx context.Context, name string) error {
	_, err := p.run(ctx, "stop", name)
	return err
}

func (p podman) ImagePresent(ctx context.Context, ref string) (bool, error) {
	return p.exists(ctx, "image", ref)
}

func (p podman) Pull(ctx context.Context, ref string, progress io.Writer) error {
	return p.stream(ctx, progress, "pull", ref)
}

func (p podman) Login(ctx context.Context, registry, user, password string) error {
	return login(ctx, p.r, "podman", registry, user, password)
}

// RemoveImage removes the image, or the multi-platform manifest list of that name: on the
// machine that cuts releases the name is the list the release built, and `rmi` answers "tag not
// known" for it. Any other failure is reported as it is.
func (p podman) RemoveImage(ctx context.Context, ref string) error {
	_, err := p.run(ctx, "rmi", ref)
	if err != nil && strings.Contains(err.Error(), "tag not known") {
		_, err = p.run(ctx, "manifest", "rm", ref)
	}
	return err
}

func (p podman) RemoveVolume(ctx context.Context, name string) error {
	_, err := p.run(ctx, "volume", "rm", name)
	return err
}

func (p podman) Logs(ctx context.Context, name string, follow bool, tail int, out io.Writer) error {
	args := []string{"logs"}
	if follow {
		args = append(args, "--follow")
	}
	if tail > 0 {
		args = append(args, "--tail", strconv.Itoa(tail))
	}
	return p.stream(ctx, out, append(args, name)...)
}

func (p podman) FollowSince(ctx context.Context, name string, since time.Time, out io.Writer) error {
	return p.stream(ctx, out, "logs", "--follow", "--since", since.UTC().Format(time.RFC3339), name)
}

// stream runs a command whose output a person watches. A Runner that cannot stream still works:
// the output arrives when the command ends.
func (p podman) stream(ctx context.Context, out io.Writer, args ...string) error {
	streamer, ok := p.r.(Streamer)
	if !ok {
		res, err := p.run(ctx, args...)
		if err == nil {
			_, _ = io.WriteString(out, res.Stdout)
		}
		return err
	}
	code, err := streamer.Stream(ctx, out, "podman", args...)
	if err != nil {
		return &NotRunnable{Err: err}
	}
	if code != 0 {
		return fmt.Errorf("podman %s exited %d", args[0], code)
	}
	return nil
}
