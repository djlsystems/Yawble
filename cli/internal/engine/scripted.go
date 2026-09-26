package engine

import (
	"context"
	"io"
	"strings"
)

// Scripted records every command and answers from a script. It lives in the package, not a
// _test file, because the instance and cli tests drive the Podman engine through it.
type Scripted struct {
	// Calls is every command line run, in order: the program, a space, the arguments joined
	// by spaces. Tests compare it to the sequence a verb must produce.
	Calls []string
	// Inputs is what each RunInput call handed on stdin, in order; Calls still records its line,
	// so a test can assert the secret is here and not there.
	Inputs  []string
	answers []*answer
}

type answer struct {
	prefix  string
	results []Result
	next    int
}

func NewScripted() *Scripted { return &Scripted{} }

// On answers any command line starting with prefix. The first registered match wins; an
// unmatched command succeeds with no output, which is what most podman verbs do.
func (s *Scripted) On(prefix string, r Result) { s.OnSequence(prefix, r) }

// OnSequence answers the first match with the first result, the second with the second, and
// repeats the last one afterwards: a container that is absent, then running, then exited.
func (s *Scripted) OnSequence(prefix string, results ...Result) {
	s.answers = append(s.answers, &answer{prefix: prefix, results: results})
}

// Run answers from the most specific script: the longest prefix that matches, so an entry for
// "... yawble-tunnel" wins over one for "... yawble" although both match. Ties go to the first
// registered. An unmatched command succeeds with no output.
func (s *Scripted) Run(_ context.Context, name string, args ...string) (Result, error) {
	line := name + " " + strings.Join(args, " ")
	s.Calls = append(s.Calls, line)
	var best *answer
	for _, a := range s.answers {
		if strings.HasPrefix(line, a.prefix) && (best == nil || len(a.prefix) > len(best.prefix)) {
			best = a
		}
	}
	if best == nil {
		return Result{}, nil
	}
	r := best.results[min(best.next, len(best.results)-1)]
	best.next++
	return r, nil
}

func (s *Scripted) RunInput(ctx context.Context, stdin string, name string, args ...string) (Result, error) {
	s.Inputs = append(s.Inputs, stdin)
	return s.Run(ctx, name, args...)
}

func (s *Scripted) Stream(ctx context.Context, out io.Writer, name string, args ...string) (int, error) {
	r, err := s.Run(ctx, name, args...)
	if err != nil {
		return -1, err
	}
	_, _ = io.WriteString(out, r.Stdout)
	return r.ExitCode, nil
}
