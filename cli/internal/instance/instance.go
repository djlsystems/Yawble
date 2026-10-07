package instance

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"sort"
	"strings"
	"sync"
	"time"

	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/release"
)

// Status is what `yawble status` reports. Image and URL are what RUNS, read from the container
// and the label `up` stamped on it; Pending names settings the config has changed since, which
// the next `up` applies. Healthy is nil unless the container runs: not measured is not a failure.
type Status struct {
	Engine        string       `json:"engine"`
	EngineVersion string       `json:"engineVersion"`
	Container     engine.State `json:"container"`
	Image         string       `json:"image"`
	URL           string       `json:"url"`
	Healthy       *bool        `json:"healthy"`
	Pending       []string     `json:"pending,omitempty"`
	// Workers is each worker container, configured or found, by number.
	Workers []WorkerStatus `json:"workers"`
}

// WorkerStatus is one worker container as the engine sees it. Health is the engine's verdict
// from the image's health check, which reads healthy exactly while the worker is connected.
type WorkerStatus struct {
	Name      string        `json:"name"`
	ID        string        `json:"id"`
	Container engine.State  `json:"container"`
	Health    engine.Health `json:"health,omitempty"`
	Image     string        `json:"image"`
	Pending   []string      `json:"pending,omitempty"`
}

// ErrNoImage is `up` with nothing to run. A dev build pins none, and guessing one (or reaching
// for `latest`) is exactly what the design forbids.
var ErrNoImage = errors.New("this build of yawble pins no Yawble image; choose one with: yawble config set image ghcr.io/djlsystems/yawble:<version>")

// TunnelName is the remote sidecar's container. The instance starts and stops it with the Host
// when it exists; the remote package creates and configures it.
const TunnelName = "yawble-tunnel"

// Health polling bounds. The first start installs the agent CLIs onto the volume before the
// host listens, which is minutes, not seconds. The container's own state is checked every few
// polls so a crash loop is reported in seconds, not after the whole timeout.
var (
	healthInterval  = 2 * time.Second
	healthTimeout   = 10 * time.Minute
	healthNoteAt    = 20 * time.Second
	stateCheckEvery = 5
)

func URL(port int) string { return fmt.Sprintf("http://127.0.0.1:%d", port) }

// applied is what the settings label records: everything a `run` consumed. Two settings with
// the same label make the same container.
type applied struct {
	Role       string `json:"role"`
	Port       int    `json:"port"`
	Memory     string `json:"memory"`
	CPUs       int    `json:"cpus"`
	MaxRunning int    `json:"maxRunning"`
	Image      string `json:"image"`
	EnvHash    string `json:"envHash"`
	KeyHash    string `json:"keyHash"`
}

// controlApplied is control's label for the settings now: its own fixed allowance, not a worker's.
func controlApplied(s Settings) applied {
	return applied{RoleControl, s.Port, ControlMemory, ControlCPUs, s.MaxRunning, s.Image, s.EnvFileHash, s.KeyHash}
}

// PreSplit says whether control's label is from before control and workers: a yawble label with
// no role, or none at all.
func PreSplit(label string) bool {
	var was applied
	return label == "" || json.Unmarshal([]byte(label), &was) != nil || was.Role == ""
}

// SettingsLabel is the label value `up` stamps on the container: the settings as JSON, so the
// next `up` can compare what runs with what the config now says by asking the engine.
func SettingsLabel(s Settings) string {
	b, _ := json.Marshal(controlApplied(s))
	return string(b)
}

// Changes names what differs between a recorded label and the settings now, in words for a
// person. An empty or unreadable label (a container something else made) differs in everything.
func Changes(label string, s Settings) []string {
	var was applied
	if label == "" || json.Unmarshal([]byte(label), &was) != nil {
		return []string{"the container was not made by yawble, or by an older yawble"}
	}
	if was.Role == "" {
		return []string{"the container is from before control and workers were split"}
	}
	now := controlApplied(s)
	var out []string
	if was.Port != now.Port {
		out = append(out, fmt.Sprintf("port %d -> %d", was.Port, now.Port))
	}
	if was.Memory != now.Memory {
		out = append(out, fmt.Sprintf("memory %s -> %s", was.Memory, now.Memory))
	}
	if was.CPUs != now.CPUs {
		out = append(out, fmt.Sprintf("cpus %d -> %d", was.CPUs, now.CPUs))
	}
	if was.MaxRunning != now.MaxRunning {
		out = append(out, fmt.Sprintf("maxRunning %d -> %d", was.MaxRunning, now.MaxRunning))
	}
	if was.Image != now.Image {
		out = append(out, fmt.Sprintf("image %s -> %s", was.Image, now.Image))
	}
	if was.EnvHash != now.EnvHash {
		out = append(out, "the env file changed")
	}
	if was.KeyHash != now.KeyHash {
		out = append(out, "the worker key changed")
	}
	return out
}

// PortOf is the port a container's settings label records, else fallback: where a container
// that exists answers, whatever the config now says.
func PortOf(label string, fallback int) int { return portOf(label, fallback) }

func portOf(label string, fallback int) int {
	var was applied
	if label != "" && json.Unmarshal([]byte(label), &was) == nil && was.Port != 0 {
		return was.Port
	}
	return fallback
}

// Up makes every object exist and control and the workers run with the settings the config now
// says, creating only what is missing and replacing only what differs. It is the repair command
// as much as the start command: on an instance that already matches, it changes nothing. The
// order is volume, pod, control (waited for), each worker (waited for), then a tunnel sidecar
// that exists and is stopped. A container from before control and workers becomes control, and
// worker 1 joins it, on the same volume.
func Up(ctx context.Context, e engine.Engine, s Settings, health func(string) bool, out io.Writer) error {
	if s.Image == "" {
		return ErrNoImage
	}
	if s.Workers < 1 {
		s.Workers = 1
	}
	ref, err := s.WorkerRef()
	if err != nil {
		return err
	}
	if ok, err := e.VolumeExists(ctx, VolumeName); err != nil {
		return err
	} else if !ok {
		if err := e.CreateVolume(ctx, VolumeName); err != nil {
			return err
		}
		fmt.Fprintf(out, "created volume %s (the instance's data; `yawble down` never removes it)\n", VolumeName)
	}
	podExisted, err := e.PodExists(ctx, PodName)
	if err != nil {
		return err
	}
	if podExisted {
		current, err := e.PodCurrent(ctx, PodName)
		if err != nil {
			return err
		}
		if !current {
			// Removing the pod removes the containers in it (the Host, a tunnel sidecar); the
			// Host is created again below on the same volume, and `remote enable` remakes a tunnel.
			if err := e.RemovePod(ctx, PodName); err != nil {
				return err
			}
			fmt.Fprintf(out, "remaking pod %s so its port reaches localhost on this computer; the data volume is kept\n", PodName)
			podExisted = false
		}
	}
	if !podExisted {
		if err := e.CreatePod(ctx, PodName, s.Port, ContainerPort); err != nil {
			return portError(err, s.Port)
		}
		fmt.Fprintf(out, "created pod %s publishing port %d\n", PodName, s.Port)
	}
	info, err := e.Inspect(ctx, ContainerName)
	if err != nil {
		return err
	}
	podCreatedHere := !podExisted
	// On Docker a worker lives in control's network namespace: a control made again leaves it in
	// a dead one, and a control started again has a new one. Podman's pod owns the namespace.
	var step workerStep

	if info.State != engine.StateAbsent {
		if diff := Changes(info.Label, s); len(diff) > 0 {
			if PreSplit(info.Label) {
				fmt.Fprintln(out, PreSplitLine)
			} else {
				fmt.Fprintf(out, "settings changed (%s); replacing %s on the same volume\n", strings.Join(diff, ", "), ContainerName)
			}
			step.recreate = e.Name() == "docker"
			if err := e.Remove(ctx, ContainerName); err != nil {
				return err
			}
			if portOf(info.Label, s.Port) != s.Port {
				// The pod holds the port. A new pod, then the container joins it below. The
				// tunnel sidecar was in that pod too and goes with it; `remote enable` recreates it.
				if err := e.RemovePod(ctx, PodName); err != nil {
					return err
				}
				if err := e.CreatePod(ctx, PodName, s.Port, ContainerPort); err != nil {
					return portError(err, s.Port)
				}
				podCreatedHere = true
				fmt.Fprintf(out, "recreated pod %s publishing port %d\n", PodName, s.Port)
			}
			info = engine.ContainerInfo{State: engine.StateAbsent}
		}
	}

	switch info.State {
	case engine.StateRunning:
		fmt.Fprintf(out, "%s is already running\n", ContainerName)
	case engine.StateStopped:
		if err := e.Start(ctx, ContainerName); err != nil {
			return portError(err, s.Port)
		}
		step.restart = e.Name() == "docker"
		fmt.Fprintf(out, "started %s\n", ContainerName)
	case engine.StateAbsent:
		if present, err := e.ImagePresent(ctx, s.Image); err != nil {
			return err
		} else if !present {
			fmt.Fprintf(out, "pulling %s\n", s.Image)
			if err := e.Pull(ctx, s.Image, out); err != nil {
				// Measured on a first up in a fresh distro: a failed pull would otherwise leave
				// the pod this up made holding the port with nothing in it.
				if podCreatedHere {
					_ = e.RemovePod(ctx, PodName)
				}
				return fmt.Errorf("the pull of %s failed (%w); podman's own message is above. Is the image name right, and can this machine reach the registry?", s.Image, err)
			}
		}
		if err := e.Run(ctx, controlSpec(s)); err != nil {
			// A failed first run leaves a container in "created" and, when this up made the pod,
			// a pod on a port nobody can use. Both would make every later up fail the same way.
			_ = e.Remove(ctx, ContainerName)
			if podCreatedHere {
				_ = e.RemovePod(ctx, PodName)
			}
			return portError(err, s.Port)
		}
		fmt.Fprintf(out, "started %s (control) from %s (memory %s, cpus %d, running limit %s)\n", ContainerName, s.Image, ControlMemory, ControlCPUs, RunLimitText(s))
	}
	if err := WaitHealthy(ctx, e, URL(s.Port), health, out); err != nil {
		return err
	}
	if err := upWorkers(ctx, e, s, ref, step, out); err != nil {
		return err
	}
	// The tunnel, when one exists and was stopped with the Host.
	if state, err := e.ContainerState(ctx, TunnelName); err == nil && state == engine.StateStopped {
		if err := e.Start(ctx, TunnelName); err == nil {
			fmt.Fprintf(out, "started %s (remote access)\n", TunnelName)
		} else {
			fmt.Fprintf(out, "note: %s could not be started: %v (yawble remote enable recreates it)\n", TunnelName, err)
		}
	}
	return nil
}

// controlSpec is control's run: the published port, the role, its fixed allowance and the few
// capabilities it keeps. No Wip__MaxRunning unless one was configured: the Host's default then
// applies and GET /api/wip names its bound.
func controlSpec(s Settings) engine.RunSpec {
	// HARNESS_RELEASE_REPOSITORY tells the Host where its releases are published, so the version in
	// the web's top bar can say when a newer one is out. The Host carries no repository name of its own.
	env := map[string]string{"HARNESS_ROLE": RoleControl, "HARNESS_RELEASE_REPOSITORY": release.Repository}
	if s.MaxRunning > 0 {
		env["Wip__MaxRunning"] = fmt.Sprint(s.MaxRunning)
	}
	return engine.RunSpec{
		Name: ContainerName, Pod: PodName, Image: s.Image,
		Volumes: []string{VolumeName + ":/data"},
		Env:     env,
		Labels: map[string]string{
			InstanceLabel: PodName, RoleLabel: RoleControl, engine.SettingsLabel: SettingsLabel(s),
		},
		EnvFiles: s.EnvFiles(), Memory: ControlMemory, CPUs: ControlCPUs,
		CapDrop: []string{"ALL"}, CapAdd: ControlCaps,
		HostPort: s.Port, ContainerPort: ContainerPort,
	}
}

// portError names the port as the thing to check when a pod, run or start fails. When podman
// says "in use" the cause is known and said. Otherwise it is NOT claimed: Podman 6 reports a
// taken port at run time as "starting some containers: internal libpod error" (measured), so
// the port is offered as the first thing to look at, beside podman's own sentence.
func portError(err error, port int) error {
	if strings.Contains(strings.ToLower(err.Error()), "in use") {
		return fmt.Errorf("port %d is already in use by something else: stop that, or choose another with `yawble config set port <n>` (%w)", port, err)
	}
	return fmt.Errorf("%w\nThe usual cause is port %d being held by something else (`ss -ltnp` or `netstat -ano` shows what); stop that, or choose another port with `yawble config set port <n>`", err, port)
}

// WaitHealthy polls the instance until it answers, noticing a container that stops on the way.
// `up` uses it after a start, and so does `doctor --fix`: a Host is not up the instant
// `podman start` returns.
func WaitHealthy(ctx context.Context, e engine.Engine, url string, health func(string) bool, out io.Writer) error {
	return waitHealthy(ctx, e, url, health, out)
}

func waitHealthy(ctx context.Context, e engine.Engine, url string, health func(string) bool, out io.Writer) error {
	started := time.Now()
	deadline := started.Add(healthTimeout)
	noted := false

	// The log is followed from just before this start (the margin covers the machine's clock
	// running a little behind this computer's) and filtered to what a person waiting wants. It
	// is held back until the note: a restart that answers in seconds shows none of it.
	shown := &progress{out: out}
	follow, cancel := context.WithCancel(ctx)
	followed := make(chan struct{})
	go func() {
		defer close(followed)
		_ = e.FollowSince(follow, ContainerName, started.Add(-followMargin), shown)
	}()
	var once sync.Once
	stopFollowing := func() { once.Do(func() { cancel(); <-followed; shown.flush() }) }
	defer stopFollowing()

	for poll := 0; ; poll++ {
		if health(url + "/healthz") {
			stopFollowing()
			fmt.Fprintf(out, "Yawble is up at %s\n", url)
			return nil
		}
		if poll > 0 && poll%stateCheckEvery == 0 {
			if state, err := e.ContainerState(ctx, ContainerName); err == nil && state != engine.StateRunning {
				stopFollowing()
				return stoppedError(ctx, e, url)
			}
		}
		if time.Now().After(deadline) {
			return fmt.Errorf("the instance did not answer at %s/healthz within %s; read `yawble logs`", url, healthTimeout)
		}
		if !noted && time.Since(started) > healthNoteAt {
			fmt.Fprintln(out, "waiting for the instance (the first start installs the agent CLIs onto the volume, which takes a few minutes):")
			shown.show()
			noted = true
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(healthInterval):
		}
	}
}

// followMargin is how far before this start the log is followed from.
const followMargin = 30 * time.Second

// progress is the log as a person waiting for a start sees it: whole lines, only those that say
// what the start is doing, indented under the waiting note. Until show() it holds them back.
type progress struct {
	mu      sync.Mutex
	out     io.Writer
	live    bool
	held    []string
	partial string
}

func (p *progress) Write(b []byte) (int, error) {
	p.mu.Lock()
	defer p.mu.Unlock()
	text := p.partial + string(b)
	lines := strings.Split(text, "\n")
	p.partial = lines[len(lines)-1]
	for _, line := range lines[:len(lines)-1] {
		p.add(line)
	}
	return len(b), nil
}

// add keeps one line if it means something; the caller holds the lock.
func (p *progress) add(line string) {
	msg := progressLine(line)
	if msg == "" {
		return
	}
	if p.live {
		fmt.Fprintln(p.out, "  "+msg)
	} else {
		p.held = append(p.held, msg)
	}
}

// show prints what was held and every later line as it comes.
func (p *progress) show() {
	p.mu.Lock()
	defer p.mu.Unlock()
	p.live = true
	for _, msg := range p.held {
		fmt.Fprintln(p.out, "  "+msg)
	}
	p.held = nil
}

// flush takes a last line that ended without a newline.
func (p *progress) flush() {
	p.mu.Lock()
	defer p.mu.Unlock()
	if p.partial != "" {
		p.add(p.partial)
		p.partial = ""
	}
}

// progressLine is the part of one log line worth showing, or "". The entrypoint's own lines
// ("volume: ...", "agent cli: ...") are kept whole; of the Host's JSON lines only its
// agent-launch decision is; apt's output and every request line are dropped.
func progressLine(line string) string {
	line = strings.TrimSpace(line)
	switch {
	case strings.HasPrefix(line, "volume:"), strings.HasPrefix(line, "agent cli:"):
		return line
	case strings.HasPrefix(line, "{"):
		var entry struct{ Category, Message string }
		if json.Unmarshal([]byte(line), &entry) == nil && entry.Category == "Harness.Host" && strings.HasPrefix(entry.Message, "Agent launch") {
			return entry.Message
		}
	}
	return ""
}

// stoppedError is a container that stopped before it answered. It is stopped for good first,
// because `--restart unless-stopped` would otherwise start it again forever; `yawble up` starts
// it when the person is ready. Then the last log lines are read so the one refusal a person can
// act on, a volume the Host will not open, is said in the Host's own words with the ways out.
func stoppedError(ctx context.Context, e engine.Engine, url string) error {
	_ = e.Stop(ctx, ContainerName)
	var logs strings.Builder
	_ = e.Logs(ctx, ContainerName, false, 50, &logs)
	for _, line := range strings.Split(logs.String(), "\n") {
		if strings.Contains(line, "written by a newer build") {
			return fmt.Errorf("%s stopped and has been left stopped: %s\nThe Host in this image will not open this volume: it was written by an older or a newer Yawble than the image knows. If yawble itself is old, `yawble update` moves yawble and the instance to the latest release, whose image knows it. If the volume is from before a schema squash, recreate it with `yawble uninstall --data`, which deletes every team, account and saved login on it", ContainerName, strings.TrimSpace(line))
		}
	}
	return fmt.Errorf("%s stopped before it answered at %s/healthz and has been left stopped; the reason is in `yawble logs`, and `yawble up` starts it again", ContainerName, url)
}

// StopWorkers stops every running worker, highest first, and answers the ones it stopped in
// that order. A worker is stopped before control: on Docker it lives in control's network
// namespace.
func StopWorkers(ctx context.Context, e engine.Engine) ([]string, error) {
	indices, err := WorkerContainers(ctx, e)
	if err != nil {
		return nil, err
	}
	var stopped []string
	for k := len(indices) - 1; k >= 0; k-- {
		name := WorkerName(indices[k])
		state, err := e.ContainerState(ctx, name)
		if err != nil {
			return stopped, err
		}
		if state == engine.StateRunning {
			if err := e.Stop(ctx, name); err != nil {
				return stopped, err
			}
			stopped = append(stopped, name)
		}
	}
	return stopped, nil
}

// StartWorkers starts the named workers, lowest first, once control answers again, and waits
// for each to connect: what a backup stopped, or what doctor --fix finds stopped.
func StartWorkers(ctx context.Context, e engine.Engine, names []string, out io.Writer) error {
	sorted := append([]string(nil), names...)
	sort.Slice(sorted, func(a, b int) bool {
		ia, _ := WorkerIndex(sorted[a])
		ib, _ := WorkerIndex(sorted[b])
		return ia < ib
	})
	for _, name := range sorted {
		if err := e.Start(ctx, name); err != nil {
			return fmt.Errorf("%s could not be started again (`yawble up` starts it): %w", name, err)
		}
		fmt.Fprintf(out, "started %s\n", name)
	}
	for _, name := range sorted {
		if err := waitWorker(ctx, e, name, out); err != nil {
			return err
		}
	}
	return nil
}

// Down stops the workers, control and the tunnel sidecar in front of them, nothing else. The pod
// keeps the port, the volume keeps the data, and `up` brings all of it back with a start.
func Down(ctx context.Context, e engine.Engine, port int, out io.Writer) error {
	if state, err := e.ContainerState(ctx, TunnelName); err == nil && state == engine.StateRunning {
		if err := e.Stop(ctx, TunnelName); err != nil {
			return err
		}
		fmt.Fprintf(out, "stopped %s (remote access; `yawble up` starts it again)\n", TunnelName)
	}
	workers, err := StopWorkers(ctx, e)
	if err != nil {
		return err
	}
	if len(workers) > 0 {
		fmt.Fprintf(out, "stopped %s\n", strings.Join(workers, ", "))
	}
	state, err := e.ContainerState(ctx, ContainerName)
	if err != nil {
		return err
	}
	stopped := len(workers) > 0
	if state == engine.StateRunning {
		if err := e.Stop(ctx, ContainerName); err != nil {
			return err
		}
		stopped = true
	}
	// On Podman the pod's infra container publishes the port. It runs on after the container
	// stops, holding the port, until the pod itself is stopped; `up` starts it again with the
	// container. On Docker there is no pod and this does nothing.
	podRunning, err := e.PodRunning(ctx, PodName)
	if err != nil {
		return err
	}
	if podRunning {
		if err := e.StopPod(ctx, PodName); err != nil {
			return err
		}
	}
	if !stopped && !podRunning {
		fmt.Fprintf(out, "%s is not running; nothing to stop (data volume %s kept)\n", ContainerName, VolumeName)
		return nil
	}
	fmt.Fprintf(out, "stopped %s; port %d is free (data volume %s kept; `yawble up` starts it again)\n", ContainerName, port, VolumeName)
	return nil
}

// GetStatus asks the engine what runs and, only when the container runs, the instance itself.
// The config is consulted only to say what a future `up` would change.
func GetStatus(ctx context.Context, e engine.Engine, s Settings, health func(string) bool) (Status, error) {
	st := Status{Engine: e.Name(), Image: s.Image, URL: URL(s.Port)}
	if v, err := e.Version(ctx); err == nil {
		st.EngineVersion = v
	} else {
		st.EngineVersion = "not available: " + err.Error()
	}
	info, err := e.Inspect(ctx, ContainerName)
	if err != nil {
		return st, err
	}
	st.Container = info.State
	if info.State != engine.StateAbsent {
		if info.Image != "" {
			st.Image = info.Image
		}
		st.URL = URL(portOf(info.Label, s.Port))
		st.Pending = Changes(info.Label, s)
	}
	if info.State == engine.StateRunning {
		h := health(st.URL + "/healthz")
		st.Healthy = &h
	}
	st.Workers, err = Workers(ctx, e, s)
	return st, err
}

// Workers is workers 1..the configured count, and any other worker container found.
func Workers(ctx context.Context, e engine.Engine, s Settings) ([]WorkerStatus, error) {
	existing, err := WorkerContainers(ctx, e)
	if err != nil {
		return nil, err
	}
	count := max(s.Workers, 1)
	for _, i := range existing {
		count = max(count, i)
	}
	ref, refErr := s.WorkerRef()
	var out []WorkerStatus
	for i := 1; i <= count; i++ {
		w := WorkerStatus{Name: WorkerName(i), ID: WorkerID(i)}
		info, err := e.Inspect(ctx, w.Name)
		if err != nil {
			return out, err
		}
		w.Container, w.Image = info.State, info.Image
		if info.State == engine.StateAbsent && i > s.Workers {
			continue
		}
		if info.State != engine.StateAbsent && refErr == nil {
			w.Pending = WorkerChanges(info.Label, s, i, ref)
		}
		if i > s.Workers {
			w.Pending = append(w.Pending, fmt.Sprintf("beyond the configured %d (yawble workers %d removes it)", s.Workers, s.Workers))
		}
		if info.State == engine.StateRunning {
			if w.Health, err = e.Health(ctx, w.Name); err != nil {
				return out, err
			}
		}
		out = append(out, w)
	}
	return out, nil
}

// Logs prints one container's log: control's, or a worker's (LogTarget).
func Logs(ctx context.Context, e engine.Engine, name string, follow bool, tail int, out io.Writer) error {
	return e.Logs(ctx, name, follow, tail, out)
}

// Removal is what Uninstall found on one engine: anything of Yawble's, and the data volume
// (removed with data, kept without).
type Removal struct {
	Found, Volume bool
}

// Uninstall removes the sidecar, every worker, control, the pod (a network on Docker) and the
// images from one engine, and the data volume only when asked. Workers go before control: on
// Docker each lives in control's network namespace. What exists is asked first, and only what
// exists is removed; every line names the engine, and an engine holding nothing says so. An
// engine that cannot answer is a refusal, never a silent no-op with a clean exit.
func Uninstall(ctx context.Context, e engine.Engine, images []string, data bool, out io.Writer) (Removal, error) {
	var r Removal
	name := e.Name()
	if _, err := e.Version(ctx); err != nil {
		return r, fmt.Errorf("%s cannot be asked, so nothing was removed from it: %w", name, err)
	}
	pod := "pod"
	if name == "docker" {
		pod = "network"
	}
	var failed []string
	// what is the object, rest what follows the engine on the line.
	remove := func(what, rest string, exists func() (bool, error), rm func() error) bool {
		ok, err := exists()
		if err != nil {
			failed = append(failed, what+": "+err.Error())
			return false
		}
		if !ok {
			return false
		}
		r.Found = true
		if err := rm(); err != nil {
			failed = append(failed, what+": "+err.Error())
			return true
		}
		fmt.Fprintf(out, "removed %s (%s)%s\n", what, name, rest)
		return true
	}
	containerExists := func(c string) func() (bool, error) {
		return func() (bool, error) {
			state, err := e.ContainerState(ctx, c)
			return state != engine.StateAbsent, err
		}
	}
	remove("tunnel sidecar "+TunnelName, "", containerExists(TunnelName), func() error { return e.Remove(ctx, TunnelName) })
	others, err := instanceContainers(ctx, e)
	if err != nil {
		failed = append(failed, "containers of the instance: "+err.Error())
	}
	for _, c := range others {
		remove("container "+c, "", containerExists(c), func() error { return e.Remove(ctx, c) })
	}
	remove("container "+ContainerName, "", containerExists(ContainerName), func() error { return e.Remove(ctx, ContainerName) })
	remove(pod+" "+PodName, "", func() (bool, error) { return e.PodExists(ctx, PodName) }, func() error { return e.RemovePod(ctx, PodName) })
	for _, image := range images {
		if image != "" {
			remove("image "+image, "", func() (bool, error) { return e.ImagePresent(ctx, image) }, func() error { return e.RemoveImage(ctx, image) })
		}
	}
	volumeExists := func() (bool, error) { return e.VolumeExists(ctx, VolumeName) }
	if data {
		r.Volume = remove("volume "+VolumeName, " and everything on it", volumeExists, func() error { return e.RemoveVolume(ctx, VolumeName) })
	} else if ok, err := volumeExists(); err != nil {
		failed = append(failed, "volume "+VolumeName+": "+err.Error())
	} else if ok {
		r.Found, r.Volume = true, true
		fmt.Fprintf(out, "volume %s (%s) kept (the instance's data); `yawble uninstall --data` removes it\n", VolumeName, name)
	}
	if len(failed) > 0 {
		return r, fmt.Errorf("%s: some things could not be removed:\n  %s", name, strings.Join(failed, "\n  "))
	}
	if !r.Found {
		fmt.Fprintf(out, "%s: no Yawble container, %s, image or volume\n", name, pod)
	}
	return r, nil
}

// instanceContainers is every container of the instance but control and the tunnel, workers
// highest first: those the instance's label marks, and any worker found by name.
func instanceContainers(ctx context.Context, e engine.Engine) ([]string, error) {
	indices, err := WorkerContainers(ctx, e)
	if err != nil {
		return nil, err
	}
	var names []string
	seen := map[string]bool{ContainerName: true, TunnelName: true}
	for k := len(indices) - 1; k >= 0; k-- {
		names = append(names, WorkerName(indices[k]))
		seen[WorkerName(indices[k])] = true
	}
	labelled, err := e.List(ctx, "label="+InstanceLabel+"="+PodName)
	if err != nil {
		return names, err
	}
	for _, name := range labelled {
		if !seen[name] {
			names = append(names, name)
			seen[name] = true
		}
	}
	return names, nil
}

// RunLimitText is the running limit as `up` states it: the configured number, or the Host's
// default, which only the Host names (`yawble doctor` reads it). Nothing is estimated here.
func RunLimitText(s Settings) string {
	if s.MaxRunning > 0 {
		return fmt.Sprint(s.MaxRunning)
	}
	return "the Host's default (yawble doctor names it)"
}
