package cli

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"runtime"

	"github.com/spf13/cobra"

	"github.com/djlsystems/yawble/cli/internal/buildinfo"
	"github.com/djlsystems/yawble/cli/internal/config"
	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/instance"
	"github.com/djlsystems/yawble/cli/internal/machine"
)

// loadConfig reads the config with the environment applied.
func loadConfig(deps Deps) (config.Config, error) {
	c, _, err := config.Load(deps.ConfigDir, deps.Env)
	return c, err
}

// engineOf is the engine the config and the machine give: the configured one, else Podman when
// present, else Docker when present. `up` saves the choice when both are installed.
// A GitHub token, when one is set, logs the engine in to ghcr.io before a pull from it.
func engineOf(deps Deps, c config.Config) engine.Engine {
	return engine.WithRegistryToken(engine.NewFor(engine.Select(c.Engine, deps.LookPath), runnerOf(deps), goosOf(deps)), githubToken(deps))
}

// githubToken is GH_TOKEN, else GITHUB_TOKEN, else "". While the repository and its image are
// private it is what lets `update --cli` read a release and the engine pull the image; once
// both are public nothing needs it and none is set.
func githubToken(deps Deps) string {
	if deps.Env == nil {
		return ""
	}
	if t := deps.Env("GH_TOKEN"); t != "" {
		return t
	}
	return deps.Env("GITHUB_TOKEN")
}

// settingsFor derives the run's settings from the config and a measurement of the machine the
// container will run in, and adds the env file when there is one.
func settingsFor(deps Deps, c config.Config, m instance.Machine) (instance.Settings, []string, error) {
	s, notes := instance.Defaults(c, m, buildinfo.Image())
	envFile := config.EnvFile(deps.ConfigDir)
	hash, err := readEnvFile(envFile)
	if err != nil {
		return s, nil, err
	}
	if hash != "" {
		s.EnvFile, s.EnvFileHash = envFile, hash
	}
	return s, notes, nil
}

// measure is what the container's default limits come from: what the engine has, asked of it
// (engineCapacity). A Podman machine that is not running yet (before `up`'s preflight) is not
// measured, and Defaults says so.
func measure(deps Deps, c config.Config) instance.Machine {
	return engineCapacity(context.Background(), deps, engine.Select(c.Engine, deps.LookPath))
}

func machineOf(info machine.Info) instance.Machine {
	if !info.Running || info.MemoryMB == 0 {
		return instance.Machine{}
	}
	return instance.Machine{MemoryBytes: int64(info.MemoryMB) << 20, CPUs: info.CPUs, Measured: true, Source: "podman machine"}
}

// prepare loads config, derives settings and picks the engine. Every lifecycle verb but `up`
// starts here; `up` does the same in pieces around its preflight.
func prepare(deps Deps) (engine.Engine, instance.Settings, []string, error) {
	e, s, notes, _, err := prepareMeasured(deps)
	return e, s, notes, err
}

// prepareMeasured is prepare, also answering what the engine has (for `doctor`).
func prepareMeasured(deps Deps) (engine.Engine, instance.Settings, []string, instance.Machine, error) {
	c, err := loadConfig(deps)
	if err != nil {
		return nil, instance.Settings{}, nil, instance.Machine{}, err
	}
	m := measure(deps, c)
	s, notes, err := settingsFor(deps, c, m)
	if err != nil {
		return nil, instance.Settings{}, nil, instance.Machine{}, err
	}
	return engineOf(deps, c), s, notes, m, nil
}

// runnerOf is the one place the real runner is chosen, so every command and the machine layer
// share whatever a test injected.
func runnerOf(deps Deps) engine.Runner {
	if deps.Runner != nil {
		return deps.Runner
	}
	return engine.ExecRunner()
}

// readEnvFile answers a digest of the env file, or "" when there is none. The file holds
// provider keys, so on Unix a mode that lets the group or anyone else read it is refused with
// the fix named, before the engine is touched. Windows has no Unix modes; the profile directory
// is the boundary there.
func readEnvFile(path string) (string, error) {
	info, err := os.Stat(path)
	if err != nil || info.IsDir() {
		return "", nil
	}
	if runtime.GOOS != "windows" && info.Mode().Perm()&0o077 != 0 {
		return "", fmt.Errorf("%s holds provider keys and is readable by other users (mode %04o); run: chmod 600 %s", path, info.Mode().Perm(), path)
	}
	data, err := os.ReadFile(path)
	if err != nil {
		return "", fmt.Errorf("reading %s: %w", path, err)
	}
	sum := sha256.Sum256(data)
	return hex.EncodeToString(sum[:8]), nil
}

func healthChecker(client *http.Client) func(string) bool {
	return func(url string) bool {
		resp, err := client.Get(url)
		if err != nil {
			return false
		}
		defer resp.Body.Close()
		return resp.StatusCode == http.StatusOK
	}
}

func newUpCommand(deps Deps) *cobra.Command {
	var yes, noBrowser bool
	cmd := &cobra.Command{
		Use:   "up",
		Short: "Start the instance on Podman or Docker, creating what is missing; safe to run again",
		Long: "up runs the instance on the container engine that is installed: Podman or Docker, asking " +
			"which when both are (Podman is recommended) and remembering the answer. It installs no " +
			"engine: with neither, it says where to get one. With Podman on macOS and Windows it creates the Podman machine " +
			"rootless if there is none, starts it if it is stopped, and offers to make a rootful one " +
			"rootless. Then it creates the data volume and the pod, pulls the image, starts the container " +
			"and waits for it to answer. Every step is idempotent: on a healthy instance it changes nothing.",
		Example: "  yawble up\n  yawble up --yes    # answer every question yes (Podman when both are installed, machine changes)",
		Args:    cobra.NoArgs,
		RunE: func(cmd *cobra.Command, _ []string) error {
			showLogo(deps, cmd.OutOrStdout())
			_, statErr := os.Stat(config.Path(deps.ConfigDir))
			fresh := os.IsNotExist(statErr)
			e, s, err := upReady(cmd, deps, yes, true)
			if err != nil {
				return err
			}
			if fresh {
				noteReusedVolume(cmd.Context(), deps, e, cmd.OutOrStdout())
			}
			noteRestart(cmd.Context(), e, s, cmd.OutOrStdout())
			if err := instance.Up(cmd.Context(), e, s, healthChecker(deps.HTTP), cmd.OutOrStdout()); err != nil {
				return err
			}
			gitHubHint(deps, cmd.OutOrStdout())
			// A person at a terminal gets the board opened; a script does not want a window.
			if deps.Interactive && !noBrowser {
				openBrowser(cmd.Context(), deps, instance.URL(s.Port))
			}
			return nil
		},
	}
	cmd.Flags().BoolVarP(&yes, "yes", "y", false, "answer yes to every question: Podman when both engines are installed, create or re-root the machine")
	cmd.Flags().BoolVar(&noBrowser, "no-browser", false, "do not open the board in a browser when it is up")
	return cmd
}

// noteReusedVolume is what a fresh install (no saved settings, no container) says about data that
// is already there: the chosen engine's data volume, which the new instance takes over with every
// team and login on it, and one on the other engine, which it does not use. Neither is touched.
func noteReusedVolume(ctx context.Context, deps Deps, e engine.Engine, out io.Writer) {
	if state, err := e.ContainerState(ctx, instance.ContainerName); err != nil || state != engine.StateAbsent {
		return
	}
	if v := volumeOn(ctx, e); v != "" {
		fmt.Fprintf(out, "using the existing data volume %s: its teams and logins are kept; `yawble uninstall --data` removes it\n", v)
	}
	if other := otherEngine(deps, e.Name()); other != nil {
		if v := volumeOn(ctx, other); v != "" {
			fmt.Fprintf(out, "%s also holds a data volume %s, which this instance on %s does not use; `%s volume rm %s` removes it\n", other.Name(), v, e.Name(), other.Name(), instance.VolumeName)
		}
	}
}

func newDownCommand(deps Deps) *cobra.Command {
	return &cobra.Command{
		Use:     "down",
		Short:   "Stop the instance; the data volume is kept",
		Example: "  yawble down",
		Args:    cobra.NoArgs,
		RunE: func(cmd *cobra.Command, _ []string) error {
			e, s, _, err := prepare(deps)
			if err != nil {
				return err
			}
			return instance.Down(cmd.Context(), e, s.Port, cmd.OutOrStdout())
		},
	}
}

func newStatusCommand(deps Deps) *cobra.Command {
	var asJSON bool
	cmd := &cobra.Command{
		Use:     "status",
		Short:   "Whether the instance runs, its URL, engine, image and the engine's stats of it",
		Example: "  yawble status\n  yawble status --json",
		Args:    cobra.NoArgs,
		RunE: func(cmd *cobra.Command, _ []string) error {
			e, s, _, err := prepare(deps)
			if err != nil {
				return err
			}
			st, err := instance.GetStatus(cmd.Context(), e, s, healthChecker(deps.HTTP))
			if err != nil {
				return err
			}
			stats, statsErr := containerStats(cmd.Context(), e, st.Container)
			if asJSON {
				enc := json.NewEncoder(cmd.OutOrStdout())
				enc.SetIndent("", "  ")
				return enc.Encode(statusJSON{Status: st, Stats: stats, StatsError: errText(statsErr)})
			}
			w := cmd.OutOrStdout()
			fmt.Fprintf(w, "container  %s\n", st.Container)
			fmt.Fprintf(w, "url        %s\n", st.URL)
			if st.Healthy != nil {
				if *st.Healthy {
					fmt.Fprintln(w, "health     healthy")
				} else {
					fmt.Fprintln(w, "health     not answering (yawble logs)")
				}
			}
			fmt.Fprintf(w, "engine     %s %s\n", st.Engine, st.EngineVersion)
			fmt.Fprintf(w, "stats      %s\n", statsLine(e, stats, statsErr))
			if st.Image == "" {
				fmt.Fprintln(w, "image      none pinned (yawble config set image <reference>)")
			} else {
				fmt.Fprintf(w, "image      %s\n", st.Image)
			}
			if len(st.Pending) > 0 {
				fmt.Fprintf(w, "pending    %v (yawble up applies them)\n", st.Pending)
			}
			return nil
		},
	}
	cmd.Flags().BoolVar(&asJSON, "json", false, "print JSON")
	return cmd
}

// statusJSON is the status document: the instance's status and, beside it, the engine's own
// stats of the container (null when it is not running), or why they could not be read.
type statusJSON struct {
	instance.Status
	Stats      *engine.Stats `json:"stats"`
	StatsError string        `json:"statsError,omitempty"`
}

// containerStats asks the engine for the container's stats only when it runs: a stopped
// container has none, and that is said, not asked.
func containerStats(ctx context.Context, e engine.Engine, state engine.State) (*engine.Stats, error) {
	if state != engine.StateRunning {
		return nil, nil
	}
	st, err := e.Stats(ctx, instance.ContainerName)
	if err != nil {
		return nil, err
	}
	return &st, nil
}

// statsLine is the stats row of `status`, naming the command the figures came from. A failure
// is "not measured" with the reason; it never fails the status itself.
func statsLine(e engine.Engine, st *engine.Stats, err error) string {
	command := e.Name() + " stats"
	switch {
	case err != nil:
		return fmt.Sprintf("not measured: %v  (%s)", err, command)
	case st == nil:
		return "the container is not running"
	default:
		return fmt.Sprintf("%s  (%s)", st.Summary(), st.Command)
	}
}

func errText(err error) string {
	if err == nil {
		return ""
	}
	return err.Error()
}

func newLogsCommand(deps Deps) *cobra.Command {
	var follow bool
	var tail int
	cmd := &cobra.Command{
		Use:     "logs",
		Short:   "The instance's log",
		Example: "  yawble logs\n  yawble logs -f",
		Args:    cobra.NoArgs,
		RunE: func(cmd *cobra.Command, _ []string) error {
			e, _, _, err := prepare(deps)
			if err != nil {
				return err
			}
			return instance.Logs(cmd.Context(), e, follow, tail, cmd.OutOrStdout())
		},
	}
	cmd.Flags().BoolVarP(&follow, "follow", "f", false, "keep printing new lines")
	cmd.Flags().IntVar(&tail, "tail", 200, "how many lines to start with (0 = all)")
	return cmd
}

// goosOf is the operating system the commands act for: deps.GOOS in tests, else this one.
func goosOf(deps Deps) string {
	if deps.GOOS != "" {
		return deps.GOOS
	}
	return runtime.GOOS
}

// upReady is `up` up to the start: the image pin, the platform, the engine (asked when both are
// installed), its machine, the port, and the settings measured from the machine the container
// will run in. `restore` makes the same preparations before it writes into the volume, without
// the first up's size questions (size false): its question is the replace.
func upReady(cmd *cobra.Command, deps Deps, yes, size bool) (engine.Engine, instance.Settings, error) {
	c, err := loadConfig(deps)
	if err != nil {
		return nil, instance.Settings{}, err
	}
	s, _, err := settingsFor(deps, c, instance.Machine{})
	if err != nil {
		return nil, instance.Settings{}, err
	}
	// Before the engine, the machine or an install is touched: with nothing to run there is
	// nothing to prepare the computer for.
	if s.Image == "" {
		return nil, instance.Settings{}, instance.ErrNoImage
	}
	if err := supportedPlatform(deps); err != nil {
		return nil, instance.Settings{}, err
	}
	// The engine installed (asked when both are); yawble installs none.
	name, err := chooseEngine(deps, c, yes, cmd.OutOrStdout())
	if err != nil {
		return nil, instance.Settings{}, err
	}
	c.Engine = name
	info, err := preflight(cmd.Context(), deps, yes, name, containerMemoryMB(c.Memory), cmd.OutOrStdout(), cmd.ErrOrStderr())
	if err != nil {
		return nil, instance.Settings{}, err
	}
	// The port, once the engine answers and before anything is created.
	port, err := ensurePort(cmd.Context(), deps, c, yes, cmd.OutOrStdout())
	if err != nil {
		return nil, instance.Settings{}, err
	}
	c.Port = port
	// Now the machine the container runs in is known: derive the limits from what its engine
	// has. The Podman machine that just came up was inspected by the preflight already.
	m := engineCapacity(cmd.Context(), deps, name)
	if name != "docker" && info.Applies {
		m = machineOf(info)
	}
	// The first up chooses the container's size; later ups keep what was saved and warn about a
	// saved value the engine cannot give.
	if size {
		if c, err = sizeFirstUp(deps, c, m, yes, cmd.OutOrStdout()); err != nil {
			return nil, instance.Settings{}, err
		}
	}
	for _, w := range overEngine(c, m) {
		fmt.Fprintln(cmd.ErrOrStderr(), "warning:", w)
	}
	// Asked once, before the settings are read, so a token saved now reaches the container
	// on this same run.
	offerGitHub(cmd.Context(), deps, c, yes, cmd.OutOrStdout())
	s, notes, err := settingsFor(deps, c, m)
	if err != nil {
		return nil, instance.Settings{}, err
	}
	for _, n := range notes {
		fmt.Fprintln(cmd.ErrOrStderr(), "note:", n)
	}
	return engineOf(deps, c), s, nil
}
