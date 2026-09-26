package cli

import (
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"

	"github.com/spf13/cobra"

	"github.com/djlsystems/yawble/cli/internal/doctor"
	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/instance"
	"github.com/djlsystems/yawble/cli/internal/machine"
)

// errChecksFailed is `doctor` with at least one FAIL. The list has already been printed;
// Execute adds one line to stderr and exits 1.
var errChecksFailed = errors.New("one or more checks failed")

func newDoctorCommand(deps Deps) *cobra.Command {
	var asJSON, fix bool
	cmd := &cobra.Command{
		Use:   "doctor",
		Short: "Check the engine, the instance and what runs inside it; say how to fix what is wrong",
		Long: "Every check is ok, warn, FAIL or skip. skip means it could not be measured (a stopped " +
			"instance has no health to check), and skip is not failure. Exit 1 when anything FAILs.\n" +
			"--fix repairs only what is safe and idempotent: it creates a missing data volume and starts " +
			"a stopped container, then waits for it to answer. It never removes anything.",
		Example: "  yawble doctor\n  yawble doctor --fix\n  yawble doctor --json",
		Args:    cobra.NoArgs,
		RunE: func(cmd *cobra.Command, _ []string) error {
			e, s, _, err := prepare(deps)
			if err != nil {
				return err
			}
			out := cmd.OutOrStdout()
			probes := doctor.Probes{Health: healthChecker(deps.HTTP), PortFree: deps.PortFree, Runner: runnerOf(deps), GOOS: deps.GOOS, ConfigDir: deps.ConfigDir, HTTP: deps.HTTP, GitHubAPI: deps.ReleaseBaseURL}
			exeDir := ""
			if exe, err := os.Executable(); err == nil {
				exeDir = filepath.Dir(exe)
			}
			observed := doctor.Observe(cmd.Context(), e, s, probes, exeDir, deps.Env("PATH"))

			// With --json the prose must not precede the document; the repairs are in it instead.
			var fixed []string
			if fix && observed.EngineErr == nil {
				progress := out
				if asJSON {
					progress = io.Discard
				}
				fixed, err = repair(cmd, e, s, observed, probes, progress)
				if err != nil {
					return err
				}
				if len(fixed) > 0 {
					observed = doctor.Observe(cmd.Context(), e, s, probes, exeDir, deps.Env("PATH"))
				}
			}

			checks := doctor.HostChecks(observed)
			var report *doctor.HostReport
			var reportErr error
			if observed.EngineErr != nil {
				reportErr = doctor.ErrEngineUnavailable
			} else {
				report, reportErr = doctor.FetchHostReport(cmd.Context(), e, observed.ContainerKnown && observed.Container == engine.StateRunning)
			}
			checks = append(checks, doctor.InstanceChecks(report, reportErr, deps.Now())...)

			if asJSON {
				enc := json.NewEncoder(out)
				enc.SetIndent("", "  ")
				if fixed == nil {
					fixed = []string{}
				}
				if err := enc.Encode(map[string]any{"checks": checks, "fixed": fixed, "instance": report}); err != nil {
					return err
				}
			} else {
				doctor.Render(out, checks)
			}
			if doctor.AnyFailed(checks) {
				return errChecksFailed
			}
			return nil
		},
	}
	cmd.Flags().BoolVar(&asJSON, "json", false, "print JSON")
	cmd.Flags().BoolVar(&fix, "fix", false, "create a missing volume and start a stopped container, then check again")
	return cmd
}

// repair is what --fix may do: create the volume, start the container. Each line it prints is
// also returned, for the JSON. A started container is waited for the way `up` waits, because a
// health check taken the instant `podman start` returns would fail an instance that is fine.
func repair(cmd *cobra.Command, e engine.Engine, s instance.Settings, o doctor.Observed, p doctor.Probes, progress io.Writer) ([]string, error) {
	var fixed []string
	say := func(line string) {
		fixed = append(fixed, line)
		fmt.Fprintln(progress, "fixed:", line)
	}
	// The machine first: nothing behind a stopped machine can be observed, let alone repaired.
	// Starting is safe and idempotent; creating or re-rooting a machine is `up`'s to offer.
	if o.Machine.Applies && o.MachineErr == nil && o.Machine.Exists && !o.Machine.Running {
		if err := machine.Start(cmd.Context(), p.Runner); err != nil {
			return fixed, err
		}
		say("started the podman machine " + o.Machine.Name)
		// Everything below was not measured; the caller re-observes and, when asked again,
		// repairs the rest on that second pass.
		return fixed, nil
	}
	if o.VolumePresent != nil && !*o.VolumePresent {
		if err := e.CreateVolume(cmd.Context(), instance.VolumeName); err != nil {
			return fixed, err
		}
		say("created volume " + instance.VolumeName)
	}
	if o.ContainerKnown && o.Container == engine.StateStopped {
		if err := e.Start(cmd.Context(), instance.ContainerName); err != nil {
			return fixed, err
		}
		say("started " + instance.ContainerName)
		if err := instance.WaitHealthy(cmd.Context(), e, o.URL, p.Health, progress); err != nil {
			return fixed, err
		}
	}
	return fixed, nil
}
