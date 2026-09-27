package cli

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
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

// measure is what the container's default limits come from. On Linux the host is the machine.
// On macOS and Windows the Podman machine is, and it is asked when it can be; when it is not
// running yet (before `up`'s preflight) nothing is measured, and Defaults says so.
func measure(deps Deps, c config.Config) instance.Machine {
	goos := deps.GOOS
	if goos == "" {
		goos = runtime.GOOS
	}
	if goos != "darwin" && goos != "windows" {
		return instance.Measure()
	}
	if engine.Select(c.Engine, deps.LookPath) == "docker" {
		// Docker Desktop's VM is not asked in this build; the person chooses with config set.
		return instance.Machine{}
	}
	info, err := machine.Inspect(context.Background(), runnerOf(deps), goos)
	if err != nil || !info.Running || info.MemoryMB == 0 {
		return instance.Machine{}
	}
	return machineOf(info)
}

func machineOf(info machine.Info) instance.Machine {
	if !info.Running || info.MemoryMB == 0 {
		return instance.Machine{}
	}
	return instance.Machine{MemoryBytes: int64(info.MemoryMB) << 20, CPUs: info.CPUs, Measured: true}
}

// prepare loads config, derives settings and picks the engine. Every lifecycle verb but `up`
// starts here; `up` does the same in pieces around its preflight.
func prepare(deps Deps) (engine.Engine, instance.Settings, []string, error) {
	c, err := loadConfig(deps)
	if err != nil {
		return nil, instance.Settings{}, nil, err
	}
	s, notes, err := settingsFor(deps, c, measure(deps, c))
	if err != nil {
		return nil, instance.Settings{}, nil, err
	}
	return engineOf(deps, c), s, notes, nil
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
			c, err := loadConfig(deps)
			if err != nil {
				return err
			}
			s, _, err := settingsFor(deps, c, instance.Machine{})
			if err != nil {
				return err
			}
			// Before the engine, the machine or an install is touched: with nothing to run there is
			// nothing to prepare the computer for.
			if s.Image == "" {
				return instance.ErrNoImage
			}
			if err := supportedPlatform(deps); err != nil {
				return err
			}
			// The engine installed (asked when both are); yawble installs none.
			name, err := chooseEngine(deps, c, yes, cmd.OutOrStdout())
			if err != nil {
				return err
			}
			c.Engine = name
			info, err := preflight(cmd.Context(), deps, yes, name, containerMemoryMB(c.Memory), cmd.OutOrStdout(), cmd.ErrOrStderr())
			if err != nil {
				return err
			}
			// The port, once the engine answers and before anything is created.
			port, err := ensurePort(cmd.Context(), deps, c, yes, cmd.OutOrStdout())
			if err != nil {
				return err
			}
			c.Port = port
			// Now the machine the container runs in is known: derive the limits from it. On Linux
			// that is the host; on macOS and Windows the Podman machine that just came up; with
			// Docker, what Docker says it can give (Docker Desktop's VM, or the host).
			m := instance.Measure()
			switch {
			case name == "docker":
				if measured := dockerMachine(cmd.Context(), runnerOf(deps)); measured.Measured {
					m = measured
				}
			case info.Applies:
				m = machineOf(info)
			}
			// Asked once, before the settings are read, so a token saved now reaches the container
			// on this same run.
			offerGitHub(cmd.Context(), deps, c, yes, cmd.OutOrStdout())
			s, notes, err := settingsFor(deps, c, m)
			if err != nil {
				return err
			}
			for _, n := range notes {
				fmt.Fprintln(cmd.ErrOrStderr(), "note:", n)
			}
			noteRestart(cmd.Context(), engineOf(deps, c), s, cmd.OutOrStdout())
			if err := instance.Up(cmd.Context(), engineOf(deps, c), s, healthChecker(deps.HTTP), cmd.OutOrStdout()); err != nil {
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
		Short:   "Whether the instance runs, its URL, engine and image",
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
			if asJSON {
				enc := json.NewEncoder(cmd.OutOrStdout())
				enc.SetIndent("", "  ")
				return enc.Encode(st)
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
