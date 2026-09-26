package engine

import "context"

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
