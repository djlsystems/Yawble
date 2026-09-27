package cli

import (
	"context"
	"errors"
	"fmt"
	"io"
	"runtime"
	"strconv"
	"strings"

	"github.com/djlsystems/yawble/cli/internal/config"
	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/instance"
)

// chooseEngine is which container engine `up` uses. yawble installs none: it uses what is
// installed. A configured engine must be installed; with none
// configured, the one installed is used, and when both are the person chooses - Podman is the
// recommendation - and the choice is kept in yawble's config for every later command. With
// neither, it stops with where to get one.
func chooseEngine(deps Deps, c config.Config, yes bool, out io.Writer) (string, error) {
	installed := func(name string) bool {
		if deps.LookPath == nil {
			return name == "podman"
		}
		_, err := deps.LookPath(name)
		return err == nil
	}
	podman, docker := installed("podman"), installed("docker")

	if c.Engine != "" {
		if installed(c.Engine) {
			return c.Engine, nil
		}
		other := map[string]string{"podman": "docker", "docker": "podman"}[c.Engine]
		if other != "" && installed(other) {
			return "", fmt.Errorf("the engine is set to %s, which is not installed; install it, or use %s: yawble config set engine %s", c.Engine, other, other)
		}
		return "", noEngine(goosOf(deps))
	}

	switch {
	case podman && docker:
		name, err := askEngine(deps, yes, out)
		if err != nil {
			return "", err
		}
		if err := saveEngine(deps, name); err != nil {
			return "", err
		}
		fmt.Fprintf(out, "using %s; saved in yawble's config (yawble config set engine %s changes it)\n", engineLabel(name), map[string]string{"podman": "docker", "docker": "podman"}[name])
		return name, nil
	case podman:
		return "podman", nil
	case docker:
		return "docker", nil
	}
	return "", noEngine(goosOf(deps))
}

// supportedPlatform refuses what yawble does not support, before anything else `up` does or says:
// macOS on Apple silicon only for now, whichever engine is installed: Intel Macs are not tested yet.
func supportedPlatform(deps Deps) error {
	goarch := deps.GOARCH
	if goarch == "" {
		goarch = runtime.GOARCH
	}
	if goosOf(deps) == "darwin" && goarch != "arm64" {
		return errors.New("Intel Macs are not tested yet, so yawble runs on Apple silicon (M1 or later) only for now; this Mac has an Intel processor")
	}
	return nil
}

// askEngine asks which of the two installed engines to use; Enter takes Podman. --yes takes
// Podman too; no terminal and no --yes is a refusal naming the setting.
func askEngine(deps Deps, yes bool, out io.Writer) (string, error) {
	if yes {
		return "podman", nil
	}
	if !deps.Interactive || deps.Stdin == nil {
		return "", UsageError{"Both Podman and Docker are installed. Choose one: yawble config set engine podman (recommended), or yawble config set engine docker; or run yawble up from a terminal to be asked."}
	}
	fmt.Fprint(out, "Both Podman and Docker are installed. Which should Yawble use?\n  1) Podman (recommended)\n  2) Docker\nChoose [1]: ")
	line, _ := readLine(deps.Stdin)
	switch strings.ToLower(strings.TrimSpace(line)) {
	case "", "1", "podman":
		return "podman", nil
	case "2", "docker":
		return "docker", nil
	}
	return "", UsageError{fmt.Sprintf("%q is not 1 or 2; run yawble up again, or choose with yawble config set engine podman|docker", strings.TrimSpace(line))}
}

// saveEngine writes only the engine into yawble's config file. The file is read without the
// environment, so a YAWBLE_* override in force now is not written into it.
func saveEngine(deps Deps, name string) error {
	raw, _, err := config.Load(deps.ConfigDir, func(string) string { return "" })
	if err != nil {
		return err
	}
	if err := raw.Set("engine", name); err != nil {
		return err
	}
	_, err = config.Save(deps.ConfigDir, raw)
	return err
}

func engineLabel(name string) string {
	if name == "docker" {
		return "Docker"
	}
	return "Podman"
}

// noEngine is where to get a container engine, Podman first and recommended, then to run up.
func noEngine(goos string) error {
	var b strings.Builder
	b.WriteString("No container engine is installed. Yawble runs in one; install either, then run: yawble up\n")
	switch goos {
	case "windows", "darwin":
		b.WriteString("  Podman Desktop (recommended, free for everyone):  https://podman-desktop.io\n")
		b.WriteString("  Docker Desktop (free for personal use and small businesses):  https://www.docker.com/products/docker-desktop")
		if goos == "windows" {
			b.WriteString("\nEither one sets up WSL, which needs a restart of Windows.")
		}
	default:
		b.WriteString("  Podman (recommended):  https://podman.io/docs/installation\n")
		b.WriteString("  Docker Engine:  https://docs.docker.com/engine/install")
	}
	return errors.New(b.String())
}

// dockerMachine is what Docker says it can give a container - Docker Desktop's VM on macOS and
// Windows, the host on Linux - so the limits come from a measurement, not the unmeasured default.
func dockerMachine(ctx context.Context, r engine.Runner) instance.Machine {
	res, err := r.Run(ctx, "docker", "info", "--format", "{{.MemTotal}}|{{.NCPU}}")
	if err != nil || res.ExitCode != 0 {
		return instance.Machine{}
	}
	fields := strings.Split(strings.TrimSpace(res.Stdout), "|")
	if len(fields) != 2 {
		return instance.Machine{}
	}
	memory, errMemory := strconv.ParseInt(fields[0], 10, 64)
	cpus, errCPUs := strconv.Atoi(fields[1])
	if errMemory != nil || errCPUs != nil || memory <= 0 || cpus <= 0 {
		return instance.Machine{}
	}
	return instance.Machine{MemoryBytes: memory, CPUs: cpus, Measured: true}
}

// noteRestart says what `up` is about to stop when it replaces a RUNNING container whose settings
// changed (a new secret, image or limit). It does not ask: running `up` is the instruction, and a
// question would refuse scripts and count a person's own Concierge session as a reason to wait. With no agent process running it says nothing.
func noteRestart(ctx context.Context, e engine.Engine, s instance.Settings, out io.Writer) {
	info, err := e.Inspect(ctx, instance.ContainerName)
	if err != nil || info.State != engine.StateRunning {
		return
	}
	changes := instance.Changes(info.Label, s)
	if len(changes) == 0 {
		return
	}
	stopped := agentProcesses(listAgentProcesses(ctx, e))
	if len(stopped) == 0 {
		return
	}
	fmt.Fprintf(out, "restarting %s (%s): this stops %s\n", instance.ContainerName, strings.Join(changes, ", "), strings.Join(stopped, ", "))
}

// processListing prints one line per process: pid, working folder, command line, tab-separated.
// The same listing scripts/release-functions.ps1 reads.
const processListing = `for p in /proc/[0-9]*; do a=$(tr "\000" " " < "$p/cmdline" 2>/dev/null); [ -n "$a" ] || continue; printf "%s\t%s\t%s\n" "${p#/proc/}" "$(readlink "$p/cwd" 2>/dev/null)" "$a"; done`

// listAgentProcesses runs processListing AS THE AGENT USER: root in the container cannot read the
// working folder of another user's process (measured: empty), and the folder is what names a run.
// An image without that user falls back to root, and the runs are then named without a folder.
func listAgentProcesses(ctx context.Context, e engine.Engine) string {
	if res, err := e.Exec(ctx, instance.ContainerName, "runuser", "-u", "agent", "--", "sh", "-c", processListing); err == nil && res.ExitCode == 0 {
		return res.Stdout
	}
	if res, err := e.Exec(ctx, instance.ContainerName, "sh", "-c", processListing); err == nil {
		return res.Stdout
	}
	return ""
}

var agentCLIs = map[string]bool{"claude": true, "codex": true, "copilot": true, "grok": true, "gemini": true}

// agentProcesses names each agent run in a listing once, in order: "Team/Member (cli)" from a
// member's workspace or worktree, "a Concierge session (cli)" from the tenant's interactive
// workspaces, else "an agent (cli)". A CLI is the first argument's file name, or the second's when
// the first is node; a grep that mentions one is not one.
func agentProcesses(listing string) []string {
	seen := map[string]bool{}
	var names []string
	for _, line := range strings.Split(listing, "\n") {
		fields := strings.SplitN(line, "\t", 3)
		if len(fields) < 3 {
			continue
		}
		cli := ""
		for i, arg := range strings.Fields(fields[2]) {
			leaf := arg[strings.LastIndex(arg, "/")+1:]
			if agentCLIs[leaf] {
				cli = leaf
				break
			}
			if i > 0 || leaf != "node" {
				break
			}
		}
		if cli == "" {
			continue
		}
		name := "an agent (" + cli + ")"
		cwd := fields[1]
		switch {
		case strings.Contains(cwd, "/tenant-interactive-agent-workspaces/"):
			name = "a Concierge session (" + cli + ")"
		case teamMember(cwd) != "":
			name = teamMember(cwd) + " (" + cli + ")"
		}
		if !seen[name] {
			seen[name] = true
			names = append(names, name)
		}
	}
	return names
}

// teamMember is "Team/Member" for a folder under /teams/<team>/workspaces/<member> or
// /teams/<team>/repos/<repo>/wt-<member>, else "".
func teamMember(cwd string) string {
	parts := strings.Split(cwd, "/")
	for i, part := range parts {
		if part != "teams" || i+3 >= len(parts) {
			continue
		}
		team := parts[i+1]
		if parts[i+2] == "workspaces" {
			return team + "/" + parts[i+3]
		}
		if parts[i+2] == "repos" && i+4 < len(parts) && strings.HasPrefix(parts[i+4], "wt-") {
			return team + "/" + strings.TrimPrefix(parts[i+4], "wt-")
		}
	}
	return ""
}
