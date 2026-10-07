package cli

import (
	"encoding/json"
	"fmt"

	"github.com/spf13/cobra"

	"github.com/djlsystems/yawble/cli/internal/doctor"
	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/instance"
)

func newAgentsCommand(deps Deps) *cobra.Command {
	var asJSON bool
	cmd := &cobra.Command{
		Use:   "agents",
		Short: "Each agent CLI inside the instance: installed, signed in, and how to sign in if not",
		Long: "Reads the instance's own report of its agent CLIs (the Host's --doctor). Sign-in happens " +
			"in the board: open a Concierge on the agent and log in there. A provider key is the other way: " +
			"`yawble secret set <KEY>` (ANTHROPIC_API_KEY, OPENAI_API_KEY, ...), then `yawble up` hands it to the container.\n" +
			"With one agent able to run, the others are listed as not in use, with how to use them; only " +
			"when no agent can run is there something to fix.\n\n" +
			"Each agent also shows its source: home signs in through the shared home, issued uses the one " +
			"credential stored for its command (`yawble agents credential set`), chosen per preset with " +
			"`yawble agents source`.",
		Example: "  yawble agents\n  yawble agents --json\n  yawble agents source claude-headless issued\n  yawble agents credential set claude",
		Args:    cobra.NoArgs,
		RunE: func(cmd *cobra.Command, _ []string) error {
			e, _, _, err := prepare(deps)
			if err != nil {
				return err
			}
			state, err := e.ContainerState(cmd.Context(), instance.ContainerName)
			if err != nil {
				return err
			}
			if state != engine.StateRunning {
				return fmt.Errorf("the instance is not running; run `yawble up` first, then `yawble agents`")
			}
			report, err := doctor.FetchHostReport(cmd.Context(), e, true)
			if err != nil {
				return err
			}
			if asJSON {
				type withHint struct {
					doctor.Agent
					Hint string `json:"hint"`
				}
				rows := make([]withHint, 0, len(report.Agents))
				for _, a := range report.Agents {
					rows = append(rows, withHint{a, doctor.SignInHint(a)})
				}
				enc := json.NewEncoder(cmd.OutOrStdout())
				enc.SetIndent("", "  ")
				return enc.Encode(rows)
			}
			doctor.RenderAgents(cmd.OutOrStdout(), report.Agents)
			return nil
		},
	}
	cmd.Flags().BoolVar(&asJSON, "json", false, "print JSON")
	cmd.AddCommand(newAgentsCredentialCommand(deps), newAgentsSourceCommand(deps))
	return cmd
}
