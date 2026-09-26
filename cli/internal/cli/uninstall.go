package cli

import (
	"fmt"
	"strings"

	"github.com/spf13/cobra"

	"github.com/djlsystems/yawble/cli/internal/instance"
)

func newUninstallCommand(deps Deps) *cobra.Command {
	var data, yes bool
	cmd := &cobra.Command{
		Use:   "uninstall",
		Short: "Remove the tunnel, the container, the pod and the image; the data volume only with --data",
		Long: "uninstall removes what `up` created: the tunnel sidecar, the container, the pod (or " +
			"network) and the image. The data volume, which holds every team, account, document and " +
			"saved agent login, is removed only with --data, and only after you type the word delete at " +
			"a terminal (or pass --yes together with --data from a script). yawble's own config directory " +
			"is left in place and named at the end.",
		Example: "  yawble uninstall\n  yawble uninstall --data\n  yawble uninstall --data --yes",
		Args:    cobra.NoArgs,
		RunE: func(cmd *cobra.Command, _ []string) error {
			e, s, _, err := prepare(deps)
			if err != nil {
				return err
			}
			out := cmd.OutOrStdout()
			removeData := false
			if data {
				switch {
				case yes:
					removeData = true
				case deps.Interactive && deps.Stdin != nil:
					fmt.Fprintf(out, "Type delete to remove the volume %s and everything on it (teams, accounts, documents, agent logins): ", instance.VolumeName)
					line, _ := readLine(deps.Stdin)
					removeData = strings.TrimSpace(line) == "delete"
					if !removeData {
						fmt.Fprintln(out, "not 'delete': the volume is kept")
					}
				default:
					fmt.Fprintf(out, "--data needs the word delete typed at a terminal, or --yes together with --data; the volume %s is kept\n", instance.VolumeName)
				}
			}
			if err := instance.Uninstall(cmd.Context(), e, s.Image, removeData, out); err != nil {
				return err
			}
			fmt.Fprintf(out, "yawble's own settings stay in %s (delete that directory to forget them, including any remote credential)\n", deps.ConfigDir)
			return nil
		},
	}
	cmd.Flags().BoolVar(&data, "data", false, "also remove the data volume (asks for the word delete)")
	cmd.Flags().BoolVarP(&yes, "yes", "y", false, "with --data: remove the volume without the prompt")
	return cmd
}
