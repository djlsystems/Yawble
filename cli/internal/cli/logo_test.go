package cli_test

import (
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/cli"
)

// hasMark says whether output carries the Braille mark: any character from the Braille block.
func hasMark(out string) bool {
	for _, r := range out {
		if r >= 0x2800 && r <= 0x28FF {
			return true
		}
	}
	return false
}

func marks(out string) int {
	return strings.Count(out, cli.LogoFirstLine())
}

func TestVersionShowsTheMarkOnlyToATerminal(t *testing.T) {
	code, out, _ := run(t, cli.Deps{StdoutTerminal: true}, "version")
	if code != 0 || !hasMark(out) || !strings.Contains(out, "yawble ") {
		t.Errorf("a terminal: exit %d out %q", code, out)
	}
	if _, out, _ := run(t, cli.Deps{}, "version"); hasMark(out) {
		t.Errorf("a pipe got the mark: %q", out)
	}
	if _, out, _ := run(t, cli.Deps{StdoutTerminal: true}, "version", "--json"); hasMark(out) {
		t.Errorf("--json got the mark: %q", out)
	}
}

func TestYawbleWithNoArgumentsShowsTheMarkAndTheCommands(t *testing.T) {
	code, out, _ := run(t, cli.Deps{StdoutTerminal: true})
	if code != 0 || !hasMark(out) || !strings.Contains(out, "Available Commands") {
		t.Errorf("exit %d out %q", code, out)
	}
	if _, out, _ := run(t, cli.Deps{}); hasMark(out) || !strings.Contains(out, "Available Commands") {
		t.Errorf("a pipe: %q", out)
	}
}

// Giving the root an action must not swallow a mistyped command.
func TestAnUnknownCommandIsStillAnInvocationError(t *testing.T) {
	code, out, errOut := run(t, cli.Deps{StdoutTerminal: true}, "stauts")
	if code != 2 || hasMark(out) || !strings.Contains(errOut, "unknown command") {
		t.Errorf("exit %d out %q err %q", code, out, errOut)
	}
}

func TestUpShowsTheMarkOnceToATerminalAndNeverToAPipe(t *testing.T) {
	s := upScript()
	deps := stubbed(s)
	deps.LookPath, deps.StdoutTerminal = lookPath("podman"), true
	code, out, errOut := run(t, deps, "up", "--no-browser")
	if code != 0 || marks(out) != 1 {
		t.Errorf("exit %d, %d marks: %s %s", code, marks(out), out, errOut)
	}

	s = upScript()
	deps = stubbed(s)
	deps.LookPath = lookPath("podman")
	if _, out, _ := run(t, deps, "up", "--no-browser"); hasMark(out) {
		t.Errorf("a pipe got the mark: %q", out)
	}
}
