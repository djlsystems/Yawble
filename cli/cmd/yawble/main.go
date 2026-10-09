// yawble is the operator CLI for a Yawble instance. See internal/cli for the commands.
package main

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"

	"github.com/djlsystems/yawble/cli/internal/cli"
	"github.com/djlsystems/yawble/cli/internal/maskedinput"
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
		Color:          stdoutTerminal && os.Getenv("NO_COLOR") == "" && enableColor(),
		ReadSecret:     readHidden,
	}), os.Args[1:]))
}

// readHidden shows prompt on stderr and reads a line from the terminal with one * echoed per
// character, so a secret typed or pasted at `yawble secret set NAME` is never on screen but the
// person sees that the paste landed. A terminal that cannot be put in raw mode falls back to
// reading with no echo at all.
func readHidden(prompt string) (string, error) {
	fmt.Fprint(os.Stderr, prompt)
	fd := int(os.Stdin.Fd())

	state, err := term.MakeRaw(fd)
	if err != nil {
		value, err := term.ReadPassword(fd)
		fmt.Fprintln(os.Stderr)
		return string(value), err
	}

	value, err := maskedinput.Read(os.Stdin, os.Stderr)
	_ = term.Restore(fd, state)
	fmt.Fprintln(os.Stderr)
	return value, err
}
