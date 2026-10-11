package cli

import (
	"context"
	"fmt"
	"strconv"

	"github.com/spf13/cobra"

	"github.com/djlsystems/yawble/cli/internal/config"
	"github.com/djlsystems/yawble/cli/internal/doctor"
	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/instance"
)

func newWorkersCommand(deps Deps) *cobra.Command {
	var now, yes bool
	cmd := &cobra.Command{
		Use:   "workers [n]",
		Short: "How many worker containers run the agents; with a number, set it and apply it",
		Long: "Control serves the board and keeps the database; workers run the agents, each with the " +
			"memory and cpus in yawble's config. With no number, workers prints the count and each " +
			"worker's state. With a number (1 or more) it saves the count and applies it at once, " +
			"touching only worker containers, never the data volume: a new worker is started and " +
			"waited for until it connects to control. A worker removed, highest first, is told to take " +
			"no new run and stopped once its runs end; --now stops it at once, failing its runs as " +
			"worker-lost (the Manager re-sends each once), after a question --yes answers. A count whose " +
			"memory with control's allowance is more than the engine has is refused.",
		Example: "  yawble workers\n  yawble workers 3\n  yawble workers 1\n  yawble workers 1 --now --yes",
		Args:    cobra.MaximumNArgs(1),
		RunE: func(cmd *cobra.Command, args []string) error {
			if len(args) == 0 {
				return showWorkers(cmd, deps)
			}
			n, err := strconv.Atoi(args[0])
			if err != nil || n < 1 {
				return UsageError{fmt.Sprintf("workers needs a whole number, 1 or more, not %q: control runs no agent, so an instance needs at least one worker", args[0])}
			}
			return setWorkers(cmd, deps, n, now, yes)
		},
	}
	cmd.Flags().BoolVar(&now, "now", false, "stop removed workers at once, failing their runs as worker-lost")
	cmd.Flags().BoolVarP(&yes, "yes", "y", false, "answer yes to the --now question")
	return cmd
}

// setWorkers checks the count against the engine, saves it, and applies it to a running instance.
func setWorkers(cmd *cobra.Command, deps Deps, n int, now, yes bool) error {
	out := cmd.OutOrStdout()
	c, err := loadConfig(deps)
	if err != nil {
		return err
	}
	c.Workers = n
	m := measure(deps, c)
	s, _, err := settingsFor(deps, c, m)
	if err != nil {
		return err
	}
	if m.Measured {
		cpus, refusal := instance.Bound(m.MemoryMB(), m.CPUs, n, instance.MemoryMB(s.Memory), s.CPUs)
		if refusal != nil {
			if n >= 2 {
				return fmt.Errorf("%w (%s); the count was not changed", refusal, m.Source)
			}
			fmt.Fprintln(cmd.ErrOrStderr(), "warning:", refusal.Error())
		}
		if cpus != "" && n >= 2 {
			fmt.Fprintln(cmd.ErrOrStderr(), "warning:", cpus)
		}
	}
	if err := saveWorkers(deps, n); err != nil {
		return err
	}
	fmt.Fprintf(out, "workers = %d (saved in yawble's config)\n", n)
	e := engineOf(deps, c)
	if s, err = withWorkerKey(deps, s, out); err != nil {
		return err
	}
	state, err := e.ContainerState(cmd.Context(), instance.ContainerName)
	if err != nil {
		return err
	}
	if state != engine.StateRunning {
		fmt.Fprintf(out, "%s (control) is not running; `yawble up` starts it with %d worker(s)\n", instance.ContainerName, n)
		return nil
	}
	runsOf := doctor.RunsOf(func(ctx context.Context) (*doctor.HostReport, error) {
		return doctor.FetchHostReport(ctx, e, true)
	})
	return instance.Scale(cmd.Context(), e, s, runsOf, instance.ScaleOptions{
		Now:     now,
		Confirm: func(question string) (bool, error) { return confirm(deps, yes, out, question) },
	}, out)
}

// saveWorkers writes only the count into yawble's config file, read without the environment so a
// YAWBLE_* override in force now is not written into it.
func saveWorkers(deps Deps, n int) error {
	raw, _, err := config.Load(deps.ConfigDir, func(string) string { return "" })
	if err != nil {
		return err
	}
	raw.Workers = n
	_, err = config.Save(deps.ConfigDir, raw)
	return err
}

// showWorkers is the count and one line per worker: the engine's state and health, and what
// control recorded of it.
func showWorkers(cmd *cobra.Command, deps Deps) error {
	e, s, _, err := prepare(deps)
	if err != nil {
		return err
	}
	st, err := instance.GetStatus(cmd.Context(), e, s, healthChecker(deps.HTTP))
	if err != nil {
		return err
	}
	var record *doctor.WorkersRecord
	if st.Container == engine.StateRunning {
		if r, err := doctor.FetchHostReport(cmd.Context(), e, true); err == nil {
			record = r.Workers
		}
	}
	out := cmd.OutOrStdout()
	fmt.Fprintf(out, "workers    %d (yawble's config)\n", s.Workers)
	for _, w := range st.Workers {
		fmt.Fprintf(out, "%-10s %s\n", w.ID, workerLine(w, record))
	}
	return nil
}

// workerLine is one worker in a few words: the engine's state and health, then control's record.
func workerLine(w instance.WorkerStatus, record *doctor.WorkersRecord) string {
	line := string(w.Container)
	if w.Health != "" && w.Health != engine.HealthNone {
		line += ", " + string(w.Health)
	}
	if rec := record.Worker(w.ID); rec != nil {
		line += fmt.Sprintf("; control: %s, version %s, %s", rec.StateText(), rec.Version, rec.RunsText())
	} else if w.Container == engine.StateRunning {
		line += "; control: no record of it"
	}
	if len(w.Pending) > 0 {
		line += fmt.Sprintf("; pending %v", w.Pending)
	}
	return line
}
