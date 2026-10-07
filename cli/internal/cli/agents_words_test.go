package cli_test

import (
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/engine"
)

// With one agent signed in and launching, agents is ok and the others are not in use: a
// Claude-only install is not warned about codex, copilot and grok for ever.
func TestDoctorAgentsIsOkWithOneAgentReadyAndTheOthersNotInUse(t *testing.T) {
	s, dir := claudeOnly(t)
	deps := stubbed(s)
	deps.ConfigDir = dir
	_, out, _ := run(t, deps, "doctor", "--json")
	v, detail, fix := verdict(t, parseDoctor(t, out), "agents")
	if v != "ok" || fix != "" {
		t.Errorf("agents: %s %q fix %q", v, detail, fix)
	}
	for _, want := range []string{"claude signed in", "codex not in use", "copilot not in use", "grok not in use"} {
		if !strings.Contains(detail, want) {
			t.Errorf("agents lacks %q: %s", want, detail)
		}
	}
	if strings.Contains(detail, "NOT signed in") {
		t.Errorf("an agent nobody uses is not a warning: %s", detail)
	}
}

func TestAgentsWithOneAgentReadyListsTheOthersAsNotInUse(t *testing.T) {
	s, dir := claudeOnly(t)
	deps := stubbed(s)
	deps.ConfigDir = dir
	code, out, errOut := run(t, deps, "agents")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if !strings.Contains(out, "claude can run agent work") {
		t.Errorf("agents does not say which agent can run:\n%s", out)
	}
	if n := strings.Count(out, "  in use      no"); n != 3 {
		t.Errorf("want codex, copilot and grok as not in use, got %d:\n%s", n, out)
	}
	if strings.Contains(out, "signed in   NO") || strings.Contains(out, "to fix") {
		t.Errorf("an agent nobody uses is not a fault to fix:\n%s", out)
	}
}

// With no agent able to run, the agents row still warns and says what to do.
func TestDoctorAgentsWarnsWhenNoAgentCanRun(t *testing.T) {
	none := strings.Replace(claudeOnlyReport, `"authenticated":true`, `"authenticated":false`, 1)
	s := runningScript()
	s.On(doctorExec, engine.Result{Stdout: none})
	deps := stubbed(s)
	deps.ConfigDir = t.TempDir()
	_, out, _ := run(t, deps, "doctor", "--json")
	v, detail, fix := verdict(t, parseDoctor(t, out), "agents")
	if v != "warn" || fix != "yawble agents" || !strings.Contains(detail, "claude NOT signed in") {
		t.Errorf("agents: %s %q fix %q", v, detail, fix)
	}
	_, out, _ = run(t, deps, "agents")
	if !strings.Contains(out, "No agent can run yet") {
		t.Errorf("agents does not say no agent can run:\n%s", out)
	}
}

// Every line that tells a person what to do names the command that does it, and nothing ends in
// a stray full stop the CLI itself printed (copilot's version says "1.0.92.").
func TestAgentsFixLinesNameTheCommandAndCarryNoStrayPunctuation(t *testing.T) {
	none := strings.Replace(claudeOnlyReport, `"authenticated":true`, `"authenticated":false`, 1)
	s := runningScript()
	s.On(doctorExec, engine.Result{Stdout: none})
	deps := stubbed(s)
	deps.ConfigDir = t.TempDir()
	_, out, _ := run(t, deps, "agents")
	fixes := 0
	for _, line := range strings.Split(out, "\n") {
		label := strings.TrimSpace(line)
		if !strings.HasPrefix(label, "to fix") && !strings.HasPrefix(label, "to use it") {
			continue
		}
		fixes++
		if !strings.Contains(line, "yawble secret set ") {
			t.Errorf("a fix line names no command: %q", line)
		}
		if strings.Contains(line, "env file beside") {
			t.Errorf("a fix line names a file without its path: %q", line)
		}
	}
	if fixes != 4 {
		t.Errorf("want a fix line for each of the 4 agents, got %d:\n%s", fixes, out)
	}
	for _, want := range []string{"yawble secret set ANTHROPIC_API_KEY", "yawble secret set OPENAI_API_KEY", "yawble secret set GH_TOKEN", "yawble secret set XAI_API_KEY"} {
		if !strings.Contains(out, want) {
			t.Errorf("agents lacks %q:\n%s", want, out)
		}
	}
	if !strings.Contains(out, "installed   yes, GitHub Copilot CLI 1.0.92\n") {
		t.Errorf("copilot's version keeps a stray full stop:\n%s", out)
	}
	_, out, _ = run(t, deps, "doctor", "--details")
	if strings.Contains(out, "1.0.92.") {
		t.Errorf("doctor keeps copilot's stray full stop:\n%s", out)
	}
}
