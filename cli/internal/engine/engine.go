// Package engine is the one interface between the CLI and a container engine. Podman is the
// implementation this step ships; Docker is a later one behind the same interface. Every
// method maps to one command line, listed in the Podman file, and tests assert those lines.
package engine

import (
	"context"
	"io"
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
	Remove(ctx context.Context, name string) error
	// Exec runs a program inside a running container and answers its output. A non-zero exit
	// is an error carrying stderr, as for every other verb.
	Exec(ctx context.Context, name string, args ...string) (Result, error)
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
