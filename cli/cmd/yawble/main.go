// yawble is the operator CLI for a Yawble instance. See internal/cli for the commands.
package main

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"

	"github.com/djlsystems/yawble/cli/internal/cli"
	"github.com/djlsystems/yawble/cli/internal/release"
	"golang.org/x/term"
)

func main() {
	dir, err := os.UserConfigDir()
	if err != nil {
		fmt.Fprintln(os.Stderr, "yawble: cannot find the user config directory:", err)
		os.Exit(1)
	}
	// A Windows `update --cli` leaves the previous binary beside this one; sweep it now.
	if exe, err := os.Executable(); err == nil {
		release.SweepOld(exe)
	}
	// A terminal on stdin is what makes a yes/no question possible; a pipe gets --yes or a refusal.
	interactive := false
	if info, err := os.Stdin.Stat(); err == nil && info.Mode()&os.ModeCharDevice != 0 {
		interactive = true
	}
	// A terminal on stdout is what the mark is drawn for; a pipe or a file gets plain text.
	stdoutTerminal := false
	if info, err := os.Stdout.Stat(); err == nil && info.Mode()&os.ModeCharDevice != 0 {
		stdoutTerminal = true
	}
	os.Exit(cli.Execute(cli.NewRoot(cli.Deps{
		Stdout:         os.Stdout,
		Stderr:         os.Stderr,
		Env:            os.Getenv,
		ConfigDir:      filepath.Join(dir, "yawble"),
		LookPath:       exec.LookPath,
		Stdin:          os.Stdin,
		Interactive:    interactive,
		StdoutTerminal: stdoutTerminal,
		ReadSecret:     readHidden,
	}), os.Args[1:]))
}

// readHidden shows prompt on stderr and reads a line from the terminal without echoing it, so a
// secret typed at `yawble secret set NAME` is never on screen.
func readHidden(prompt string) (string, error) {
	fmt.Fprint(os.Stderr, prompt)
	value, err := term.ReadPassword(int(os.Stdin.Fd()))
	fmt.Fprintln(os.Stderr)
	return string(value), err
}
