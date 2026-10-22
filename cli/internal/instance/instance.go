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
	"sync"
	"time"

	"github.com/djlsystems/yawble/cli/internal/config"
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
	// PreviewPorts is the configured preview range ("" with previews off). PreviewMadeWith is the
	// range control was made with and PreviewPublished the ports of it published, as its label
	// records them; nil with no control of yawble's.
	PreviewPorts     string `json:"previewPorts"`
	PreviewMadeWith  string `json:"previewMadeWith,omitempty"`
	PreviewPublished []int  `json:"previewPublished"`
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
	// Catalog is omitted when none is passed, so a label from before it reads the same.
	Catalog string `json:"marketplaceCatalog,omitempty"`
	// MicrosoftClient is omitted when none is passed, so a label from before it reads the same.
	MicrosoftClient string `json:"microsoftClientId,omitempty"`
	// Preview is the configured preview range and Published the ports of it actually published,
	// each as config.FormatPorts writes them; both omitted with previews off, so a label from
	// before them reads as previews off.
	Preview   string `json:"previewPorts,omitempty"`
	Published string `json:"previewPublished,omitempty"`
}

// controlApplied is control's label for the settings now: its own fixed allowance, not a worker's.
func controlApplied(s Settings) applied {
	return applied{RoleControl, s.Port, ControlMemory, ControlCPUs, s.MaxRunning, s.Image, s.EnvFileHash, s.KeyHash, s.MarketplaceCatalog, s.MicrosoftClientID,
		config.FormatPorts(s.PreviewPorts), config.FormatPorts(s.PreviewPublished)}
}

// PreviewOf is the preview range a container's settings label records and the ports of it that
// were published; ok is false for a label that is not yawble's.
func PreviewOf(label string) (preview string, published []int, ok bool) {
	var was applied
	if label == "" || json.Unmarshal([]byte(label), &was) != nil {
		return "", nil, false
	}
	published, _ = config.ParsePorts(was.Published)
	return was.Preview, published, true
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
	if was.Catalog != now.Catalog {
		out = append(out, fmt.Sprintf("marketplaceCatalog %q -> %q", was.Catalog, now.Catalog))
	}
	if was.MicrosoftClient != now.MicrosoftClient {
		out = append(out, fmt.Sprintf("microsoftClientId %q -> %q", was.MicrosoftClient, now.MicrosoftClient))
	}
	if was.Preview != now.Preview {
		out = append(out, fmt.Sprintf("previewPorts %s -> %s", orNone(was.Preview), orNone(now.Preview)))
	} else if s.PreviewPublished != nil && was.Published != now.Published {
		// Decided only by Up: a port it published is taken now by something else.
		out = append(out, fmt.Sprintf("published preview ports %s -> %s", orNone(was.Published), orNone(now.Published)))
	}
	return out
}

func orNone(ports string) string {
	if ports == "" {
		return "none"
	}
	return ports
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
	// On Podman the pod publishes the preview ports beside the Host's; on Docker control does.
	podPublishes := e.Name() != "docker"
	if !podExisted {
		if err := createPod(ctx, e, &s, podPublishes, "created", out); err != nil {
			return err
		}
	}
	info, err := e.Inspect(ctx, ContainerName)
	if err != nil {
		return err
	}
	podCreatedHere := !podExisted
	if podExisted && podPublishes && info.State == engine.StateAbsent && len(s.PreviewPorts) > 0 {
		// A pod with no control in it publishes ports nothing records. Made again, it publishes
		// what is free of the range now.
		if err := e.RemovePod(ctx, PodName); err != nil {
			return err
		}
		if err := createPod(ctx, e, &s, podPublishes, "recreated", out); err != nil {
			return err
		}
		podCreatedHere = true
	}
	wasPublished := keptPreview(ctx, e, &s, info, podPublishes)
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
			podPorts := podPublishes && (s.PreviewPublished == nil || config.FormatPorts(s.PreviewPublished) != config.FormatPorts(wasPublished))
			if portOf(info.Label, s.Port) != s.Port || podPorts {
				// The pod holds the ports. A new pod, then the container joins it below. The
				// tunnel sidecar was in that pod too and goes with it; `remote enable` recreates it.
				if err := e.RemovePod(ctx, PodName); err != nil {
					return err
				}
				s.PreviewPublished = nil
				if err := createPod(ctx, e, &s, podPublishes, "recreated", out); err != nil {
					return err
				}
				podCreatedHere = true
			} else if !podPublishes {
				// Docker: control publishes, and is made again below on what is free now.
				s.PreviewPublished = nil
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
		if !podPublishes || s.PreviewPublished == nil {
			s.PreviewPublished = publishable(s, out)
		}
		err := e.Run(ctx, controlSpec(s))
		if err != nil && len(s.PreviewPublished) > 0 {
			// A preview port the probe saw free and the engine could not bind (taken since, or
			// held where a loopback dial does not reach) never stops the instance: once more with
			// none, said.
			_ = e.Remove(ctx, ContainerName)
			fmt.Fprintf(out, "the preview ports could not be published (%v); starting without them\n", err)
			s.PreviewPublished = []int{}
			err = nil
			if podPublishes {
				if err = e.RemovePod(ctx, PodName); err == nil {
					podCreatedHere = true
					err = createPod(ctx, e, &s, true, "recreated", out)
				}
			}
			if err == nil {
				err = e.Run(ctx, controlSpec(s))
			}
		}
		if err != nil {
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
	// HARNESS_MARKETPLACE_CATALOG is where the Host reads the package catalog for Marketplace > Browse; with
	// none the Host says the catalog is not checked.
	if s.MarketplaceCatalog != "" {
		env["HARNESS_MARKETPLACE_CATALOG"] = s.MarketplaceCatalog
	}
	// HARNESS_MICROSOFT_CLIENT_ID is the public Microsoft app the Host signs in through while no Microsoft
	// client is saved; with none, Microsoft waits for a person's own app.
	if s.MicrosoftClientID != "" {
		env["HARNESS_MICROSOFT_CLIENT_ID"] = s.MicrosoftClientID
	}
	// HARNESS_PUBLISHED_PREVIEW_PORTS is the preview ports actually published, comma-separated: the
	// ports the Host forwards to a preview. Absent with none.
	if len(s.PreviewPublished) > 0 {
		ports := make([]string, len(s.PreviewPublished))
		for i, port := range s.PreviewPublished {
			ports[i] = strconv.Itoa(port)
		}
		env[PublishedPreviewPortsVariable] = strings.Join(ports, ",")
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
		HostPort: s.Port, ContainerPort: ContainerPort, PreviewPorts: s.PreviewPublished,
	}
}

// PublishedPreviewPortsVariable tells control the preview ports actually published: the contract
// with the Host, which forwards each of them to a preview.
const PublishedPreviewPortsVariable = "HARNESS_PUBLISHED_PREVIEW_PORTS"

// createPod makes the pod. When it publishes (Podman) it publishes the Host's port and every
// preview port free now, and the settings record which; verb is "created" or "recreated".
func createPod(ctx context.Context, e engine.Engine, s *Settings, publishes bool, verb string, out io.Writer) error {
	var preview []int
	if publishes {
		if s.PreviewPublished == nil {
			s.PreviewPublished = publishable(*s, out)
		}
		preview = s.PreviewPublished
	}
	if err := e.CreatePod(ctx, PodName, s.Port, ContainerPort, preview...); err != nil {
		return portError(err, s.Port)
	}
	if len(preview) > 0 {
		fmt.Fprintf(out, "%s pod %s publishing port %d and preview ports %s\n", verb, PodName, s.Port, config.FormatPorts(preview))
	} else {
		fmt.Fprintf(out, "%s pod %s publishing port %d\n", verb, PodName, s.Port)
	}
	return nil
}

// publishable is the configured preview ports less any this computer has taken, each skipped
// port said in one line: a taken port never stops the instance starting. The Host's own ports
// are never a preview's. Asked only where nothing of the instance holds the ports.
func publishable(s Settings, out io.Writer) []int {
	free := s.PortFree
	if free == nil {
		free = PortFree
	}
	published := []int{}
	for _, port := range s.PreviewPorts {
		switch {
		case port == s.Port || port == ContainerPort:
			fmt.Fprintf(out, "preview port %d is Yawble's own port; skipped\n", port)
		case !free(port):
			fmt.Fprintf(out, "preview port %d is already in use on this computer; skipped (the instance starts without it)\n", port)
		default:
			published = append(published, port)
		}
	}
	return published
}

// keptPreview decides, for a control that exists with the preview range configured now, which
// preview ports are published: those its label records while they are held, or, when nothing of
// the instance holds them (a stopped instance), every port of the range free now, so a port
// taken since is dropped and one freed since is taken up. It answers the ports the label
// records. A changed range leaves PreviewPublished undecided: Changes names it, and the ports are
// probed again where they are bound.
func keptPreview(ctx context.Context, e engine.Engine, s *Settings, info engine.ContainerInfo, podPublishes bool) []int {
	if info.State == engine.StateAbsent {
		return nil
	}
	preview, published, ok := PreviewOf(info.Label)
	if !ok || preview != config.FormatPorts(s.PreviewPorts) {
		return published
	}
	s.PreviewPublished = append([]int{}, published...)
	if info.State == engine.StateStopped && len(s.PreviewPorts) > 0 {
		held := false
		if podPublishes {
			// The pod's infra container holds them while the pod runs with the container stopped.
			held, _ = e.PodRunning(ctx, PodName)
		}
		if !held {
			s.PreviewPublished = publishable(*s, io.Discard)
		}
	}
	return published
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

	return waitUntil(ctx, e, ContainerName,
		func() (bool, error) {
			if !health(url + "/healthz") {
				return false, nil
			}
			stopFollowing()
			fmt.Fprintf(out, "Yawble is up at %s\n", url)
			return true, nil
		},
		func() error {
			stopFollowing()
			return stoppedError(ctx, e, url)
		},
		func() error {
			return fmt.Errorf("the instance did not answer at %s/healthz within %s; read `yawble logs`", url, healthTimeout)
		},
		func() {
			fmt.Fprintln(out, "waiting for the instance (the first start installs the agent CLIs onto the volume, which takes a few minutes):")
			shown.show()
		})
}

// waitUntil is the one wait on a container coming up: check every healthInterval until it answers
// done or an error; every stateCheckEvery polls, a container no longer running is stopped(); past
// healthTimeout it is timedOut(); noted() is said once after healthNoteAt.
func waitUntil(
	ctx context.Context, e engine.Engine, name string,
	check func() (bool, error), stopped func() error, timedOut func() error, noted func()) error {
	started := time.Now()
	deadline := started.Add(healthTimeout)
	said := false
	for poll := 0; ; poll++ {
		done, err := check()
		if err != nil || done {
			return err
		}
		if poll > 0 && poll%stateCheckEvery == 0 {
			if state, err := e.ContainerState(ctx, name); err == nil && state != engine.StateRunning {
				return stopped()
			}
		}
		if time.Now().After(deadline) {
			return timedOut()
		}
		if !said && time.Since(started) > healthNoteAt {
			noted()
			said = true
		}
		if err := sleep(ctx, healthInterval); err != nil {
			return err
		}
	}
}

const followMargin = 30 * time.Second

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
	st := Status{Engine: e.Name(), Image: s.Image, URL: URL(s.Port), PreviewPorts: config.FormatPorts(s.PreviewPorts)}
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
		if preview, published, ok := PreviewOf(info.Label); ok {
			st.PreviewMadeWith, st.PreviewPublished = preview, append([]int{}, published...)
		}
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

// RunLimitText is the running limit as `up` states it: the configured number, the number the
// first up's size screen just said, or else a pointer to `yawble doctor`, which reads the Host's.
// Nothing is estimated here.
func RunLimitText(s Settings) string {
	switch {
	case s.MaxRunning > 0:
		return fmt.Sprint(s.MaxRunning)
	case s.RunsShown > 0:
		return fmt.Sprint(s.RunsShown)
	}
	return "set from the worker's size (yawble doctor shows the number)"
}
