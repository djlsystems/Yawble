package cli_test

import (
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/engine"
)

const (
	claudeLaunchOk     = `"launch":{"result":"ok","exitCode":0,"stderrTail":null,"detail":null}`
	claudeLaunchFailed = `"launch":{"result":"failed","exitCode":1,"stderrTail":"boom","detail":"The launch exited 1."}`
)

// codexSignedInFailing is the Claude-only report with codex signed in too, but failing to start.
func codexSignedInFailing() string {
	return strings.NewReplacer(
		`"authenticated":false,"detail":"The status command exited 1.","credentialVariable":"OPENAI_API_KEY","launch":{"result":"not checked","exitCode":null,"stderrTail":null,"detail":"No preset starts `+"`codex`"+`, so nothing was started."}`,
		`"authenticated":true,"detail":"The status command exited 0.","credentialVariable":"OPENAI_API_KEY","launch":{"result":"failed","exitCode":1,"stderrTail":"boom","detail":"The launch exited 1."}`,
	).Replace(claudeOnlyReport)
}

func agentsDeps(t *testing.T, report string) (*engine.Scripted, string) {
	t.Helper()
	s := runningScript()
	s.On(doctorExec, engine.Result{Stdout: report})
	return s, t.TempDir()
}

// block is one agent's block of `yawble agents`.
func block(out, agent string) string {
	for _, b := range strings.Split(out, "\n\n") {
		if name, _, _ := strings.Cut(b, "\n"); name == agent {
			return b
		}
	}
	return ""
}

// An agent that is signed in but did not start has a line saying what to do about it, naming a
// command, and the line above the blocks does not tell the person to sign in what is signed in.
func TestASignedInAgentThatDidNotStartHasAFixLineNamingTheCommand(t *testing.T) {
	failed := strings.Replace(claudeOnlyReport, claudeLaunchOk, claudeLaunchFailed, 1)
	s, dir := agentsDeps(t, failed)
	deps := stubbed(s)
	deps.ConfigDir = dir
	_, out, _ := run(t, deps, "doctor", "--json")
	if v, detail, fix := verdict(t, parseDoctor(t, out), "agents"); v != "warn" || fix != "yawble agents" {
		t.Errorf("with no agent able to run, agents should warn: %s %q fix %q", v, detail, fix)
	}
	code, out, errOut := run(t, deps, "agents")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	claude := block(out, "claude")
	var fix string
	for _, line := range strings.Split(claude, "\n") {
		if strings.HasPrefix(strings.TrimSpace(line), "to fix") {
			fix = line
		}
	}
	if fix == "" {
		t.Fatalf("claude is signed in and did not start, and has no fix line:\n%s", out)
	}
	if !strings.Contains(fix, "yawble down, then yawble up") || !strings.Contains(fix, "yawble agents --details") {
		t.Errorf("the fix line names no command that does it: %q", fix)
	}
	top, _, _ := strings.Cut(out, "\n")
	if strings.Contains(top, "sign one in") || !strings.Contains(top, "claude is signed in but did not start") {
		t.Errorf("the top line should say claude did not start, not to sign in: %q", top)
	}
}

// With one agent able to run, another that is signed in but did not start is named in the agents
// row without a warning, and `yawble agents` says the same; with every signed-in agent failing,
// nothing can run and the row warns.
func TestOneAgentRunningKeepsAgentsOkWhileAnotherFailsToStart(t *testing.T) {
	s, dir := agentsDeps(t, codexSignedInFailing())
	deps := stubbed(s)
	deps.ConfigDir = dir
	_, out, _ := run(t, deps, "doctor", "--json")
	v, detail, fix := verdict(t, parseDoctor(t, out), "agents")
	if v != "ok" || fix != "" {
		t.Errorf("claude runs, so agents is ok: %s %q fix %q", v, detail, fix)
	}
	if !strings.Contains(detail, "codex signed in, source home, launch FAILED, exit 1, see yawble agents") {
		t.Errorf("the row should name codex's failed launch and where to look: %q", detail)
	}
	_, out, _ = run(t, deps, "agents")
	top, _, _ := strings.Cut(out, "\n")
	if !strings.HasPrefix(top, "claude can run agent work") || !strings.Contains(top, "codex is signed in but did not start") {
		t.Errorf("the top line should say claude runs and codex did not start: %q", top)
	}
	if !strings.Contains(block(out, "codex"), "to fix") {
		t.Errorf("codex did not start and has no fix line:\n%s", out)
	}

	both := strings.Replace(codexSignedInFailing(), claudeLaunchOk, claudeLaunchFailed, 1)
	s, dir = agentsDeps(t, both)
	deps = stubbed(s)
	deps.ConfigDir = dir
	_, out, _ = run(t, deps, "doctor", "--json")
	if v, detail, fix := verdict(t, parseDoctor(t, out), "agents"); v != "warn" || fix != "yawble agents" {
		t.Errorf("no signed-in agent starts, so agents warns: %s %q fix %q", v, detail, fix)
	}
}

// The default `yawble agents` is what a person acts on; the source, the probe's time and the
// launch's own words are developer detail, shown with --details.
func TestAgentsDefaultLeavesDeveloperLinesForDetails(t *testing.T) {
	s, dir := agentsDeps(t, codexSignedInFailing())
	deps := stubbed(s)
	deps.ConfigDir = dir
	developer := []string{"  source  ", "  measured  ", "  launch why  ", "  stderr  ", "  detail  "}
	_, out, _ := run(t, deps, "agents")
	for _, line := range developer {
		if strings.Contains(out, line) {
			t.Errorf("the default list has the developer line %q:\n%s", strings.TrimSpace(line), out)
		}
	}
	code, out, errOut := run(t, deps, "agents", "--details")
	if code != 0 {
		t.Fatalf("--details: exit %d: %s %s", code, out, errOut)
	}
	for _, line := range []string{"  source      home", "  launch why  The launch exited 1.", "  stderr      boom", "  detail      The status command exited 1."} {
		if !strings.Contains(out, line) {
			t.Errorf("--details lacks %q:\n%s", line, out)
		}
	}
}

// With several signed-in agents that did not start, the top line points at their "to fix" lines.
func TestAgentsTopLineSaysTheirFixLinesForSeveral(t *testing.T) {
	both := strings.Replace(codexSignedInFailing(), claudeLaunchOk, claudeLaunchFailed, 1)
	s, dir := agentsDeps(t, both)
	deps := stubbed(s)
	deps.ConfigDir = dir
	_, out, _ := run(t, deps, "agents")
	top, _, _ := strings.Cut(out, "\n")
	if top != "No agent can run yet: claude and codex are signed in but did not start; their \"to fix\" lines say what to do." {
		t.Errorf("top line: %q", top)
	}
}

// Doctor's default agents row leaves the source and when the sign-ins were measured to --details.
func TestDoctorShowsTheAgentsSourceOnlyWithDetails(t *testing.T) {
	s, dir := agentsDeps(t, codexSignedInFailing())
	deps := stubbed(s)
	deps.ConfigDir = dir
	_, out, _ := run(t, deps, "doctor")
	row := lineOf(out, "agents")
	if row == "" || strings.Contains(row, "source") || strings.Contains(row, "sign-ins measured") {
		t.Errorf("default agents row: %q\n%s", row, out)
	}
	if !strings.Contains(row, "codex signed in, launch FAILED, exit 1, see yawble agents") {
		t.Errorf("default agents row should still name codex's failed launch: %q", row)
	}
	_, out, _ = run(t, deps, "doctor", "--details")
	if row := lineOf(out, "agents"); !strings.Contains(row, "source home") || !strings.Contains(row, "sign-ins measured") {
		t.Errorf("--details agents row: %q", row)
	}
}

// lineOf is the doctor line whose check is name, "" when there is none.
func lineOf(out, name string) string {
	for _, l := range strings.Split(out, "\n") {
		if f := strings.Fields(l); len(f) > 1 && f[1] == name {
			return l
		}
	}
	return ""
}
