package engine

import (
	"context"
	"fmt"
	"io"
	"strconv"
	"strings"
	"time"
)

// base is the body the two engines share: every verb that is the same command line on Podman and
// Docker but for the program's name. An engine embeds it and adds only what differs (how an
// object's existence is asked, the pod verbs, inspect's format, the health template, run's
// network and port, and removing an image).
type base struct {
	program string
	r       Runner
}

func (b base) Name() string { return b.program }

// run is every non-streaming call: one command line, and a non-zero exit becomes an error that
// carries the engine's own sentence, because that sentence is usually the whole diagnosis.
func (b base) run(ctx context.Context, args ...string) (Result, error) {
	res, err := b.r.Run(ctx, b.program, args...)
	if err != nil {
		return res, &NotRunnable{Program: b.program, Err: err}
	}
	if res.ExitCode != 0 {
		return res, fmt.Errorf("%s %s: %s (exit %d)", b.program, strings.Join(args[:min(2, len(args))], " "), strings.TrimSpace(res.Stderr), res.ExitCode)
	}
	return res, nil
}

func (b base) Version(ctx context.Context) (string, error) {
	res, err := b.run(ctx, "version", "--format", "{{.Client.Version}}")
	return strings.TrimSpace(res.Stdout), err
}

func (b base) CreateVolume(ctx context.Context, name string) error {
	_, err := b.run(ctx, "volume", "create", name)
	return err
}

// Stats is `<program> stats --no-stream` in the program's JSON form.
func (b base) Stats(ctx context.Context, name string) (Stats, error) {
	return stats(ctx, b.r, b.program, name)
}

func (b base) Remove(ctx context.Context, name string) error {
	_, err := b.run(ctx, "rm", "-f", name)
	return err
}

func (b base) Exec(ctx context.Context, name string, args ...string) (Result, error) {
	return b.run(ctx, execArgs(nil, name, args)...)
}

// List is `<program> ps -a --filter <filter> --format {{.Names}}`.
func (b base) List(ctx context.Context, filter string) ([]string, error) {
	return list(ctx, b.run, filter)
}

func (b base) StopWithin(ctx context.Context, name string, grace int) error {
	_, err := b.run(ctx, "stop", "-t", strconv.Itoa(grace), name)
	return err
}

func (b base) ExecTo(ctx context.Context, name string, stdout io.Writer, args ...string) (Result, error) {
	return execTo(ctx, b.r, b.program, name, stdout, args)
}

func (b base) ExecInput(ctx context.Context, name, stdin string, args ...string) (Result, error) {
	return execInput(ctx, b.r, b.program, name, stdin, args)
}

func (b base) CopyTo(ctx context.Context, name, src, dst string) error {
	_, err := b.run(ctx, "cp", src, name+":"+dst)
	return err
}

func (b base) Start(ctx context.Context, name string) error {
	_, err := b.run(ctx, "start", name)
	return err
}

func (b base) Stop(ctx context.Context, name string) error {
	_, err := b.run(ctx, "stop", name)
	return err
}

func (b base) Pull(ctx context.Context, ref string, progress io.Writer) error {
	return b.stream(ctx, progress, "pull", ref)
}

func (b base) Login(ctx context.Context, registry, user, password string) error {
	return login(ctx, b.r, b.program, registry, user, password)
}

func (b base) RemoveVolume(ctx context.Context, name string) error {
	_, err := b.run(ctx, "volume", "rm", name)
	return err
}

// VolumeCreated is `<program> volume inspect --format {{.CreatedAt}} <name>`, as a date.
func (b base) VolumeCreated(ctx context.Context, name string) (string, error) {
	res, err := b.run(ctx, "volume", "inspect", "--format", "{{.CreatedAt}}", name)
	return createdDate(res.Stdout), err
}

func (b base) Logs(ctx context.Context, name string, follow bool, tail int, out io.Writer) error {
	args := []string{"logs"}
	if follow {
		args = append(args, "--follow")
	}
	if tail > 0 {
		args = append(args, "--tail", strconv.Itoa(tail))
	}
	return b.stream(ctx, out, append(args, name)...)
}

func (b base) FollowSince(ctx context.Context, name string, since time.Time, out io.Writer) error {
	return b.stream(ctx, out, "logs", "--follow", "--since", since.UTC().Format(time.RFC3339), name)
}

// stream runs a command whose output a person watches. A Runner that cannot stream still works:
// the output arrives when the command ends.
func (b base) stream(ctx context.Context, out io.Writer, args ...string) error {
	streamer, ok := b.r.(Streamer)
	if !ok {
		res, err := b.run(ctx, args...)
		if err == nil {
			_, _ = io.WriteString(out, res.Stdout)
		}
		return err
	}
	code, err := streamer.Stream(ctx, out, b.program, args...)
	if err != nil {
		return &NotRunnable{Program: b.program, Err: err}
	}
	if code != 0 {
		return fmt.Errorf("%s %s exited %d", b.program, args[0], code)
	}
	return nil
}

func (b base) RunHelper(ctx context.Context, spec HelperSpec) (Result, error) {
	return runHelper(ctx, b.r, b.program, spec)
}
