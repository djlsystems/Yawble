package doctor_test

import (
	"bytes"
	"context"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/djlsystems/yawble/cli/internal/doctor"
)

func installsReport(t *testing.T) doctor.HostReport {
	t.Helper()
	raw, err := os.ReadFile(filepath.Join("testdata", "host-doctor-installs.json"))
	if err != nil {
		t.Fatal(err)
	}
	r, err := doctor.ParseHostReport(string(raw))
	if err != nil {
		t.Fatal(err)
	}
	return r
}

func agentNamed(r doctor.HostReport, name string) doctor.Agent {
	for _, a := range r.Agents {
		if a.Agent == name {
			return a
		}
	}
	return doctor.Agent{}
}

// The Host's record of its background installs decodes, and an agent it is installing is never
// "not installed" - not in `yawble agents`, not in the doctor's agents row - while one whose install
// failed says why.
func TestAnAgentBeingInstalledReadsInstallingNeverNotInstalled(t *testing.T) {
	r := installsReport(t)
	if r.AgentInstalls == nil || len(r.AgentInstalls.Items) != 3 || r.AgentInstalls.GoogleChrome.Word() != "40%" {
		t.Fatalf("agentInstalls %+v", r.AgentInstalls)
	}
	if got := r.AgentInstalls.ProgressText(); got != "claude installing, codex failed, copilot done, google-chrome 40%" {
		t.Errorf("progress %q", got)
	}
	claude := agentNamed(r, "claude")
	if claude.Install != "installing" || claude.NotInstalled() || claude.NotInUse() || claude.CanRun() {
		t.Errorf("claude %+v", claude)
	}
	if codex := agentNamed(r, "codex"); codex.InstallFailed == nil || !strings.Contains(*codex.InstallFailed, "network unreachable") {
		t.Errorf("codex %+v", codex)
	}

	var out bytes.Buffer
	doctor.RenderAgents(&out, r.Agents, false)
	text := out.String()
	block := text[strings.Index(text, "claude\n"):strings.Index(text, "codex\n")]
	if !strings.Contains(block, "installed   installing") || strings.Contains(block, "installed   no") || strings.Contains(block, "not installed") {
		t.Errorf("claude's block:\n%s", block)
	}
	if !strings.Contains(text, "install     failed: `/bin/sh /opt/harness/ensure-agent-clis.sh codex` exited 1: npm ERR! network unreachable") {
		t.Errorf("agents:\n%s", text)
	}

	found := false
	for _, c := range doctor.InstanceChecks(&r, nil, time.Date(2026, 11, 1, 10, 0, 0, 0, time.UTC)) {
		if c.Name != "agents" {
			continue
		}
		found = true
		if !strings.Contains(c.Detail, "claude installing") || strings.Contains(c.Detail, "claude not installed") {
			t.Errorf("agents check %+v", c)
		}
	}
	if !found {
		t.Error("the doctor has no agents check")
	}
}

func fetching(reports ...*doctor.HostReport) func(context.Context) (*doctor.HostReport, error) {
	i := 0
	return func(context.Context) (*doctor.HostReport, error) {
		r := reports[min(i, len(reports)-1)]
		i++
		return r, nil
	}
}

func withInstalls(items []doctor.AgentInstallItem, chrome *doctor.GoogleChrome) *doctor.HostReport {
	worker := "worker-1"
	return &doctor.HostReport{AgentInstalls: &doctor.AgentInstalls{Worker: &worker, Items: items, GoogleChrome: chrome}}
}

func fastWait(t *testing.T, bound time.Duration) {
	bound0, interval0 := doctor.AgentsWaitBound, doctor.AgentsWaitInterval
	doctor.AgentsWaitBound, doctor.AgentsWaitInterval = bound, time.Millisecond
	t.Cleanup(func() { doctor.AgentsWaitBound, doctor.AgentsWaitInterval = bound0, interval0 })
}

func pct(n int) *int { return &n }

// `up` prints each change of the agents' progress in the shape a person reads, once per change, and
// stops waiting as soon as nothing is installing.
func TestWaitForAgentsPrintsProgressAndEndsWhenTheyAreReady(t *testing.T) {
	fastWait(t, time.Minute)
	installing := withInstalls([]doctor.AgentInstallItem{{Agent: "claude", Command: "claude", State: "installing"}, {Agent: "codex", Command: "codex", State: "ready"}},
		&doctor.GoogleChrome{State: "installing", Percent: pct(40)})
	ready := withInstalls([]doctor.AgentInstallItem{{Agent: "claude", Command: "claude", State: "ready"}, {Agent: "codex", Command: "codex", State: "ready"}},
		&doctor.GoogleChrome{State: "ready"})
	var out bytes.Buffer

	if err := doctor.WaitForAgents(context.Background(), fetching(installing, installing, ready), "http://localhost:8080", &out); err != nil {
		t.Fatal(err)
	}
	want := "worker worker-1: getting agents ready (claude installing, codex done, google-chrome 40%)\n" +
		"agents are ready (claude done, codex done, google-chrome done)\n"
	if out.String() != want {
		t.Errorf("got\n%s\nwant\n%s", out.String(), want)
	}
}

// Agents still installing when the bound ends are no failure: `up` says where the board is and how to
// see when they are ready, and returns nil.
func TestWaitForAgentsStopsAtItsBoundWithoutFailing(t *testing.T) {
	fastWait(t, 30*time.Millisecond)
	installing := withInstalls([]doctor.AgentInstallItem{{Agent: "claude", Command: "claude", State: "installing"}}, nil)
	var out bytes.Buffer

	if err := doctor.WaitForAgents(context.Background(), fetching(installing), "http://localhost:8080", &out); err != nil {
		t.Fatal(err)
	}
	if !strings.HasSuffix(out.String(), "Yawble is up at http://localhost:8080; agents are still being installed - `yawble status` shows when they are ready\n") {
		t.Errorf("out %q", out.String())
	}
}

// A failed vendor is said, by name, and is no failure of `up` either.
func TestWaitForAgentsNamesAFailedInstallAndSucceeds(t *testing.T) {
	fastWait(t, time.Minute)
	done := withInstalls([]doctor.AgentInstallItem{{Agent: "claude", Command: "claude", State: "ready"}, {Agent: "codex", Command: "codex", State: "failed"}}, nil)
	var out bytes.Buffer

	if err := doctor.WaitForAgents(context.Background(), fetching(done), "http://x", &out); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(out.String(), "agents: codex could not be installed; the others are ready") {
		t.Errorf("out %q", out.String())
	}
}

// An older Host, or one run outside the image (no installer, so no record), or a report that cannot be
// read: nothing to wait for, and nothing said.
func TestWaitForAgentsWithNoRecordReturnsAtOnce(t *testing.T) {
	fastWait(t, time.Minute)
	for _, fetch := range []func(context.Context) (*doctor.HostReport, error){
		fetching(&doctor.HostReport{}),
		func(context.Context) (*doctor.HostReport, error) { return nil, errors.New("no report") },
	} {
		var out bytes.Buffer
		started := time.Now()
		if err := doctor.WaitForAgents(context.Background(), fetch, "http://x", &out); err != nil || out.Len() != 0 || time.Since(started) > time.Second {
			t.Errorf("err %v out %q after %s", err, out.String(), time.Since(started))
		}
	}
}
