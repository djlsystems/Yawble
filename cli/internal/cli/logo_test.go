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

// No arguments: the mark, the tagline and where to go next; the full command list is --help's.
func TestYawbleWithNoArgumentsShowsTheMarkTheTaglineAndWhereToGetHelp(t *testing.T) {
	code, out, _ := run(t, cli.Deps{StdoutTerminal: true})
	if code != 0 || !hasMark(out) {
		t.Fatalf("exit %d out %q", code, out)
	}
	for _, want := range []string{"Yawble — Teams of agents, moving with you.", "yawble <command> [flags]", "yawble up", "yawble --help", "yawble <command> --help"} {
		if !strings.Contains(out, want) {
			t.Errorf("missing %q in %q", want, out)
		}
	}
	if strings.Contains(out, "Available Commands") {
		t.Errorf("the command list belongs to --help: %q", out)
	}

	// A pipe: the same words, no mark.
	if _, out, _ := run(t, cli.Deps{}); hasMark(out) || !strings.Contains(out, "Teams of agents, moving with you.") {
		t.Errorf("a pipe: %q", out)
	}
	// --help and -h still list every command.
	for _, flag := range []string{"--help", "-h"} {
		if _, out, _ := run(t, cli.Deps{StdoutTerminal: true}, flag); !strings.Contains(out, "Available Commands") {
			t.Errorf("%s: %q", flag, out)
		}
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

func TestTheMarkIsYawbleOrangeWhenColourIsOnAndPlainOtherwise(t *testing.T) {
	truecolor := func(k string) string {
		if k == "COLORTERM" {
			return "truecolor"
		}
		return ""
	}
	_, out, _ := run(t, cli.Deps{StdoutTerminal: true, Color: true, Env: truecolor}, "version")
	if !strings.Contains(out, "\x1b[38;2;232;59;0m") || !strings.Contains(out, "\x1b[0m") {
		t.Errorf("24-bit terminal: %q", out)
	}
	_, out, _ = run(t, cli.Deps{StdoutTerminal: true, Color: true}, "version")
	if !strings.Contains(out, "\x1b[38;5;202m") || !strings.Contains(out, "\x1b[0m") {
		t.Errorf("256-colour terminal: %q", out)
	}
	_, out, _ = run(t, cli.Deps{StdoutTerminal: true}, "version")
	if !hasMark(out) || strings.Contains(out, "\x1b[") {
		t.Errorf("colour off: %q", out)
	}
	// The colour ends before the version line: nothing after the mark is orange.
	_, out, _ = run(t, cli.Deps{StdoutTerminal: true, Color: true}, "version")
	if i, v := strings.Index(out, "\x1b[0m"), strings.Index(out, "yawble "); i < 0 || v < i {
		t.Errorf("the reset must come before the version line: %q", out)
	}
}
