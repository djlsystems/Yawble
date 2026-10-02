package instance

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"sort"
	"strconv"
	"strings"
	"time"

	"github.com/djlsystems/yawble/cli/internal/engine"
)

// The instance is one control container, named as the single container always was, and 1-N
// worker containers beside it on the same volume. Control serves the board and holds the
// database; workers run the agents.
const (
	InstanceLabel = "yawble.instance"
	RoleLabel     = "yawble.role"
	// WorkerIndexLabel is a worker's number, 1-based.
	WorkerIndexLabel = "yawble.worker"
	RoleControl      = "control"
	RoleWorker       = "worker"
	workerPrefix     = ContainerName + "-worker-"
	// ControlURLForWorkers is control as a worker reaches it: the pod (Podman) or control's own
	// namespace (Docker) makes it loopback, which the worker accepts as plain ws://.
	ControlURLForWorkers = "ws://127.0.0.1:8080"
	// WorkerStateDir is container-local in the worker image, never on the volume; DrainFile in it
	// asks the worker's Host to take no new run.
	WorkerStateDir = "/var/lib/harness-worker"
	DrainFile      = WorkerStateDir + "/drain"
)

// ControlMemory and ControlCPUs are control's fixed allowance, provisional until measured on a
// busy instance. Every bound and the first-up screen read them from here.
const (
	ControlMemory = "1536m"
	ControlCPUs   = 2
)

// ControlMemoryMB is ControlMemory in megabytes.
func ControlMemoryMB() int { return MemoryMB(ControlMemory) }

// ControlCaps is all control keeps: CHOWN, DAC_OVERRIDE, FOWNER and FSETID for the entrypoint's
// ownership pass; SETUID, SETGID and SETPCAP for its drop to the Host's user; KILL, SETUID and
// SETGID for the Host's git and gh as the agent. Workers keep the engine's default set.
var ControlCaps = []string{"CHOWN", "DAC_OVERRIDE", "FOWNER", "FSETID", "SETUID", "SETGID", "SETPCAP", "KILL"}

// PreSplitLine is what `up` and `update` say, once, when the instance's container is from before
// control and workers.
const PreSplitLine = "This instance is from before control and workers were split: its container becomes control and one worker, on the same volume. Nothing on the volume changes."

// WorkerName is worker i's container, 1-based.
func WorkerName(i int) string { return workerPrefix + strconv.Itoa(i) }

// WorkerID is worker i as its Host names itself to control. Always passed: the pod shares one
// hostname, which would make every worker the same id.
func WorkerID(i int) string { return "worker-" + strconv.Itoa(i) }

// WorkerIndex reads a worker container's number from its name.
func WorkerIndex(name string) (int, bool) {
	rest, ok := strings.CutPrefix(name, workerPrefix)
	if !ok {
		return 0, false
	}
	n, err := strconv.Atoi(rest)
	return n, err == nil && n >= 1
}

// LogTarget is the container `yawble logs` reads: control by default, else a worker named
// "worker-<i>" or "<i>".
func LogTarget(arg string) (string, error) {
	switch arg {
	case "", RoleControl, ContainerName:
		return ContainerName, nil
	}
	n, err := strconv.Atoi(strings.TrimPrefix(strings.TrimPrefix(arg, workerPrefix), "worker-"))
	if err != nil || n < 1 {
		return "", fmt.Errorf("%q is not control, worker-<n> or a worker's number", arg)
	}
	return WorkerName(n), nil
}

// WorkerRef is the worker image: the config's workerImage, else control's reference with
// "-worker" on its tag ("latest-worker" when it has none). A reference by digest names no tag
// to derive from, so it needs workerImage.
func (s Settings) WorkerRef() (string, error) {
	if s.WorkerImage != "" {
		return s.WorkerImage, nil
	}
	return WorkerImage(s.Image)
}

// WorkerImage derives the worker image from control's reference.
func WorkerImage(control string) (string, error) {
	if strings.Contains(control, "@") {
		return "", fmt.Errorf("the image %s is named by digest, so its worker image cannot be derived; name it with: yawble config set workerImage <reference>", control)
	}
	slash := strings.LastIndex(control, "/")
	if colon := strings.LastIndex(control, ":"); colon > slash {
		return control + "-worker", nil
	}
	return control + ":latest-worker", nil
}

// workerApplied is what a worker's settings label records.
type workerApplied struct {
	Role    string `json:"role"`
	Index   int    `json:"index"`
	Memory  string `json:"memory"`
	CPUs    int    `json:"cpus"`
	Image   string `json:"image"`
	EnvHash string `json:"envHash"`
	KeyHash string `json:"keyHash"`
}

// WorkerSettingsLabel is the label value stamped on worker i.
func WorkerSettingsLabel(s Settings, i int, ref string) string {
	b, _ := json.Marshal(workerApplied{RoleWorker, i, s.Memory, s.CPUs, ref, s.EnvFileHash, s.KeyHash})
	return string(b)
}

// WorkerChanges names what differs between worker i's label and the settings now.
func WorkerChanges(label string, s Settings, i int, ref string) []string {
	var was workerApplied
	if label == "" || json.Unmarshal([]byte(label), &was) != nil || was.Role != RoleWorker {
		return []string{"the container was not made by this yawble"}
	}
	var out []string
	if was.Memory != s.Memory {
		out = append(out, fmt.Sprintf("memory %s -> %s", was.Memory, s.Memory))
	}
	if was.CPUs != s.CPUs {
		out = append(out, fmt.Sprintf("cpus %d -> %d", was.CPUs, s.CPUs))
	}
	if was.Image != ref {
		out = append(out, fmt.Sprintf("image %s -> %s", was.Image, ref))
	}
	if was.EnvHash != s.EnvFileHash {
		out = append(out, "the env file changed")
	}
	if was.KeyHash != s.KeyHash {
		out = append(out, "the worker key changed")
	}
	return out
}

func workerSpec(s Settings, i int, ref string) engine.RunSpec {
	return engine.RunSpec{
		Name: WorkerName(i), Pod: PodName, Network: "container:" + ContainerName, Image: ref,
		Volumes: []string{VolumeName + ":/data"},
		Env: map[string]string{
			"HARNESS_ROLE":        RoleWorker,
			"HARNESS_CONTROL_URL": ControlURLForWorkers,
			"HARNESS_WORKER_ID":   WorkerID(i),
		},
		Labels: map[string]string{
			InstanceLabel: PodName, RoleLabel: RoleWorker, WorkerIndexLabel: strconv.Itoa(i),
			engine.SettingsLabel: WorkerSettingsLabel(s, i, ref),
		},
		EnvFiles: s.EnvFiles(), Memory: s.Memory, CPUs: s.CPUs,
	}
}

// WorkerContainers is every worker container the engine knows, by number, lowest first: those
// labelled as workers and, for an instance whose labels were lost, those named as workers.
func WorkerContainers(ctx context.Context, e engine.Engine) ([]int, error) {
	seen := map[int]bool{}
	for _, filter := range []string{"label=" + RoleLabel + "=" + RoleWorker, "name=^" + workerPrefix} {
		names, err := e.List(ctx, filter)
		if err != nil {
			return nil, err
		}
		for _, name := range names {
			if i, ok := WorkerIndex(name); ok {
				seen[i] = true
			}
		}
	}
	indices := make([]int, 0, len(seen))
	for i := range seen {
		indices = append(indices, i)
	}
	sort.Ints(indices)
	return indices, nil
}

// workerStep is what one up does to one worker before waiting for it.
type workerStep struct {
	// recreate replaces a worker that exists (Docker, when control was made again: a worker
	// joined the old control's namespace). restart stops and starts a running one (Docker, when
	// control was started again: its namespace is new).
	recreate, restart bool
}

// upWorkers makes workers 1..s.Workers run with the settings now, then waits for each to connect
// to control. Workers beyond the count are named, not touched: `yawble workers` removes them,
// waiting for their runs.
func upWorkers(ctx context.Context, e engine.Engine, s Settings, ref string, step workerStep, out io.Writer) error {
	pulled := false
	for i := 1; i <= s.Workers; i++ {
		if err := upWorker(ctx, e, s, i, ref, step, &pulled, out); err != nil {
			return err
		}
	}
	for i := 1; i <= s.Workers; i++ {
		if err := waitWorker(ctx, e, WorkerName(i), out); err != nil {
			return err
		}
	}
	existing, err := WorkerContainers(ctx, e)
	if err != nil {
		return err
	}
	for _, i := range existing {
		if i > s.Workers {
			fmt.Fprintf(out, "note: %s is beyond the %d worker(s) yawble's config asks for; `yawble workers %d` stops it once its runs end\n", WorkerName(i), s.Workers, s.Workers)
		}
	}
	return nil
}

func upWorker(ctx context.Context, e engine.Engine, s Settings, i int, ref string, step workerStep, pulled *bool, out io.Writer) error {
	name := WorkerName(i)
	info, err := e.Inspect(ctx, name)
	if err != nil {
		return err
	}
	if info.State != engine.StateAbsent {
		diff := WorkerChanges(info.Label, s, i, ref)
		if step.recreate && len(diff) == 0 {
			diff = []string{"control was made again, and a worker shares control's network"}
		}
		if len(diff) > 0 {
			fmt.Fprintf(out, "settings changed (%s); replacing %s on the same volume\n", strings.Join(diff, ", "), name)
			if err := e.Remove(ctx, name); err != nil {
				return err
			}
			info.State = engine.StateAbsent
		} else if step.restart && info.State == engine.StateRunning {
			if err := e.Stop(ctx, name); err != nil {
				return err
			}
			info.State = engine.StateStopped
		}
	}
	switch info.State {
	case engine.StateRunning:
		fmt.Fprintf(out, "%s is already running\n", name)
	case engine.StateStopped:
		if err := e.Start(ctx, name); err != nil {
			return err
		}
		fmt.Fprintf(out, "started %s\n", name)
	case engine.StateAbsent:
		if !*pulled {
			if present, err := e.ImagePresent(ctx, ref); err != nil {
				return err
			} else if !present {
				fmt.Fprintf(out, "pulling %s\n", ref)
				if err := e.Pull(ctx, ref, out); err != nil {
					return fmt.Errorf("the pull of %s failed (%w); the engine's own message is above. Is the image name right, and can this machine reach the registry?", ref, err)
				}
			}
			*pulled = true
		}
		if err := e.Run(ctx, workerSpec(s, i, ref)); err != nil {
			_ = e.Remove(ctx, name)
			return err
		}
		fmt.Fprintf(out, "started %s from %s (memory %s, cpus %d)\n", name, ref, s.Memory, s.CPUs)
	}
	return nil
}

// waitWorker waits until the engine reads the worker healthy, which its image's health check
// makes exactly "connected to control". A worker image without a health check cannot say, and is
// not waited for; one that stops is reported at once, not after the whole wait.
func waitWorker(ctx context.Context, e engine.Engine, name string, out io.Writer) error {
	started := time.Now()
	deadline := started.Add(healthTimeout)
	noted := false
	for poll := 0; ; poll++ {
		h, err := e.Health(ctx, name)
		if err != nil {
			return err
		}
		switch h {
		case engine.HealthHealthy:
			fmt.Fprintf(out, "%s is connected to control\n", name)
			return nil
		case engine.HealthNone:
			fmt.Fprintf(out, "note: %s has no health check (an image built without --format docker), so its connection to control is not waited for; yawble status shows it\n", name)
			return nil
		}
		if poll > 0 && poll%stateCheckEvery == 0 {
			if state, err := e.ContainerState(ctx, name); err == nil && state != engine.StateRunning {
				return fmt.Errorf("%s stopped before it connected to control; the reason is in `yawble logs %s`, and `yawble up` starts it again", name, strings.TrimPrefix(name, ContainerName+"-"))
			}
		}
		if time.Now().After(deadline) {
			return fmt.Errorf("%s did not connect to control within %s; read `yawble logs %s`", name, healthTimeout, strings.TrimPrefix(name, ContainerName+"-"))
		}
		if !noted && time.Since(started) > healthNoteAt {
			fmt.Fprintf(out, "waiting for %s to connect to control (a first start installs the agent CLIs onto the volume, which takes a few minutes)\n", name)
			noted = true
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(healthInterval):
		}
	}
}

// Bound is the one rule for how many workers an engine holds. Memory is a hard bound: workers ×
// each worker's memory + control's allowance must fit in the engine's memory, and the refusal
// names all three figures. CPUs are only warned about: a CPU limit is a ceiling the containers
// share, not a reservation. Equal to the engine is within it.
func Bound(engineMB, engineCPUs, workers, workerMB, workerCPUs int) (cpuWarning string, refusal error) {
	total := workers*workerMB + ControlMemoryMB()
	if total > engineMB {
		refusal = fmt.Errorf("%d worker(s) × %d MB + control's %d MB = %d MB, more than the %d MB the engine has; choose fewer workers, or less memory per worker (yawble config set memory <size>), or give the engine more", workers, workerMB, ControlMemoryMB(), total, engineMB)
	}
	if cpus := workers*workerCPUs + ControlCPUs; cpus > engineCPUs {
		cpuWarning = fmt.Sprintf("%d worker(s) × %d CPUs + control's %d = %d CPUs, more than the engine's %d; the containers share them (a CPU limit is a ceiling, not a reservation)", workers, workerCPUs, ControlCPUs, cpus, engineCPUs)
	}
	return cpuWarning, refusal
}

// Run is one run on a worker, as control recorded it.
type Run struct{ Run, Team, Member string }

// RunsOf answers the runs on one worker, by its id, as control recorded them. An error is control
// not answering, or recording nothing: the runs are then not known.
type RunsOf func(ctx context.Context, id string) ([]Run, error)

// Drain polling bounds: how often the runs of a draining worker are asked, and how long a drain
// may wait before it gives up and leaves the worker running.
var (
	drainInterval = 10 * time.Second
	drainTimeout  = 24 * time.Hour
)

// errKept is a removal the person declined: nothing more is removed.
var errKept = errors.New("kept")

// ScaleOptions say how removed workers are stopped: by default each is drained and stopped once
// it holds no run; Now stops it at once, after Confirm agrees to the runs it fails.
type ScaleOptions struct {
	Now     bool
	Confirm func(question string) (bool, error)
}

// Scale makes the worker containers match s.Workers, touching only workers, never the volume or
// control. New workers are started and waited for; workers above the count are removed highest
// first, each drained of its runs unless Now.
func Scale(ctx context.Context, e engine.Engine, s Settings, runsOf RunsOf, opt ScaleOptions, out io.Writer) error {
	if s.Workers < 1 {
		s.Workers = 1
	}
	ref, err := s.WorkerRef()
	if err != nil {
		return err
	}
	existing, err := WorkerContainers(ctx, e)
	if err != nil {
		return err
	}
	for k := len(existing) - 1; k >= 0; k-- {
		if i := existing[k]; i > s.Workers {
			if err := removeWorker(ctx, e, i, runsOf, opt, out); err != nil {
				if errors.Is(err, errKept) {
					return nil
				}
				return err
			}
		}
	}
	pulled := false
	for i := 1; i <= s.Workers; i++ {
		if err := upWorker(ctx, e, s, i, ref, workerStep{}, &pulled, out); err != nil {
			return err
		}
	}
	for i := 1; i <= s.Workers; i++ {
		if err := waitWorker(ctx, e, WorkerName(i), out); err != nil {
			return err
		}
	}
	return nil
}

// runNames is a worker's runs as "team/member", for the lines a person reads.
func runNames(runs []Run) string {
	names := make([]string, len(runs))
	for k, r := range runs {
		names[k] = r.Team + "/" + r.Member
	}
	return strings.Join(names, ", ")
}

func plural(n int, one, many string) string {
	if n == 1 {
		return fmt.Sprintf("%d %s", n, one)
	}
	return fmt.Sprintf("%d %s", n, many)
}

// removeWorker stops and removes worker i. One that is not running holds no run and goes at once.
// A running one is drained: told to take no new run, then waited for until its runs end. With
// Now it is stopped at once instead, after a warning naming the runs that fail.
func removeWorker(ctx context.Context, e engine.Engine, i int, runsOf RunsOf, opt ScaleOptions, out io.Writer) error {
	name, id := WorkerName(i), WorkerID(i)
	state, err := e.ContainerState(ctx, name)
	if err != nil {
		return err
	}
	if state != engine.StateRunning {
		if err := e.Remove(ctx, name); err != nil {
			return err
		}
		fmt.Fprintf(out, "removed %s (it was not running)\n", name)
		return nil
	}
	runs, err := runsOf(ctx, id)
	if err != nil && !opt.Now {
		return fmt.Errorf("the runs on %s are not known (%v), so it was not stopped; `yawble workers <n> --now` stops it anyway, failing any run on it as worker-lost", id, err)
	}
	if opt.Now {
		if err != nil || len(runs) > 0 {
			what := "the runs on it are not known (" + errText(err) + "); any run on it fails as worker-lost"
			if err == nil {
				what = fmt.Sprintf("this fails its %s as worker-lost (%s); the Manager re-sends each once", plural(len(runs), "run", "runs"), runNames(runs))
			}
			fmt.Fprintf(out, "Stopping %s now: %s.\n", id, what)
			ok, err := opt.Confirm("Stop " + id + " now?")
			if err != nil {
				return err
			}
			if !ok {
				fmt.Fprintf(out, "%s was kept; nothing more was stopped\n", id)
				return errKept
			}
		}
		if err := e.Remove(ctx, name); err != nil {
			return err
		}
		fmt.Fprintf(out, "stopped and removed %s\n", name)
		return nil
	}
	if len(runs) > 0 {
		if _, err := e.Exec(ctx, name, "touch", DrainFile); err != nil {
			return fmt.Errorf("%s could not be told to take no new run: %w", id, err)
		}
		fmt.Fprintf(out, "draining %s: control places no new run on it\n", id)
		deadline := time.Now().Add(drainTimeout)
		said := -1
		for len(runs) > 0 {
			if len(runs) != said {
				fmt.Fprintf(out, "%s: %s still going (%s); waiting. `--now` stops it at once.\n", id, plural(len(runs), "run", "runs"), runNames(runs))
				said = len(runs)
			}
			if time.Now().After(deadline) {
				return fmt.Errorf("%s still holds %s after %s; it is left running and draining. `yawble workers <n> --now` stops it", id, plural(len(runs), "run", "runs"), drainTimeout)
			}
			select {
			case <-ctx.Done():
				return ctx.Err()
			case <-time.After(drainInterval):
			}
			if runs, err = runsOf(ctx, id); err != nil {
				return fmt.Errorf("the runs on draining %s are no longer known (%v); it is left running and draining. `yawble workers <n> --now` stops it", id, err)
			}
		}
	}
	if err := e.StopWithin(ctx, name, 30); err != nil {
		return err
	}
	if err := e.Remove(ctx, name); err != nil {
		return err
	}
	fmt.Fprintf(out, "stopped and removed %s (no runs on it)\n", name)
	return nil
}

func errText(err error) string {
	if err == nil {
		return ""
	}
	return err.Error()
}
