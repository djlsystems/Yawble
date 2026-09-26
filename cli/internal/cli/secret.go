package cli

import (
	"errors"
	"fmt"
	"strings"

	"github.com/spf13/cobra"

	"github.com/djlsystems/yawble/cli/internal/config"
)

// newSecretCommand manages the container's secrets and settings - GH_TOKEN, provider API keys -
// in yawble's env file, which `up` hands to the container, so a revoked
// GH_TOKEN has one visible place to be replaced. Values are never printed.
func newSecretCommand(deps Deps) *cobra.Command {
	cmd := &cobra.Command{
		Use:   "secret",
		Short: "Set the secrets the instance uses (GH_TOKEN, provider API keys); values are never shown",
		Long: "yawble keeps the instance's secrets and settings - GH_TOKEN for GitHub, ANTHROPIC_API_KEY, " +
			"OPENAI_API_KEY, XAI_API_KEY and the like - in a file readable only by you, and `yawble up` " +
			"hands them to the container. A change applies at the next `yawble up`, which recreates the " +
			"container on the same volume; nothing is lost. Values are never printed.",
		Example: "  gh auth token | yawble secret set GH_TOKEN   # piped: never on screen or in history\n" +
			"  yawble secret set ANTHROPIC_API_KEY          # asks, typing hidden\n" +
			"  yawble secret list\n" +
			"  yawble secret unset XAI_API_KEY",
		Args: cobra.NoArgs,
		RunE: func(cmd *cobra.Command, _ []string) error { return cmd.Help() },
	}
	cmd.AddCommand(newSecretSet(deps), newSecretList(deps), newSecretUnset(deps))
	return cmd
}

func newSecretSet(deps Deps) *cobra.Command {
	return &cobra.Command{
		Use:   "set NAME",
		Short: "Set a secret: piped in, typed at a hidden prompt, or NAME=value",
		Args:  cobra.ExactArgs(1),
		RunE: func(cmd *cobra.Command, args []string) error {
			name, value, inline := strings.Cut(args[0], "=")
			if inline {
				// On the command line the value is in the shell's history and, while this runs,
				// visible to other programs. Accepted, and said.
				fmt.Fprintln(cmd.ErrOrStderr(), "note: a value on the command line is kept in your shell's history; next time pipe it in or type it at the prompt")
			} else {
				var err error
				value, err = readSecretValue(deps, name)
				if err != nil {
					return err
				}
			}
			value = strings.TrimSpace(value)
			if value == "" {
				return UsageError{fmt.Sprintf("the value for %s is empty; nothing was changed (yawble secret unset %s removes it)", name, name)}
			}
			if err := config.ValidateSecret(name, value); err != nil {
				return UsageError{err.Error()}
			}
			if err := config.SetSecret(deps.ConfigDir, name, value); err != nil {
				return err
			}
			fmt.Fprintf(cmd.OutOrStdout(), "%s is set in %s. Run yawble up to hand it to the instance.\n", name, config.EnvFile(deps.ConfigDir))
			return nil
		},
	}
}

// readSecretValue is the value from a pipe, or from a hidden prompt at a terminal. With neither
// it is a usage error naming both ways, never a wait on a stdin nobody is typing into.
func readSecretValue(deps Deps, name string) (string, error) {
	if deps.Interactive {
		if deps.ReadSecret == nil {
			return "", UsageError{fmt.Sprintf("cannot ask for %s here; pipe it in: <command> | yawble secret set %s", name, name)}
		}
		return deps.ReadSecret(fmt.Sprintf("Value for %s (typing is hidden): ", name))
	}
	if deps.Stdin == nil {
		return "", UsageError{fmt.Sprintf("no value for %s: pipe it in (<command> | yawble secret set %s) or run this in a terminal to be asked", name, name)}
	}
	line, err := readLine(deps.Stdin)
	if err != nil && line == "" {
		return "", UsageError{fmt.Sprintf("no value for %s was piped in", name)}
	}
	return line, nil
}

func newSecretList(deps Deps) *cobra.Command {
	return &cobra.Command{
		Use:   "list",
		Short: "The names of the secrets set (never the values)",
		Args:  cobra.NoArgs,
		RunE: func(cmd *cobra.Command, _ []string) error {
			names, err := config.SecretNames(deps.ConfigDir)
			if err != nil {
				return err
			}
			out := cmd.OutOrStdout()
			if len(names) == 0 {
				fmt.Fprintln(out, "no secrets are set (yawble secret set NAME)")
				return nil
			}
			for _, name := range names {
				fmt.Fprintln(out, name)
			}
			return nil
		},
	}
}

func newSecretUnset(deps Deps) *cobra.Command {
	return &cobra.Command{
		Use:   "unset NAME",
		Short: "Remove a secret",
		Args:  cobra.ExactArgs(1),
		RunE: func(cmd *cobra.Command, args []string) error {
			removed, err := config.UnsetSecret(deps.ConfigDir, args[0])
			if err != nil {
				return err
			}
			if !removed {
				return errors.New(args[0] + " is not set; yawble secret list shows what is")
			}
			fmt.Fprintf(cmd.OutOrStdout(), "%s is removed. Run yawble up to take it out of the instance.\n", args[0])
			return nil
		},
	}
}
