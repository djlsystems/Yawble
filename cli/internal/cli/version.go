package cli

import (
	"encoding/json"
	"fmt"

	"github.com/spf13/cobra"

	"github.com/djlsystems/yawble/cli/internal/buildinfo"
)

func newVersionCommand(deps Deps) *cobra.Command {
	var asJSON bool
	cmd := &cobra.Command{
		Use:     "version",
		Short:   "This binary's version and the Yawble image it pins",
		Example: "  yawble version\n  yawble version --json",
		Args:    cobra.NoArgs,
		RunE: func(cmd *cobra.Command, _ []string) error {
			if asJSON {
				return json.NewEncoder(cmd.OutOrStdout()).Encode(map[string]string{
					"version": buildinfo.Version,
					"commit":  buildinfo.Commit,
					"image":   buildinfo.Image(),
				})
			}
			out := cmd.OutOrStdout()
			fmt.Fprintf(out, "yawble %s", buildinfo.Version)
			if buildinfo.Commit != "" {
				fmt.Fprintf(out, " (%s)", buildinfo.Commit)
			}
			fmt.Fprintln(out)
			if image := buildinfo.Image(); image != "" {
				fmt.Fprintf(out, "image   %s\n", image)
			} else {
				fmt.Fprintln(out, "image   no image pinned (a dev build; choose one with: yawble config set image <reference>)")
			}
			return nil
		},
	}
	cmd.Flags().BoolVar(&asJSON, "json", false, "print JSON")
	return cmd
}
