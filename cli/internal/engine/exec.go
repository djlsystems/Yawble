package engine

import (
	"bytes"
	"context"
	"errors"
	"io"
	"os/exec"
	"strings"
	"time"
)

type execRunner struct{}

// pipeGrace bounds how long a run waits for its output pipes once the program has exited. A
// program can leave a child holding them: xdg-open starting a browser that was not yet running
// (the first `yawble up` after a reboot) kept `up` waiting until the browser was closed. What the
// program wrote is in the pipe when it exits; a child's later output is not the program's.
const pipeGrace = 2 * time.Second

// command is exec.CommandContext with the pipe grace set.
func command(ctx context.Context, name string, args ...string) *exec.Cmd {
	cmd := exec.CommandContext(ctx, name, args...)
	cmd.WaitDelay = pipeGrace
	return cmd
}

// finished is cmd.Run's error with the pipe grace taken out: a program that exited 0 and left a
// child holding its output succeeded.
func finished(err error) error {
	if errors.Is(err, exec.ErrWaitDelay) {
		return nil
	}
	return err
}

// ExecRunner runs real programs. It is both a Runner and a Streamer.
func ExecRunner() interface {
	Runner
	Streamer
	InputRunner
	PipeRunner
} {
	return execRunner{}
}

func (r execRunner) Run(ctx context.Context, name string, args ...string) (Result, error) {
	return r.RunInput(ctx, "", name, args...)
}

func (execRunner) RunInput(ctx context.Context, stdin string, name string, args ...string) (Result, error) {
	cmd := command(ctx, name, args...)
	if stdin != "" {
		cmd.Stdin = strings.NewReader(stdin)
	}
	var out, errb bytes.Buffer
	cmd.Stdout, cmd.Stderr = &out, &errb
	err := finished(cmd.Run())
	res := Result{Stdout: out.String(), Stderr: errb.String()}
	var exit *exec.ExitError
	switch {
	case err == nil:
	case errors.As(err, &exit):
		res.ExitCode = exit.ExitCode()
	default:
		// Not found, not executable: the caller says which program, so the person hears
		// "podman could not be run" rather than a bare errno.
		return res, err
	}
	return res, nil
}

func (execRunner) Stream(ctx context.Context, out io.Writer, name string, args ...string) (int, error) {
	cmd := command(ctx, name, args...)
	cmd.Stdout, cmd.Stderr = out, out
	err := finished(cmd.Run())
	var exit *exec.ExitError
	switch {
	case err == nil:
		return 0, nil
	case errors.As(err, &exit):
		return exit.ExitCode(), nil
	default:
		return -1, err
	}
}

func (execRunner) RunPipe(ctx context.Context, stdin io.Reader, stdout io.Writer, name string, args ...string) (Result, error) {
	cmd := command(ctx, name, args...)
	cmd.Stdin = stdin
	var out, errb bytes.Buffer
	cmd.Stdout, cmd.Stderr = &out, &errb
	if stdout != nil {
		cmd.Stdout = stdout
	}
	err := finished(cmd.Run())
	res := Result{Stdout: out.String(), Stderr: errb.String()}
	var exit *exec.ExitError
	switch {
	case err == nil:
	case errors.As(err, &exit):
		res.ExitCode = exit.ExitCode()
	default:
		return res, err
	}
	return res, nil
}
