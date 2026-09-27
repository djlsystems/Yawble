package cli

import (
	"errors"
	"fmt"
	"os"
	"os/exec"
	"runtime"

	"github.com/spf13/cobra"

	"github.com/djlsystems/yawble/cli/internal/buildinfo"
	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/instance"
	"github.com/djlsystems/yawble/cli/internal/machine"
	"github.com/djlsystems/yawble/cli/internal/release"
)

func newUpdateCommand(deps Deps) *cobra.Command {
	var cliOnly, instanceOnly, prerelease bool
	cmd := &cobra.Command{
		Use:   "update",
		Short: "Update yawble and the instance together; --cli or --instance does one half",
		Long: "update looks for a newer yawble release. When there is one it downloads it, verifies its " +
			"checksum, replaces this binary, and runs the new one to move the instance: each yawble pins " +
			"the Yawble image it was released with, so only the new binary knows the new image. " +
			"When this yawble is the latest, or the release cannot be read, it moves the instance to the " +
			"image this yawble pins. Moving the instance recreates the container on the same volume, so " +
			"nothing is lost; the Host migrates its database on start. --cli replaces yawble only; " +
			"--instance moves the instance only and never looks for a release. It takes the newest " +
			"regular release; --prerelease takes the newest release, a pre-release included. It never " +
			"moves to an older yawble than this one.",
		Example: "  yawble update\n  yawble update --prerelease\n  yawble update --cli\n  yawble update --instance",
		Args:    cobra.NoArgs,
		RunE: func(cmd *cobra.Command, _ []string) error {
			switch {
			case cliOnly && instanceOnly:
				return UsageError{"--cli and --instance each do one half; a plain `yawble update` does both"}
			case cliOnly:
				_, err := updateCLI(cmd, deps, prerelease)
				return err
			case instanceOnly:
				return updateInstance(cmd, deps)
			}
			return updateBoth(cmd, deps, prerelease)
		},
	}
	cmd.Flags().BoolVar(&cliOnly, "cli", false, "replace yawble itself, not the instance")
	cmd.Flags().BoolVar(&instanceOnly, "instance", false, "move the instance to the image this yawble pins; do not look for a newer yawble")
	cmd.Flags().BoolVar(&prerelease, "prerelease", false, "take the newest release, a pre-release included; without it only regular releases are taken")
	return cmd
}

// updateBoth is a plain `update`. A development build is never replaced by a release; a release
// that cannot be read is said and does not stop the instance moving.
func updateBoth(cmd *cobra.Command, deps Deps, prerelease bool) error {
	if buildinfo.Version == "dev" {
		fmt.Fprintln(cmd.ErrOrStderr(), "note: this is a development build of yawble; it is not replaced by a release (yawble update --cli does that)")
		return updateInstance(cmd, deps)
	}
	exe, err := updateCLI(cmd, deps, prerelease)
	if err != nil {
		fmt.Fprintf(cmd.ErrOrStderr(), "note: could not look for a newer yawble (%v); moving the instance to the image this yawble pins\n", err)
		return updateInstance(cmd, deps)
	}
	if exe == "" {
		return updateInstance(cmd, deps)
	}
	// The new binary pins the new image; it finishes the update in this terminal.
	runBinary := deps.RunBinary
	if runBinary == nil {
		runBinary = runInTerminal
	}
	code, err := runBinary(exe, "update", "--instance")
	if err != nil {
		return fmt.Errorf("the new yawble is installed but could not be run to move the instance (%w); run: yawble update", err)
	}
	if code != 0 {
		return ExitError{Code: code}
	}
	return nil
}

// runInTerminal runs a program with this process's stdin, stdout and stderr and answers its exit code.
func runInTerminal(path string, args ...string) (int, error) {
	c := exec.Command(path, args...)
	c.Stdin, c.Stdout, c.Stderr = os.Stdin, os.Stdout, os.Stderr
	err := c.Run()
	var exit *exec.ExitError
	if errors.As(err, &exit) {
		return exit.ExitCode(), nil
	}
	if err != nil {
		return -1, err
	}
	return 0, nil
}

func updateInstance(cmd *cobra.Command, deps Deps) error {
	if err := startStoppedMachine(cmd, deps); err != nil {
		return err
	}
	e, s, notes, err := prepare(deps)
	if err != nil {
		return err
	}
	if s.Image == "" {
		return instance.ErrNoImage
	}
	out := cmd.OutOrStdout()
	info, err := e.Inspect(cmd.Context(), instance.ContainerName)
	if err != nil {
		return err
	}
	if info.State != engine.StateAbsent && info.Image == s.Image && len(instance.Changes(info.Label, s)) == 0 {
		fmt.Fprintf(out, "already on %s; nothing to update (yawble up starts it if it is stopped)\n", s.Image)
		return nil
	}
	if info.State != engine.StateAbsent && info.Image != s.Image {
		fmt.Fprintf(out, "image %s -> %s\n", info.Image, s.Image)
	}
	for _, n := range notes {
		fmt.Fprintln(cmd.ErrOrStderr(), "note:", n)
	}
	return instance.Up(cmd.Context(), e, s, healthChecker(deps.HTTP), out)
}

// startStoppedMachine starts a Podman machine that exists and is stopped, as `up` and
// `doctor --fix` do: after a restart the machine is stopped, and update would otherwise fail on
// the engine's refused connection (measured on the Windows VM, 2026-09-26). Creating or
// re-rooting a machine stays `up`'s to offer.
func startStoppedMachine(cmd *cobra.Command, deps Deps) error {
	c, err := loadConfig(deps)
	if err != nil {
		return err
	}
	if engineOf(deps, c).Name() != "podman" {
		return nil
	}
	goos := deps.GOOS
	if goos == "" {
		goos = runtime.GOOS
	}
	info, err := machine.Inspect(cmd.Context(), runnerOf(deps), goos)
	if err != nil || !info.Applies || !info.Exists || info.Running {
		return nil // nothing to start; the engine's own error says what is wrong
	}
	fmt.Fprintln(cmd.OutOrStdout(), "starting the podman machine")
	return machine.Start(cmd.Context(), runnerOf(deps))
}

// updateCLI replaces this binary with the newest release (a pre-release only with prerelease). It
// answers the path of the binary it installed, or "" when there is nothing newer.
func updateCLI(cmd *cobra.Command, deps Deps, prerelease bool) (string, error) {
	out := cmd.OutOrStdout()
	goos := deps.GOOS
	if goos == "" {
		goos = runtime.GOOS
	}
	base := deps.ReleaseBaseURL
	if base == "" {
		base = release.DefaultBaseURL
	}
	executable := deps.Executable
	if executable == nil {
		executable = os.Executable
	}
	rel, err := release.Latest(cmd.Context(), deps.HTTP, base, githubToken(deps), goos, runtime.GOARCH, prerelease)
	if err != nil {
		return "", err
	}
	if rel.Tag == buildinfo.Version {
		fmt.Fprintf(out, "yawble %s is already the latest release\n", rel.Tag)
		return "", nil
	}
	// Never backwards: a pre-release installed with --prerelease is newer than the newest regular
	// release, and a plain update must not replace it with that older one.
	if release.IsNewer(buildinfo.Version, rel.Tag) {
		fmt.Fprintf(out, "yawble %s is newer than the newest %srelease, %s; nothing to update\n", buildinfo.Version, map[bool]string{true: "", false: "regular "}[prerelease], rel.Tag)
		if !prerelease {
			fmt.Fprintln(out, "(yawble update --prerelease takes newer pre-releases)")
		}
		return "", nil
	}
	exe, err := executable()
	if err != nil {
		return "", fmt.Errorf("finding this binary: %w", err)
	}
	fmt.Fprintf(out, "yawble %s -> %s (%s)\n", buildinfo.Version, rel.Tag, rel.AssetName)
	if err := release.Install(cmd.Context(), deps.HTTP, rel, githubToken(deps), exe, goos); err != nil {
		return "", err
	}
	fmt.Fprintf(out, "installed %s at %s\n", rel.Tag, exe)
	if goos == "windows" {
		fmt.Fprintln(out, "the old binary was moved aside as yawble.exe.old and is removed on the next run")
	}
	return exe, nil
}
