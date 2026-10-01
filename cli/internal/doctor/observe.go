package doctor

import (
	"context"
	"errors"
	"fmt"
	"github.com/djlsystems/yawble/cli/internal/config"
	"github.com/djlsystems/yawble/cli/internal/github"
	"net"
	"net/http"
	"path/filepath"
	"runtime"
	"strings"
	"time"

	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/instance"
	"github.com/djlsystems/yawble/cli/internal/machine"
	"github.com/djlsystems/yawble/cli/internal/remote"
)

// HostDoctorArgs is the Host's own operator switch, run inside the container. HARNESS_DATA_ROOT
// comes from the image's environment, which exec inherits.
var HostDoctorArgs = []string{"dotnet", "/app/Harness.Host.dll", "--doctor"}

// ErrNotRunning is FetchHostReport asked about a container that is not running: nothing can be
// measured inside it, and that is Skip, not failure.
var ErrNotRunning = errors.New("the instance is not running, so nothing inside it could be measured")

// ErrEngineUnavailable is the engine itself not answering: nothing about the instance can be
// measured, and the engine check has already said why.
var ErrEngineUnavailable = errors.New("the engine could not be asked, so nothing inside the instance could be measured")

// Probes are the measurements that touch this machine rather than the engine, and the facts
// about it. Tests hand in stubs so no test depends on the developer's ports or operating system.
type Probes struct {
	Health    func(url string) bool
	PortFree  func(port int) bool
	Runner    engine.Runner // for the machine commands; nil means the engine's own runner is not reachable, so pass one
	GOOS      string        // "" means runtime.GOOS
	ConfigDir string        // where remote.toml and the env file are; "" skips the remote and github rows
	// HTTP and GitHubAPI ask GitHub whether the GH_TOKEN set with `yawble secret` still works;
	// a nil HTTP skips the github row. GitHubAPI "" means api.github.com.
	HTTP      *http.Client
	GitHubAPI string
}

// Observe measures the host side. Each measurement that cannot be made leaves its field unset,
// and the check tables read that as Skip. Where a Podman machine exists it is asked first, and
// a machine that is absent, stopped or unreadable ends the observation there: every engine
// answer behind it would be the same failure. Then the engine's version; then the rest.
func Observe(ctx context.Context, e engine.Engine, s instance.Settings, p Probes, exeDir, pathEnv string) Observed {
	goos := p.GOOS
	if goos == "" {
		goos = runtime.GOOS
	}
	o := Observed{EngineName: e.Name(), Image: s.Image, Port: s.Port, URL: instance.URL(s.Port), ExeDir: exeDir, GOOS: goos, ContainerMemoryMB: ContainerMemoryMB(s.Memory), CPUs: s.CPUs, MaxRunning: s.MaxRunning}
	if exeDir != "" {
		on := OnPath(resolved(exeDir), pathEnv, goos) || OnPath(exeDir, pathEnv, goos)
		o.OnPath = &on
	}
	// First, before any early return: whether GitHub accepts the token does not depend on the
	// engine, and a person without a running instance yet needs to hear about it too.
	if p.ConfigDir != "" && p.HTTP != nil {
		o.GitHub = observeGitHub(ctx, p.HTTP, p.GitHubAPI, p.ConfigDir)
	}
	if p.PortFree == nil {
		p.PortFree = PortFree
	}
	// The Podman machine only matters when Podman is the engine: with Docker, Docker Desktop runs
	// its own VM, and a Podman machine installed beside it is not this instance's business.
	if p.Runner != nil && e.Name() == "podman" {
		o.Machine, o.MachineErr = machine.Inspect(ctx, p.Runner, goos)
		if o.Machine.Applies && (o.MachineErr != nil || !o.Machine.Exists || !o.Machine.Running) {
			return o
		}
		if o.Machine.Applies {
			o.HostMemoryMB = machine.HostMemoryMB(ctx, p.Runner, goos)
		}
	}

	version, err := e.Version(ctx)
	if err != nil {
		o.EngineErr = err
		return o
	}
	o.EngineVersion = version

	if s.Image != "" {
		if present, err := e.ImagePresent(ctx, s.Image); err == nil {
			o.ImagePresent = &present
		}
	}
	if present, err := e.VolumeExists(ctx, instance.VolumeName); err == nil {
		o.VolumePresent = &present
	}
	if present, err := e.PodExists(ctx, instance.PodName); err == nil {
		o.PodPresent = &present
	}
	st, err := instance.GetStatus(ctx, e, s, p.Health)
	if err != nil {
		return o
	}
	o.ContainerKnown = true
	o.Container = st.Container
	o.Pending = st.Pending
	o.URL = st.URL
	o.Healthy = st.Healthy
	if p.ConfigDir != "" {
		if c, had, err := remote.Load(p.ConfigDir); err == nil && had {
			if provider, err := remote.ProviderNamed(c.Provider); err == nil {
				ro := &RemoteObserved{Provider: c.Provider, State: engine.StateAbsent}
				if rs, err := remote.Status(ctx, e, provider, c.LastURL); err == nil {
					ro.State, ro.URL = rs.State, rs.URL
				}
				o.Remote = ro
			}
		}
	}
	// The port is measured only when nothing of ours could be holding it: the pod, when it
	// exists, keeps the port published even with the container stopped.
	if st.Container != engine.StateRunning && (o.PodPresent == nil || !*o.PodPresent) {
		free := p.PortFree(s.Port)
		o.PortFree = &free
	}
	return o
}

// resolved follows symlinks so a binary symlinked into a PATH directory counts as on it.
func resolved(dir string) string {
	if real, err := filepath.EvalSymlinks(dir); err == nil {
		return real
	}
	return dir
}

// PortFree dials the loopback port for an instant. A listener answers; nothing there refuses.
// Dialing rather than listening raises no firewall prompt on Windows.
func PortFree(port int) bool {
	conn, err := net.DialTimeout("tcp", fmt.Sprintf("127.0.0.1:%d", port), 500*time.Millisecond)
	if err != nil {
		return true
	}
	_ = conn.Close()
	return false
}

// FetchHostReport runs the Host's --doctor inside the container and parses its last line.
func FetchHostReport(ctx context.Context, e engine.Engine, running bool) (*HostReport, error) {
	if !running {
		return nil, ErrNotRunning
	}
	res, err := e.Exec(ctx, instance.ContainerName, HostDoctorArgs...)
	if err != nil {
		// Measured: a Host that does not know --doctor ignores it, tries to serve, is refused by
		// the data-root lock the live Host holds, and exits 1 with this sentence. That is the
		// image's age, not a fault. Anything else the Host said is a fault and is reported.
		if strings.Contains(err.Error(), "already using this data root") {
			return nil, ErrNoReport
		}
		return nil, fmt.Errorf("the Host's doctor could not be run: %w", err)
	}
	r, err := ParseHostReport(res.Stdout)
	if err != nil {
		return nil, err
	}
	return &r, nil
}

// observeGitHub asks GitHub who the GH_TOKEN in the env file belongs to (internal/github), and
// says an unreachable GitHub as such, not as a failure. The token is never printed.
func observeGitHub(ctx context.Context, client *http.Client, api, configDir string) *GitHubObserved {
	token, set, err := config.SecretValue(configDir, "GH_TOKEN")
	if err != nil || !set {
		return &GitHubObserved{}
	}
	a := github.Check(ctx, client, api, token)
	return &GitHubObserved{Set: true, Status: a.Status, Login: a.Login, Unreachable: a.Unreachable,
		Kind: a.Kind, Scopes: a.Scopes, ContributorReady: a.ContributorReady()}
}
