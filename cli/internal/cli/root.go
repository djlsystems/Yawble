// Package cli is the command surface. Commands are thin: they parse, call one function in
// internal/instance or internal/config, and render. Everything that touches the machine is
// injected through Deps so the whole surface runs under `go test` with no engine present.
package cli

import (
	"context"
	"errors"
	"fmt"
	"io"
	"net/http"
	"strings"
	"time"

	"github.com/spf13/cobra"

	"github.com/djlsystems/yawble/cli/internal/buildinfo"
	"github.com/djlsystems/yawble/cli/internal/engine"
)

// Deps is everything a command needs from outside: where to print, how to read the environment,
// where the config lives, how to run programs and how to reach the instance over HTTP.
type Deps struct {
	Stdout, Stderr io.Writer
	Env            func(string) string
	ConfigDir      string
	Runner         engine.Runner // nil means the real one
	HTTP           *http.Client
	Now            func() time.Time
	// PortFree says whether a host port is free; nil means the real probe. Tests stub it so a
	// developer's own listeners never decide a verdict.
	PortFree func(port int) bool
	// GOOS is the operating system the commands believe they are on; "" means runtime.GOOS.
	// Tests set it so the machine rules are exercised on every developer's OS.
	GOOS string
	// GOARCH is the processor the commands believe they are on; "" means runtime.GOARCH. macOS
	// is supported on Apple silicon only, so the tests set it to exercise both answers.
	GOARCH string
	// ReadSecret shows prompt and reads one line from the terminal without echoing it, for
	// `yawble secret set NAME` typed at a terminal. nil means no terminal to ask at.
	ReadSecret func(prompt string) (string, error)
	// LookPath finds a program on PATH (exec.LookPath in main). nil means "podman is present",
	// which is what every command but `up`'s preflight assumes anyway.
	LookPath func(string) (string, error)
	// Stdin and Interactive drive the yes/no questions: with no terminal, a question is a
	// refusal naming --yes.
	Stdin       io.Reader
	Interactive bool
	// StdoutTerminal is whether stdout is a terminal someone reads, as opposed to a pipe or a
	// file. Only then is the mark shown: `yawble version | ...` from a terminal gets none.
	StdoutTerminal bool
	// Color is whether the mark may be drawn in Yawble orange: a terminal, NO_COLOR unset, and on
	// Windows a console that accepted colour sequences. Everything else the CLI prints is plain.
	Color bool
	// ReleaseBaseURL is the GitHub API for `update --cli`; "" means api.github.com. Executable is
	// where this binary is (os.Executable in main). Tests point both at a fake.
	ReleaseBaseURL string
	Executable     func() (string, error)
	// RunBinary runs a yawble binary in this terminal and answers its exit code: `update` hands
	// the instance to the binary it just installed. nil means the real one.
	RunBinary func(path string, args ...string) (int, error)
}

// UsageError is an invocation the person got wrong: exit 2, and the message names what to change.
type UsageError struct{ Msg string }

func (e UsageError) Error() string { return e.Msg }

// ExitError ends with another yawble's exit code, printing nothing more: that yawble has already
// said what went wrong in this terminal (`update` running the binary it just installed).
type ExitError struct{ Code int }

func (e ExitError) Error() string { return fmt.Sprintf("exit %d", e.Code) }

// NewRoot builds the command tree. Colour is not used anywhere, so NO_COLOR needs no handling.
func NewRoot(deps Deps) *cobra.Command {
	if deps.Now == nil {
		deps.Now = time.Now
	}
	if deps.HTTP == nil {
		deps.HTTP = &http.Client{Timeout: 5 * time.Second}
	}
	root := &cobra.Command{
		Use:           "yawble",
		Short:         "Run a Yawble instance: install, start, diagnose, update, expose",
		Long:          "yawble is the operator CLI for a Yawble instance. It runs the instance on Podman or Docker, pulls the image, starts and updates the instance, diagnoses it, and puts it on the internet. Agents never touch it.",
		SilenceUsage:  true,
		SilenceErrors: true,
		// Cobra's `completion` (Tab completion scripts for a shell) works but is not listed:
		// most people never need it, and the README says how to turn it on.
		CompletionOptions: cobra.CompletionOptions{HiddenDefaultCmd: true},
		// No arguments: the mark, the tagline and where to go next. The whole command list is
		// --help's, so the first thing a person sees is short. NoArgs keeps a mistyped command
		// an invocation error rather than a run of this.
		Args: cobra.NoArgs,
		RunE: func(cmd *cobra.Command, _ []string) error {
			out := cmd.OutOrStdout()
			showLogo(deps, out)
			version := buildinfo.Version
			if version == "dev" {
				version = "development build"
			}
			fmt.Fprintf(out, welcome, version)
			return nil
		},
	}
	root.SetOut(deps.Stdout)
	root.SetErr(deps.Stderr)
	// Commands that ask more than yes/no (`solution install`) read their answers from here.
	if deps.Stdin != nil {
		root.SetIn(deps.Stdin)
	}
	root.AddCommand(
		newUpCommand(deps), newDownCommand(deps), newStatusCommand(deps), newLogsCommand(deps), newWorkersCommand(deps),
		newDoctorCommand(deps), newAgentsCommand(deps), newUpdateCommand(deps), newRemoteCommand(deps),
		newBackupCommand(deps), newRestoreCommand(deps),
		newUninstallCommand(deps), newConfigCommand(deps), newSecretCommand(deps), newGitHubCommand(deps), newPluginCommand(deps), newSolutionCommand(deps), newConnectCommand(deps), newRepoCommand(deps), newVersionCommand(deps),
	)
	return root
}

// Execute runs the root and maps the outcome to an exit code: 0, 1 (it failed), 2 (the
// invocation was wrong). Cobra's own parse errors are invocation errors.
func Execute(root *cobra.Command, args []string) int {
	root.SetArgs(args)
	err := root.ExecuteContext(context.Background())
	if err == nil {
		return 0
	}
	var exit ExitError
	if errors.As(err, &exit) {
		return exit.Code
	}
	var usage UsageError
	if errors.As(err, &usage) || isCobraUsageError(err) {
		fmt.Fprintln(root.ErrOrStderr(), "yawble:", err)
		fmt.Fprintln(root.ErrOrStderr(), "Run 'yawble --help' for usage.")
		return 2
	}
	fmt.Fprintln(root.ErrOrStderr(), "yawble:", err)
	return 1
}

// Cobra reports an unknown command or a bad flag as a plain error whose text starts with one of
// these; there is no error type to test for.
func isCobraUsageError(err error) bool {
	msg := err.Error()
	for _, prefix := range []string{
		"unknown command", "unknown flag", "unknown shorthand flag",
		"accepts ", "requires ", "invalid argument", "flag needs an argument",
	} {
		if strings.HasPrefix(msg, prefix) {
			return true
		}
	}
	return false
}
