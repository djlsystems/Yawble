package cli

import (
	"encoding/json"
	"fmt"

	"github.com/spf13/cobra"

	"github.com/djlsystems/yawble/cli/internal/buildinfo"
	"github.com/djlsystems/yawble/cli/internal/config"
	"github.com/djlsystems/yawble/cli/internal/instance"
)

func newConfigCommand(deps Deps) *cobra.Command {
	cmd := &cobra.Command{
		Use:   "config",
		Short: "Read or change yawble's own settings (engine, port, memory, cpus, maxRunning, image, channel)",
	}
	var asJSON bool
	get := &cobra.Command{
		Use:     "get [key]",
		Short:   "Print one setting, or all of them with --json",
		Example: "  yawble config get port\n  yawble config get --json",
		Args:    cobra.MaximumNArgs(1),
		RunE: func(cmd *cobra.Command, args []string) error {
			c, _, err := config.Load(deps.ConfigDir, deps.Env)
			if err != nil {
				return err
			}
			if len(args) == 0 {
				if !asJSON {
					return UsageError{"config get needs a key, or --json for all of them"}
				}
				enc := json.NewEncoder(cmd.OutOrStdout())
				enc.SetIndent("", "  ")
				return enc.Encode(effective(deps, c))
			}
			v, err := c.Get(args[0])
			if err != nil {
				return UsageError{err.Error()}
			}
			fmt.Fprintln(cmd.OutOrStdout(), v)
			return nil
		},
	}
	get.Flags().BoolVar(&asJSON, "json", false, "print every setting as JSON, defaults applied")
	set := &cobra.Command{
		Use:     "set <key> <value>",
		Short:   "Change one setting; the next `yawble up` applies it",
		Example: "  yawble config set port 8081\n  yawble config set image ghcr.io/djlsystems/yawble:2026.09.24.1",
		Args:    cobra.ExactArgs(2),
		RunE: func(cmd *cobra.Command, args []string) error {
			// The FILE, not the effective config: an exported YAWBLE_PORT must not be copied into
			// the file by an unrelated `set cpus`.
			c, _, err := config.Load(deps.ConfigDir, func(string) string { return "" })
			if err != nil {
				return err
			}
			if err := c.Set(args[0], args[1]); err != nil {
				return UsageError{err.Error()}
			}
			path, err := config.Save(deps.ConfigDir, c)
			if err != nil {
				return err
			}
			fmt.Fprintf(cmd.OutOrStdout(), "%s = %s (written to %s; applied by the next `yawble up`)\n", args[0], args[1], path)
			return nil
		},
	}
	cmd.AddCommand(get, set)
	return cmd
}

// effective is the config with every default applied: what a run would use. The derived ones
// come from instance.Defaults, so this and `up` can never disagree.
func effective(deps Deps, c config.Config) config.Config {
	s, _ := instance.Defaults(c, measure(deps, c), buildinfo.Image())
	return config.Config{
		Engine:     c.Engine,
		Port:       s.Port,
		Memory:     s.Memory,
		CPUs:       s.CPUs,
		MaxRunning: s.MaxRunning,
		Image:      s.Image,
	}
}
