package cli_test

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/djlsystems/yawble/cli/internal/engine"
)

// A Claude-only install as the Mac walk-through met it: claude signed in and launching, codex,
// copilot and grok installed and not signed in, one daily copy in the data volume, no per-run
// memory cap on the engine, and the Host's tool pre-flight with its gap notes.
const claudeOnlyReport = `{"at":"2026-10-06T21:04:19+00:00","dataRoot":{"path":"/data","writable":true,"freeBytes":100000000000},"database":{"path":"/data/messages.db","exists":true,"schema":{"applied":["a","b"],"pending":[],"unknown":[],"accepted":true},"error":null},"backups":{"directory":"/data/backups","dailyCount":1,"newestDailyAt":"2026-10-06T18:47:24+00:00"},"versionsRecordedAt":null,` +
	`"agents":[` +
	`{"agent":"claude","installed":true,"version":"2.1.286 (Claude Code)","authenticated":true,"detail":"The status command exited 0.","credentialVariable":"ANTHROPIC_API_KEY","launch":{"result":"ok","exitCode":0,"stderrTail":null,"detail":null},"credentialSource":"home","issuedSet":null,"measuredAt":"2026-10-06T21:00:00+00:00","measuredOn":"w1"},` +
	`{"agent":"codex","installed":true,"version":"codex-cli 0.157.0","authenticated":false,"detail":"The status command exited 1.","credentialVariable":"OPENAI_API_KEY","launch":{"result":"not checked","exitCode":null,"stderrTail":null,"detail":"No preset starts ` + "`codex`" + `, so nothing was started."},"credentialSource":"home","issuedSet":null,"measuredAt":"2026-10-06T21:00:00+00:00","measuredOn":"w1"},` +
	`{"agent":"copilot","installed":true,"version":"GitHub Copilot CLI 1.0.92.","authenticated":false,"detail":"The status command exited 1.","credentialVariable":"GH_TOKEN","launch":{"result":"not checked","exitCode":null,"stderrTail":null,"detail":"No preset starts ` + "`copilot`" + `, so nothing was started."},"credentialSource":"home","issuedSet":null,"measuredAt":"2026-10-06T21:00:00+00:00","measuredOn":"w1"},` +
	`{"agent":"grok","installed":true,"version":"0.1.9","authenticated":false,"detail":"The status command exited 1.","credentialVariable":"XAI_API_KEY","launch":{"result":"not checked","exitCode":null,"stderrTail":null,"detail":"No preset starts ` + "`grok`" + `, so nothing was started."},"credentialSource":"home","issuedSet":null,"measuredAt":"2026-10-06T21:00:00+00:00","measuredOn":"w1"}],` +
	`"agentTools":{"at":"2026-10-06T21:00:00+00:00","presets":[` +
	`{"preset":"claude-headless","mode":"member","command":"claude","verdict":"isolated","foreign":[],"loaded":[{"kind":"server","name":"harness","source":null,"off":null}],"switchedOff":[],"gaps":[],"ran":[],"detail":null},` +
	`{"preset":"codex-headless","mode":"member","command":"codex","verdict":"notMeasured","foreign":[],"loaded":[],"switchedOff":[],"gaps":["CODEX_HOME is the shared home; a config.toml there is read by every run"],"ran":[],"detail":"codex is not signed in"},` +
	`{"preset":"copilot-headless","mode":"member","command":"copilot","verdict":"notMeasured","foreign":[],"loaded":[],"switchedOff":[],"gaps":["COPILOT_HOME is the shared home; its mcp-config.json is read by every run"],"ran":[],"detail":"copilot is not signed in"},` +
	`{"preset":"grok-headless","mode":"member","command":"grok","verdict":"notMeasured","foreign":[],"loaded":[],"switchedOff":[],"gaps":["~/.grok/config.toml is read by every run and cannot be switched off"],"ran":[],"detail":"grok is not signed in"}]},` +
	`"wip":{"limit":{"limit":3,"bound":"workers","cpuBound":4,"cpus":5,"memoryBound":3,"memoryLimitMb":6144,"memoryPerRunMb":2048,"reason":"sum of 1 worker's bound: 3"},"runMemory":{"mechanism":"none","perRunMb":null,"detail":"Run memory limits: not available - no mechanism on this machine"}}}` + "\n"

func claudeOnly(t *testing.T) (*engine.Scripted, string) {
	s := runningScript()
	// THE DAILY COPY IS AN HOUR OLD, whatever day the test runs: a fixed time read as "2 days old",
	// and stale, once the calendar passed it.
	recent := `"newestDailyAt":"` + time.Now().UTC().Add(-time.Hour).Format(time.RFC3339) + `"`
	report := strings.Replace(claudeOnlyReport, `"newestDailyAt":"2026-10-06T18:47:24+00:00"`, recent, 1)
	s.On(doctorExec, engine.Result{Stdout: "Host log: /data/logs/x\n" + report})
	return s, t.TempDir()
}

// "Am I backed up?" has one answer: the daily copies in the data volume, said to be lost with it,
// and the copies `yawble backup` wrote on this computer, in one row that cannot disagree with itself.
func TestDoctorGivesOneAnswerToAmIBackedUp(t *testing.T) {
	s, dir := claudeOnly(t)
	deps := stubbed(s)
	deps.ConfigDir = dir
	_, out, _ := run(t, deps, "doctor", "--json")
	got := parseDoctor(t, out)
	var rows []string
	for _, c := range got.Checks {
		if strings.Contains(c.Name, "backup") {
			rows = append(rows, c.Name)
		}
	}
	if len(rows) != 1 {
		t.Fatalf("want one backup row, got %v", rows)
	}
	v, detail, fix := verdict(t, got, "backups")
	for _, want := range []string{"1 daily copy in the data volume", "lost if the volume is lost", "no copy on this computer yet"} {
		if !strings.Contains(detail, want) {
			t.Errorf("backups lacks %q: %s", want, detail)
		}
	}
	if v != "warn" || !strings.Contains(fix, "yawble backup") {
		t.Errorf("only in-volume copies is not backed up: %s %q fix %q", v, detail, fix)
	}

	path := filepath.Join(t.TempDir(), "b.tar.gz")
	s2 := backupScript(t, "podman", "exited", "")
	bdeps := backupDeps(t, s2, "podman")
	bdeps.ConfigDir = dir
	if code, out, errOut := run(t, bdeps, "backup", "--output", path); code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	_, out, _ = run(t, deps, "doctor", "--json")
	v, detail, fix = verdict(t, parseDoctor(t, out), "backups")
	if v != "ok" || !strings.Contains(detail, "1 daily copy in the data volume") || !strings.Contains(detail, "newest copy on this computer: "+path+", just now") || fix != "" {
		t.Errorf("after yawble backup: %s %q fix %q", v, detail, fix)
	}

	_ = os.Remove(path)
	_, out, _ = run(t, deps, "doctor", "--json")
	if v, detail, fix := verdict(t, parseDoctor(t, out), "backups"); v != "warn" || !strings.Contains(detail, "no longer there") || !strings.Contains(fix, "yawble backup") {
		t.Errorf("after the copy moved: %s %q fix %q", v, detail, fix)
	}
}

// The default list is short and a person can act on every line; the agent tools' gap notes and
// the rest of the developer detail are behind --details.
func TestDoctorDefaultIsShortAndDetailsHasTheRest(t *testing.T) {
	s, dir := claudeOnly(t)
	deps := stubbed(s)
	deps.ConfigDir = dir
	_, short, _ := run(t, deps, "doctor")
	_, long, errOut := run(t, deps, "doctor", "--details")
	if strings.Contains(errOut, "unknown flag") {
		t.Fatalf("--details: %s", errOut)
	}
	developer := []string{"CODEX_HOME", "COPILOT_HOME", "~/.grok/config.toml", "agent tools, as the Host listed them", "agent versions", "run memory", "capacity", "release"}
	for _, word := range developer {
		if strings.Contains(short, word) {
			t.Errorf("the default list says %q:\n%s", word, short)
		}
		if !strings.Contains(long, word) {
			t.Errorf("--details lacks %q:\n%s", word, long)
		}
	}
	for _, want := range []string{"engine", "container", "health", "agents", "backups", "yawble doctor --details"} {
		if !strings.Contains(short, want) {
			t.Errorf("the default list lacks %q:\n%s", want, short)
		}
	}
	lines := strings.Count(strings.TrimSpace(short), "\n") + 1
	if lines > 20 {
		t.Errorf("the default list is %d lines, want 20 or fewer:\n%s", lines, short)
	}
	if longLines := strings.Count(long, "\n"); longLines <= lines {
		t.Errorf("--details (%d lines) shows no more than the default (%d)", longLines, lines)
	}
}

// No per-run memory cap is how this engine is, said as a fact, not as a fault.
func TestDoctorSaysNoPerRunMemoryCapAsAFactOfTheEngine(t *testing.T) {
	s, dir := claudeOnly(t)
	deps := stubbed(s)
	deps.ConfigDir = dir
	_, out, _ := run(t, deps, "doctor", "--json")
	v, detail, fix := verdict(t, parseDoctor(t, out), "run memory")
	if v != "info" || fix != "" || strings.Contains(detail, "not enforced") || !strings.Contains(detail, "not a fault") {
		t.Errorf("run memory: %s %q fix %q", v, detail, fix)
	}
}
