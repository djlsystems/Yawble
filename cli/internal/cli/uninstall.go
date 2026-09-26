package cli

import (
	"errors"
	"fmt"
	"io"
	"io/fs"
	"os"
	"path/filepath"
	"strings"

	"github.com/spf13/cobra"

	"github.com/djlsystems/yawble/cli/internal/config"
	"github.com/djlsystems/yawble/cli/internal/instance"
	"github.com/djlsystems/yawble/cli/internal/remote"
)

// settingsFiles are the files yawble writes into its config directory: preferences, the secrets
// `secret set` stores, and remote access. Uninstall removes these and nothing else it cannot vouch for.
var settingsFiles = []string{config.FileName, "env", remote.FileName, remote.EnvFileName}

func newUninstallCommand(deps Deps) *cobra.Command {
	var data, yes, keepSettings bool
	cmd := &cobra.Command{
		Use:   "uninstall",
		Short: "Remove the instance and yawble's settings; the data volume only with --data",
		Long: "uninstall removes what yawble set up: the tunnel sidecar, the container, the pod (or " +
			"network), the image, and yawble's settings - the engine and port choices, the API keys and " +
			"tokens saved with `secret set`, and remote-access credentials. It asks first; --yes answers " +
			"for you. --keep-settings keeps the settings, for a reinstall or a switch of engine.\n\n" +
			"The data volume, which holds every team, account, document and saved agent login, is " +
			"removed only with --data, and only after you type the word delete at a terminal (or pass " +
			"--yes together with --data from a script). At the end it says how to remove the yawble " +
			"program itself.",
		Example: "  yawble uninstall\n  yawble uninstall --keep-settings\n  yawble uninstall --data\n  yawble uninstall --data --yes",
		Args:    cobra.NoArgs,
		RunE: func(cmd *cobra.Command, _ []string) error {
			e, s, _, err := prepare(deps)
			if err != nil {
				return err
			}
			out := cmd.OutOrStdout()

			what := "the Yawble container, pod and image"
			if !keepSettings {
				what += fmt.Sprintf(", and yawble's settings in %s (engine and port, saved API keys and tokens, remote access)", deps.ConfigDir)
			}
			if !yes {
				if !deps.Interactive || deps.Stdin == nil {
					return UsageError{"uninstall removes " + what + ". Run it from a terminal to confirm, or again with --yes."}
				}
				ok, err := confirm(deps, false, out, "Remove "+what+"?")
				if err != nil {
					return err
				}
				if !ok {
					fmt.Fprintln(out, "nothing was removed")
					return nil
				}
			}

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

			if keepSettings {
				fmt.Fprintf(out, "yawble's settings are kept in %s\n", deps.ConfigDir)
			} else if err := removeSettings(deps.ConfigDir, out); err != nil {
				return err
			}
			programHint(deps, out)
			return nil
		},
	}
	cmd.Flags().BoolVar(&data, "data", false, "also remove the data volume (asks for the word delete)")
	cmd.Flags().BoolVar(&keepSettings, "keep-settings", false, "keep yawble's settings: engine, port, saved keys and tokens, remote access")
	cmd.Flags().BoolVarP(&yes, "yes", "y", false, "answer yes: remove without asking, and with --data remove the volume without the prompt")
	return cmd
}

// removeSettings deletes the files yawble writes and then the folder, if nothing else is in it.
// A file yawble did not write is left where it is and named.
func removeSettings(dir string, out io.Writer) error {
	for _, name := range settingsFiles {
		if err := os.Remove(filepath.Join(dir, name)); err != nil && !errors.Is(err, fs.ErrNotExist) {
			return fmt.Errorf("removing %s: %w", filepath.Join(dir, name), err)
		}
	}
	left, err := os.ReadDir(dir)
	if errors.Is(err, fs.ErrNotExist) {
		fmt.Fprintln(out, "removed yawble's settings")
		return nil
	}
	if err != nil {
		return err
	}
	if len(left) == 0 {
		if err := os.Remove(dir); err != nil && !errors.Is(err, fs.ErrNotExist) {
			return fmt.Errorf("removing %s: %w", dir, err)
		}
		fmt.Fprintf(out, "removed yawble's settings (%s)\n", dir)
		return nil
	}
	names := make([]string, 0, len(left))
	for _, entry := range left {
		names = append(names, entry.Name())
	}
	fmt.Fprintf(out, "removed yawble's settings; %s still holds %s, which yawble did not write, so it was left\n", dir, strings.Join(names, ", "))
	return nil
}

// programHint says how to remove the yawble program: a running program cannot reliably delete
// itself on Windows, so it is the person's one step, named exactly.
func programHint(deps Deps, out io.Writer) {
	path, err := os.Executable()
	if err != nil || path == "" {
		path = "yawble"
	}
	if goosOf(deps) == "windows" {
		fmt.Fprintf(out, "To remove the yawble program itself: Remove-Item \"%s\"\n", path)
		return
	}
	fmt.Fprintf(out, "To remove the yawble program itself: rm \"%s\"\n", path)
}
