package engine

import (
	"bytes"
	"context"
	"errors"
	"io"
	"os/exec"
	"strings"
)

type execRunner struct{}

// ExecRunner runs real programs. It is both a Runner and a Streamer.
func ExecRunner() interface {
	Runner
	Streamer
	InputRunner
} {
	return execRunner{}
}

func (r execRunner) Run(ctx context.Context, name string, args ...string) (Result, error) {
	return r.RunInput(ctx, "", name, args...)
}

func (execRunner) RunInput(ctx context.Context, stdin string, name string, args ...string) (Result, error) {
	cmd := exec.CommandContext(ctx, name, args...)
	if stdin != "" {
		cmd.Stdin = strings.NewReader(stdin)
	}
	var out, errb bytes.Buffer
	cmd.Stdout, cmd.Stderr = &out, &errb
	err := cmd.Run()
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
	cmd := exec.CommandContext(ctx, name, args...)
	cmd.Stdout, cmd.Stderr = out, out
	err := cmd.Run()
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
