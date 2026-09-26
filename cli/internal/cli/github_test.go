package cli_test

import (
	"io"
	"net/http"
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/cli"
	"github.com/djlsystems/yawble/cli/internal/config"
	"github.com/djlsystems/yawble/cli/internal/engine"
)

// The first `yawble up` asks whether teams will use GitHub and, if so, helps
// get a token - the GitHub CLI's login, or a fine-grained token pasted at a hidden prompt - checks
// it with GitHub, and saves it as the GH_TOKEN secret. `yawble github` does the same any time.

// gitHubAnswers is a transport where GitHub's /user accepts only good-token (as octocat) and
// every other request (the health probe) answers 200.
func gitHubAnswers() *http.Client {
	return &http.Client{Transport: roundTripFunc(func(r *http.Request) (*http.Response, error) {
		code, body := http.StatusOK, "{}"
		if r.URL.Path == "/user" {
			if r.Header.Get("Authorization") == "Bearer good-token" {
				body = `{"login":"octocat"}`
			} else {
				code = http.StatusUnauthorized
			}
		}
		return &http.Response{StatusCode: code, Body: io.NopCloser(strings.NewReader(body)), Header: http.Header{}}, nil
	})}
}

func gitHubDeps(t *testing.T, s *engine.Scripted, answers string, pasted ...string) cli.Deps {
	t.Helper()
	deps := stubbed(s)
	deps.ConfigDir = t.TempDir()
	deps.HTTP, deps.ReleaseBaseURL = gitHubAnswers(), "https://api.github.test"
	deps.Interactive, deps.Stdin = true, strings.NewReader(answers)
	deps.ReadSecret = func(string) (string, error) {
		if len(pasted) == 0 {
			return "", nil
		}
		next := pasted[0]
		pasted = pasted[1:]
		return next, nil
	}
	deps.LookPath = lookPath("podman")
	return deps
}

func savedToken(t *testing.T, dir string) string {
	t.Helper()
	value, _, _ := config.SecretValue(dir, "GH_TOKEN")
	return value
}

func asked(t *testing.T, dir string) bool {
	t.Helper()
	c, _, _ := config.Load(dir, func(string) string { return "" })
	return c.GitHubAsked
}

func TestFirstUpOffersGitHubAndSavesACheckedPastedToken(t *testing.T) {
	deps := gitHubDeps(t, upScript(), "y\n", "good-token")
	code, out, errOut := run(t, deps, "up", "--no-browser")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if savedToken(t, deps.ConfigDir) != "good-token" || !asked(t, deps.ConfigDir) {
		t.Errorf("token %q asked %v", savedToken(t, deps.ConfigDir), asked(t, deps.ConfigDir))
	}
	for _, want := range []string{"GitHub", "https://github.com/settings/personal-access-tokens/new", "Contents", "Pull requests", "accepts", "octocat"} {
		if !strings.Contains(out, want) {
			t.Errorf("missing %q in %q", want, out)
		}
	}
	if strings.Contains(out+errOut, "good-token") {
		t.Error("the token was printed")
	}
}

func TestTheGitHubCLILoginIsOfferedWhenGhIsThere(t *testing.T) {
	s := upScript()
	s.On("gh auth token", engine.Result{Stdout: "good-token\n"})
	deps := gitHubDeps(t, s, "y\ny\n")
	deps.LookPath = lookPath("podman", "gh")
	code, out, errOut := run(t, deps, "up", "--no-browser")
	if code != 0 || savedToken(t, deps.ConfigDir) != "good-token" || !strings.Contains(out, "gh auth token") {
		t.Errorf("exit %d token %q out %q err %q", code, savedToken(t, deps.ConfigDir), out, errOut)
	}
}

func TestARejectedTokenIsSaidAndAskedAgainAndEnterSkips(t *testing.T) {
	deps := gitHubDeps(t, upScript(), "y\n", "revoked-token", "")
	code, out, errOut := run(t, deps, "up", "--no-browser")
	if code != 0 || !strings.Contains(out, "rejects") || savedToken(t, deps.ConfigDir) != "" {
		t.Errorf("exit %d token %q out %q err %q", code, savedToken(t, deps.ConfigDir), out, errOut)
	}
}

func TestNoIsRememberedAndNotAskedAgain(t *testing.T) {
	deps := gitHubDeps(t, upScript(), "n\n")
	if code, _, errOut := run(t, deps, "up", "--no-browser"); code != 0 || !asked(t, deps.ConfigDir) || savedToken(t, deps.ConfigDir) != "" {
		t.Fatalf("first: exit %d asked %v err %q", code, asked(t, deps.ConfigDir), errOut)
	}
	deps.Stdin = strings.NewReader("") // a second question would read nothing
	code, out, _ := run(t, deps, "up", "--no-browser")
	if code != 0 || strings.Contains(out, "Will your teams work with GitHub") || !strings.Contains(out, "yawble github") {
		t.Errorf("second: exit %d out %q", code, out)
	}
}

func TestWithoutATerminalNothingIsAskedAndUpEndsWithTheHint(t *testing.T) {
	deps := gitHubDeps(t, upScript(), "")
	deps.Interactive = false
	code, out, _ := run(t, deps, "up", "--yes")
	if code != 0 || strings.Contains(out, "Will your teams") || !strings.Contains(out, "yawble github") || asked(t, deps.ConfigDir) {
		t.Errorf("exit %d asked %v out %q", code, asked(t, deps.ConfigDir), out)
	}
}

func TestTheGitHubCommandSetsUpATokenEvenAfterNo(t *testing.T) {
	deps := gitHubDeps(t, engine.NewScripted(), "", "good-token")
	c, _, _ := config.Load(deps.ConfigDir, func(string) string { return "" })
	c.GitHubAsked = true
	_, _ = config.Save(deps.ConfigDir, c)
	code, out, errOut := run(t, deps, "github")
	if code != 0 || savedToken(t, deps.ConfigDir) != "good-token" || !strings.Contains(out, "yawble up") {
		t.Errorf("exit %d token %q out %q err %q", code, savedToken(t, deps.ConfigDir), out, errOut)
	}
}

// Running `up` is the instruction: when its settings changed, it replaces a running container
// without asking (a restart is why the command was run, and a question would refuse a script). It says what the restart stops, by name, from the
// agent processes' working folders, listed as the agent user who owns them.
func driftedRunningInstance(listing string) *engine.Scripted {
	s := engine.NewScripted()
	// An image other than the one these tests pin (testImage), so the settings have changed.
	old := "ghcr.io/djlsystems/yawble:2026.09.20.1"
	s.On("podman container inspect", engine.Result{Stdout: "running|" + old + "|" + labelFor(old) + "\n"})
	s.On("podman exec yawble runuser -u agent", engine.Result{Stdout: listing})
	return s
}

const twoRuns = "101\t/data/teams/Alpha/workspaces/Manager\tgrok --no-auto-update -p go\n" +
	"102\t/data/tenant-interactive-agent-workspaces/person-example.com\tclaude --dangerously-skip-permissions\n" +
	"103\t/data/tenant-interactive-agent-workspaces/person-example.com\tnode /data/npm-global/bin/claude --resume\n" +
	"104\t/data\tdotnet /app/Harness.Host.dll\n"

func TestUpRestartsWithoutAskingAndNamesWhatItStops(t *testing.T) {
	s := driftedRunningInstance(twoRuns)
	deps := stubbed(s)
	deps.Interactive = false
	code, out, errOut := run(t, deps, "up")
	all := out + errOut
	if code != 0 || !strings.Contains(calls(s), "podman rm -f yawble") ||
		!strings.Contains(all, "Alpha/Manager (grok)") || !strings.Contains(all, "a Concierge session (claude)") ||
		strings.Count(all, "Concierge session") != 1 || strings.Contains(all, "Restart now") {
		t.Errorf("exit %d out %q err %q calls:\n%s", code, out, errOut, calls(s))
	}
}

func TestUpRestartsQuietlyWhenNoAgentIsRunning(t *testing.T) {
	s := driftedRunningInstance("104\t/data\tdotnet /app/Harness.Host.dll\n")
	deps := stubbed(s)
	deps.Interactive = false
	if code, out, errOut := run(t, deps, "up"); code != 0 || !strings.Contains(calls(s), "podman rm -f yawble") || strings.Contains(out+errOut, "stops") {
		t.Errorf("exit %d out %q err %q", code, out, errOut)
	}
}

func TestAgentProcessesTakeTheirWorkspaceName(t *testing.T) {
	listing := "1\t/data/teams/Beta/repos/Yawble/wt-DeveloperInes\tcodex exec go\n" +
		"2\t\tcopilot --allow-all\n" +
		"3\t/data/teams/Alpha/workspaces/TesterColin\t/data/npm-global/bin/claude -p x\n" +
		"4\t/data/teams/Alpha/workspaces/TesterColin\tclaude -p x\n" +
		"5\t/tmp\tgrep claude\n"
	s := driftedRunningInstance(listing)
	deps := stubbed(s)
	deps.Interactive = false
	code, out, errOut := run(t, deps, "up")
	want := "this stops Beta/DeveloperInes (codex), an agent (copilot), Alpha/TesterColin (claude)"
	if code != 0 || !strings.Contains(out+errOut, want) {
		t.Errorf("exit %d, want %q in out %q err %q", code, want, out, errOut)
	}
}
