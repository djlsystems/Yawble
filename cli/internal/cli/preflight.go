package cli

import (
	"context"
	"errors"
	"fmt"
	"io"
	"runtime"
	"strings"

	"github.com/djlsystems/yawble/cli/internal/doctor"
	"github.com/djlsystems/yawble/cli/internal/machine"
)

// confirm asks a yes/no question. --yes answers it; no terminal and no --yes is a refusal that
// names the flag, because a script that pipes into yawble must not find a prompt waiting.
func confirm(deps Deps, yes bool, out io.Writer, question string) (bool, error) {
	if yes {
		return true, nil
	}
	if !deps.Interactive || deps.Stdin == nil {
		return false, UsageError{question + " This needs a yes: run again with --yes, or from a terminal."}
	}
	fmt.Fprint(out, question, " [y/N] ")
	line, _ := readLine(deps.Stdin)
	answer := strings.ToLower(strings.TrimSpace(line))
	return answer == "y" || answer == "yes", nil
}

// preflight is what `up` does before it can talk to the engine `chooseEngine` picked: on macOS
// and Windows with Podman, that its machine exists, runs, and is rootless. yawble installs no
// engine; every change to the computer is asked about; reading and starting are not.
// It answers the machine's facts as they are afterwards, because the container's default limits
// come from them.
func preflight(ctx context.Context, deps Deps, yes bool, engineName string, memoryMB int, out, errOut io.Writer) (machine.Info, error) {
	goos := deps.GOOS
	if goos == "" {
		goos = runtime.GOOS
	}
	r := runnerOf(deps)

	// Docker: no Podman machine to mind. Docker Desktop runs its own VM (and sets up WSL on
	// Windows); the engine check in doctor says when it is not answering.
	if engineName == "docker" {
		fmt.Fprintln(out, "using Docker")
		return machine.Info{}, nil
	}

	// Podman on Windows runs its machine in WSL. Without it, `podman machine init` would start
	// installing WSL itself and finish after a restart in a window of its own, so WSL is the person's step first.
	if goos == "windows" && !machine.WSLAnswers(ctx, r) {
		return machine.Info{}, errWSLMissing
	}

	// 2. The machine, where there is one.
	info, err := machine.Inspect(ctx, r, goos)
	if err != nil {
		return info, err
	}
	if !info.Applies {
		return info, nil
	}
	if goos == "windows" && info.WSL != nil && !*info.WSL {
		// Spec: WSL not enabled is "print the wsl --install line and stop". Podman's own
		// machine init would otherwise fail or ask its own question on a stdin nobody watches.
		return info, errWSLMissing
	}
	if !info.Exists {
		size := ""
		memory := 0
		if goos == "darwin" {
			// Half the Mac's RAM, capped at 12 GB: the machine is the bound the container
			// limit is later derived from, so it is sized from the host once, here.
			if mb := machine.HostMemoryMB(ctx, r, goos); mb > 0 {
				memory = min(mb/2, 12288)
				size = fmt.Sprintf(", %d MB", memory)
			}
		}
		ok, err := confirm(deps, yes, out, fmt.Sprintf("No Podman machine exists yet. Create one now (rootless%s)?", size))
		if err != nil {
			return info, err
		}
		if !ok {
			return info, fmt.Errorf("a Podman machine is needed; create one with `podman machine init --rootful=false` and run yawble up again")
		}
		fmt.Fprintln(out, "creating the podman machine (this downloads its image once; a few minutes)")
		if err := machine.Init(ctx, r, memory, 0); err != nil {
			return info, err
		}
		info.Exists = true
	}
	if !info.Running {
		fmt.Fprintln(out, "starting the podman machine")
		if err := machine.Start(ctx, r); err != nil {
			return info, err
		}
		info.Running = true
	}
	if info.Rootful {
		why := "the machine flag is what keeps it rootless across restarts"
		if goos == "windows" {
			why = "on Windows a rootful machine never answers on localhost"
		}
		ok, err := confirm(deps, yes, out, "The Podman machine is rootful; "+why+". Make it rootless now? (stops and restarts the machine)")
		if err != nil {
			return info, err
		}
		if ok {
			fmt.Fprintln(out, "making the podman machine rootless (stop, set, start)")
			if err := machine.Stop(ctx, r); err != nil {
				return info, err
			}
			if err := machine.SetRootless(ctx, r); err != nil {
				return info, err
			}
			if err := machine.Start(ctx, r); err != nil {
				return info, err
			}
			info.Rootful = false
		} else {
			fmt.Fprintln(errOut, "note: the machine stays rootful; `yawble doctor` will keep saying so"+map[bool]string{true: ", and on Windows http://localhost will not answer", false: ""}[goos == "windows"])
		}
	}

	// 3. Re-read the facts now that the machine runs: memory is measured only then.
	if fresh, err := machine.Inspect(ctx, r, goos); err == nil {
		info = fresh
	}
	// memoryMB is a limit a person configured, 0 when the limit will be derived from the machine
	// (half of it, which always fits): only a configured one can be too big for the machine.
	if info.Running && memoryMB > 0 && info.MemoryMB > 0 && info.MemoryMB < memoryMB {
		fmt.Fprintln(errOut, "note: "+doctor.MachineMemoryNote(goos, info.MemoryMB, memoryMB, machine.HostMemoryMB(ctx, r, goos)))
	}
	return info, nil
}

// errWSLMissing is the three steps a Windows computer without WSL needs. WSL needs a restart, so
// it comes first, done by the person, sitting at the computer: Windows refuses `wsl --install`
// over a remote session (PowerShell Direct, SSH), elevated or not.
var errWSLMissing = errors.New("WSL is not installed, and Podman runs its machine in it. It needs a restart, so it comes first; nothing has been installed yet.\n" +
	"  1. Sitting at the computer (not over a remote session), right-click Start, open Terminal (Admin), and run:  wsl --install --no-distribution\n" +
	"  2. Restart Windows.\n" +
	"  3. Open PowerShell and run:  yawble up")

// containerMemoryMB is the limit `up` will ask for, from the settings' podman size string.
func containerMemoryMB(size string) int { return doctor.ContainerMemoryMB(size) }
