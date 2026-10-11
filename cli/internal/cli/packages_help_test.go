package cli_test

import (
	"strings"
	"testing"

	"github.com/spf13/cobra"

	"github.com/djlsystems/yawble/cli/internal/cli"
)

// Packages to install are on the marketplace; the repository holds none. No command's help names a
// samples/ folder, and the plugin and solution commands say where packages come from.
func TestNoHelpNamesASamplesFolderAndThePackageCommandsNameTheMarketplace(t *testing.T) {
	var walk func(*cobra.Command)
	walk = func(c *cobra.Command) {
		help := c.Short + "\n" + c.Long + "\n" + c.Example
		if strings.Contains(help, "samples/") || strings.Contains(help, `samples\`) {
			t.Errorf("%q names a samples folder:\n%s", c.CommandPath(), help)
		}
		for _, sub := range c.Commands() {
			walk(sub)
		}
	}
	root := cli.NewRoot(cli.Deps{})
	walk(root)

	for _, path := range [][]string{{"plugin"}, {"solution"}, {"solution", "check"}, {"solution", "install"}} {
		c, _, err := root.Find(path)
		if err != nil {
			t.Fatal(err)
		}
		if !strings.Contains(c.Long, "marketplace, yawble.ai") {
			t.Errorf("%q does not name the marketplace: %s", c.CommandPath(), c.Long)
		}
	}
	if c, _, _ := root.Find([]string{"plugin"}); !strings.Contains(c.Long, "templates/plugin-go") {
		t.Errorf("`plugin` does not name the Go template: %s", c.Long)
	}
}
