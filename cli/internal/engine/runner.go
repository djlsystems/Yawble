package engine

import (
	"context"
	"io"
)

// Result is one finished command.
type Result struct {
	Stdout   string
	Stderr   string
	ExitCode int
}

// Runner runs one program with arguments and answers what it printed and how it exited. The
// real one execs; tests script one. Everything the CLI does to the machine goes through here, so
// a test can assert the exact command sequence a verb produces without an engine present.
type Runner interface {
	Run(ctx context.Context, name string, args ...string) (Result, error)
}

// InputRunner is a Runner that can also hand a program text on stdin: `login --password-stdin`,
// so a credential never appears on a command line, where every process on the machine can read it.
type InputRunner interface {
	RunInput(ctx context.Context, stdin string, name string, args ...string) (Result, error)
}

// PipeRunner is a Runner that streams: stdin from a reader (nil for none) and stdout into a
// writer as it is produced, stderr collected in the Result. A backup is gigabytes of tar, which
// must never be held in memory.
type PipeRunner interface {
	RunPipe(ctx context.Context, stdin io.Reader, stdout io.Writer, name string, args ...string) (Result, error)
}
