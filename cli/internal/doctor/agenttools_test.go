package doctor_test

import (
	"bytes"
	"errors"
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/doctor"
)

// The Host's --doctor line with an agentTools section, in the shape HostDoctor serializes.
const toolsStdout = `{"at":"2026-09-30T02:00:00+00:00","dataRoot":{"path":"/data","writable":true,"freeBytes":1},"database":{"path":"/data/messages.db","exists":true,"schema":null,"error":null},"backups":{"directory":"/data/backups","dailyCount":0,"newestDailyAt":null},"versionsRecordedAt":null,"agents":[],"agentLaunch":null,"agentTools":{"at":"2026-09-30T01:59:00+00:00","presets":[` +
	`{"preset":"claude","mode":"interactive","command":"claude","verdict":"concierge","foreign":[],"loaded":[{"kind":"connector","name":"claude.ai Gmail","source":"claude.ai account","off":null}],"switchedOff":[],"gaps":[],"ran":["claude mcp list"],"detail":null},` +
	`{"preset":"claude-headless","mode":"headless","command":"claude","verdict":"isolated","foreign":[],"loaded":[],"switchedOff":[],"gaps":["A repository's own .claude/settings.json still loads."],"ran":["claude mcp list"],"detail":null},` +
	`{"preset":"grok-headless","mode":"headless","command":"grok","verdict":"isolated","foreign":[],"loaded":[{"kind":"server","name":"harness","source":"/data/agent-home/.grok/config.toml","off":null},{"kind":"skill","name":"docs","source":null,"off":null}],"switchedOff":[],"gaps":[],"ran":["grok inspect --json"],"detail":null},` +
	`{"preset":"copilot-headless","mode":"headless","command":"copilot","verdict":"isolated","foreign":[],"loaded":[],"switchedOff":[{"kind":"server","name":"github-mcp-server","source":"builtin","off":"disabled"}],"gaps":[],"ran":[],"detail":null},` +
	`{"preset":"echo","mode":"headless","command":"cat","verdict":"notAModel","foreign":[],"loaded":[],"switchedOff":[],"gaps":[],"ran":[],"detail":null}]}}`

func report(t *testing.T, stdout string) *doctor.HostReport {
	t.Helper()
	r, err := doctor.ParseHostReport(stdout)
	if err != nil {
		t.Fatal(err)
	}
	return &r
}

func TestEveryMemberPresetIsolatedIsOkAndTheConciergeIsNotAWarning(t *testing.T) {
	c := doctor.AgentToolsCheck(report(t, toolsStdout), nil)
	if c.Verdict != doctor.OK {
		t.Fatalf("verdict %s: %s", c.Verdict, c.Detail)
	}
	if !strings.Contains(c.Detail, "claude-headless isolated") || strings.Contains(c.Detail, "claude.ai") || strings.Contains(c.Detail, "echo") {
		t.Errorf("detail %q", c.Detail)
	}
}

func TestAForeignToolOrAMissingDeclarationWarnsAndNamesIt(t *testing.T) {
	foreign := strings.Replace(toolsStdout,
		`"preset":"claude-headless","mode":"headless","command":"claude","verdict":"isolated","foreign":[]`,
		`"preset":"claude-headless","mode":"headless","command":"claude","verdict":"foreignFound","foreign":[{"kind":"connector","name":"claude.ai Gmail","source":null,"off":null}]`, 1)
	c := doctor.AgentToolsCheck(report(t, foreign), nil)
	if c.Verdict != doctor.Warn || !strings.Contains(c.Detail, "claude-headless FOREIGN TOOLS: claude.ai Gmail") || c.Fix == "" {
		t.Errorf("foreign: %+v", c)
	}

	unverified := strings.Replace(toolsStdout, `"verdict":"isolated"`, `"verdict":"notVerified"`, 1)
	if c := doctor.AgentToolsCheck(report(t, unverified), nil); c.Verdict != doctor.Warn || !strings.Contains(c.Detail, "not verified") {
		t.Errorf("not verified: %+v", c)
	}

	unmeasured := strings.Replace(toolsStdout, `"verdict":"isolated"`, `"verdict":"notMeasured"`, 1)
	if c := doctor.AgentToolsCheck(report(t, unmeasured), nil); c.Verdict != doctor.OK || !strings.Contains(c.Detail, "claude-headless not measured") {
		t.Errorf("not measured: %+v", c)
	}
}

func TestNoPreFlightOrNoReportIsSkipNotClean(t *testing.T) {
	old := `{"at":"x","dataRoot":{"path":"/data","writable":true},"database":{"path":"p","exists":false},"backups":{"directory":"d","dailyCount":0},"agents":[]}`
	if c := doctor.AgentToolsCheck(report(t, old), nil); c.Verdict != doctor.Skip {
		t.Errorf("no record: %+v", c)
	}
	if c := doctor.AgentToolsCheck(nil, errors.New("not running")); c.Verdict != doctor.Skip {
		t.Errorf("no report: %+v", c)
	}
}

func TestTheBlockListsEachPresetItsGapsAndTheConciergesToolsAsInformation(t *testing.T) {
	var out bytes.Buffer
	doctor.RenderAgentTools(&out, report(t, toolsStdout).AgentTools)
	text := out.String()

	for _, want := range []string{
		"claude (interactive, claude)\n  state       Concierge: keeps every tool the person set up (information)\n  has         claude.ai Gmail",
		"claude-headless (headless, claude)\n  state       isolated",
		"  gap         A repository's own .claude/settings.json still loads.",
		"grok-headless (headless, grok)\n  state       isolated: harness and its own tools only\n  loads       harness\n  skills      1 (instructions, not tools)",
		"  switched off github-mcp-server (disabled)",
	} {
		if !strings.Contains(text, want) {
			t.Errorf("missing %q in:\n%s", want, text)
		}
	}
	if strings.Contains(text, "echo") {
		t.Errorf("a program that runs no model is listed:\n%s", text)
	}
}
