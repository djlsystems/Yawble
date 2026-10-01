// Package engine is the one interface between the CLI and a container engine. Podman is the
// implementation this step ships; Docker is a later one behind the same interface. Every
// method maps to one command line, listed in the Podman file, and tests assert those lines.
package engine

import (
	"context"
	"errors"
	"fmt"
	"io"
	"strings"
	"time"
)

// State is what a container is right now. Absent is a container the engine does not know.
type State string

const (
	StateAbsent  State = "absent"
	StateRunning State = "running"
	StateStopped State = "stopped"
)

// RunSpec is everything a first start needs. A zero Memory or CPUs means no limit flag.
// HostPort and ContainerPort are used by engines without pods (Docker publishes on the
// container); Podman ignores them because the pod already publishes.
type RunSpec struct {
	Name, Pod, Image string
	Volumes          []string
	Env              map[string]string
	Labels           map[string]string
	EnvFile          string
	Memory           string
	CPUs             int
	HostPort         int
	ContainerPort    int
	// Command is passed after the image, each element its own argument: what a sidecar's
	// entrypoint is told to do. Empty for the Host image, whose entrypoint needs nothing.
	Command []string
}

// ContainerInfo is what one inspect answers: the state, the image the container was created
// from, and the settings label `up` stamped on it (empty when absent or made by something else).
type ContainerInfo struct {
	State State
	Image string
	Label string
}

// SettingsLabel is the label key under which `up` records the settings a container was made
// with, so a later `up` can tell whether they changed by asking the engine, not a file.
const SettingsLabel = "yawble.settings"

// Engine is what the verbs need from a container engine, no more. Adding a method here means
// adding it to every implementation and to the scripted tests that pin its command line.
type Engine interface {
	Name() string
	Version(ctx context.Context) (string, error)
	VolumeExists(ctx context.Context, name string) (bool, error)
	CreateVolume(ctx context.Context, name string) error
	PodExists(ctx context.Context, name string) (bool, error)
	CreatePod(ctx context.Context, name string, hostPort, containerPort int) error
	RemovePod(ctx context.Context, name string) error
	// StopPod stops a pod, and with it the infra container that holds the published port. A
	// missing pod is not an error. A Docker network runs nothing, so there it does nothing.
	StopPod(ctx context.Context, name string) error
	// PodRunning says whether a pod's infra container is up, holding its port. Always false on
	// Docker, where the port belongs to the container itself.
	PodRunning(ctx context.Context, name string) (bool, error)
	// PodCurrent says whether an existing pod was made the way CreatePod makes one now; `up`
	// remakes one that was not (the volume is untouched).
	PodCurrent(ctx context.Context, name string) (bool, error)
	ContainerState(ctx context.Context, name string) (State, error)
	Inspect(ctx context.Context, name string) (ContainerInfo, error)
	// Stats is the engine's one-shot reading of a container's resource use (`stats --no-stream`).
	// A container that is not running is Stats.NotRunning, not an error.
	Stats(ctx context.Context, name string) (Stats, error)
	Remove(ctx context.Context, name string) error
	// Exec runs a program inside a running container and answers its output. A non-zero exit
	// is an error carrying stderr, as for every other verb.
	Exec(ctx context.Context, name string, args ...string) (Result, error)
	// ExecTo is Exec with the program's output streamed into stdout as it is produced, stderr
	// collected in the Result: how a bare repository leaves the instance as a tar stream, which
	// must never be held in memory.
	ExecTo(ctx context.Context, name string, stdout io.Writer, args ...string) (Result, error)
	// ExecInput is Exec with text handed to the program on stdin (`exec -i`): how a request
	// carrying something that must not be on a command line - an authorization code - reaches a
	// file in the container.
	ExecInput(ctx context.Context, name, stdin string, args ...string) (Result, error)
	// CopyTo copies a folder on this computer into a container, as dst there. dst must not exist:
	// the folder's contents become dst. Ownership and modes are the caller's to set afterwards.
	CopyTo(ctx context.Context, name, src, dst string) error
	Run(ctx context.Context, spec RunSpec) error
	Start(ctx context.Context, name string) error
	Stop(ctx context.Context, name string) error
	ImagePresent(ctx context.Context, ref string) (bool, error)
	Pull(ctx context.Context, ref string, progress io.Writer) error
	// Login stores a registry credential in the engine's own auth file. The password goes on
	// stdin, never on the command line.
	Login(ctx context.Context, registry, user, password string) error
	RemoveImage(ctx context.Context, ref string) error
	RemoveVolume(ctx context.Context, name string) error
	Logs(ctx context.Context, name string, follow bool, tail int, out io.Writer) error
	// FollowSince follows the log from a moment on, until ctx ends: `up` shows a first start's
	// progress without replaying an earlier run.
	FollowSince(ctx context.Context, name string, since time.Time, out io.Writer) error
	// RunHelper runs a short-lived container from an image already on this machine with a volume
	// mounted, and removes it when it exits: how backup and restore read and write the data
	// volume on both engines (Docker has no `volume export`). A nil Stdout collects the output
	// in the Result.
	RunHelper(ctx context.Context, spec HelperSpec) (Result, error)
}

// HelperSpec is one helper container. It runs as root (the volume's files belong to two users),
// with no network and without pulling: the image is the instance's own, already here.
type HelperSpec struct {
	Image      string
	Volume     string // mounted at Target
	Target     string
	ReadOnly   bool
	Entrypoint string
	Args       []string
	// Stdin, when set, is handed to the program (the container runs with -i); Stdout, when set,
	// receives its output as it is produced.
	Stdin  io.Reader
	Stdout io.Writer
}

// helperArgs is the one command line for both engines: `run` takes the same flags on each.
func helperArgs(s HelperSpec) []string {
	args := []string{"run", "--rm"}
	if s.Stdin != nil {
		args = append(args, "-i")
	}
	mount := s.Volume + ":" + s.Target
	if s.ReadOnly {
		mount += ":ro"
	}
	args = append(args, "--pull", "never", "--network", "none", "--user", "0", "--entrypoint", s.Entrypoint, "-v", mount, s.Image)
	return append(args, s.Args...)
}

// runHelper runs a helper with the program given, streaming when the spec asks and the runner can.
func runHelper(ctx context.Context, r Runner, program string, s HelperSpec) (Result, error) {
	args := helperArgs(s)
	var res Result
	var err error
	if s.Stdin != nil || s.Stdout != nil {
		pipe, ok := r.(PipeRunner)
		if !ok {
			return Result{}, errors.New("this runner cannot stream to a helper container")
		}
		res, err = pipe.RunPipe(ctx, s.Stdin, s.Stdout, program, args...)
	} else {
		res, err = r.Run(ctx, program, args...)
	}
	if err != nil {
		return res, &NotRunnable{Err: err}
	}
	if res.ExitCode != 0 {
		return res, fmt.Errorf("%s run %s %s: %s (exit %d)", program, s.Image, s.Entrypoint, strings.TrimSpace(res.Stderr), res.ExitCode)
	}
	return res, nil
}

// execTo is ExecTo for both engines: `exec` takes the same arguments on each.
func execTo(ctx context.Context, r Runner, program, name string, stdout io.Writer, args []string) (Result, error) {
	pipe, ok := r.(PipeRunner)
	if !ok {
		return Result{}, errors.New("this runner cannot stream from a container")
	}
	res, err := pipe.RunPipe(ctx, nil, stdout, program, append([]string{"exec", name}, args...)...)
	if err != nil {
		return res, &NotRunnable{Err: err}
	}
	if res.ExitCode != 0 {
		return res, fmt.Errorf("%s exec %s: %s (exit %d)", program, name, strings.TrimSpace(res.Stderr), res.ExitCode)
	}
	return res, nil
}

func execInput(ctx context.Context, r Runner, program, name, stdin string, args []string) (Result, error) {
	in, ok := r.(InputRunner)
	if !ok {
		return Result{}, errors.New("this runner cannot hand a container input")
	}
	res, err := in.RunInput(ctx, stdin, program, append([]string{"exec", "-i", name}, args...)...)
	if err != nil {
		return res, &NotRunnable{Err: err}
	}
	if res.ExitCode != 0 {
		return res, fmt.Errorf("%s exec %s: %s (exit %d)", program, name, strings.TrimSpace(res.Stderr), res.ExitCode)
	}
	return res, nil
}

// NotRunnable is the engine's program not starting at all: not on PATH, not executable. It is a
// different fact from the program running and exiting non-zero (a machine that is down), and
// the two get different advice, so this one is typed.
type NotRunnable struct{ Err error }

func (e *NotRunnable) Error() string {
	return "podman could not be run (is Podman installed and on PATH?): " + e.Err.Error()
}

func (e *NotRunnable) Unwrap() error { return e.Err }

// Streamer is a Runner whose output goes to a writer as it is produced: logs and pulls, which a
// person watches. The real runner and the scripted one both satisfy it.
type Streamer interface {
	Stream(ctx context.Context, out io.Writer, name string, args ...string) (int, error)
}
