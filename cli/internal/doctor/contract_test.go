package doctor_test

import (
	"bytes"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/djlsystems/yawble/cli/internal/doctor"
)

// The Host's own --doctor report, written by DoctorReportCliContractTests from a real launch check
// (a fake CLI aborting under a 64 MB data limit, leaking a credential on stderr) and the wip
// figures the Host recorded under cgroup, rlimit and no per-run cap, decodes here into the launch
// result, the Host's figures and its running Concierge sessions, with nothing derived and nothing
// unredacted.
func TestTheHostsOwnDoctorReportDecodes(t *testing.T) {
	for _, c := range []struct{ file, runMemory string }{
		{"host-doctor-cgroup.json", "cgroup, 1792 MB per run: "},
		{"host-doctor-rlimit.json", "rlimit, 2048 MB per run: "},
		{"host-doctor-none.json", "no per-run memory cap: "},
	} {
		raw, err := os.ReadFile(filepath.Join("testdata", c.file))
		if err != nil {
			t.Fatal(err)
		}
		r, err := doctor.ParseHostReport("Host log: /data/logs/x\n" + string(raw))
		if err != nil {
			t.Fatalf("%s: %v", c.file, err)
		}
		if r.Wip == nil || r.Wip.Limit == nil || r.Wip.RunMemory == nil {
			t.Fatalf("%s: wip not decoded: %+v", c.file, r.Wip)
		}

		var failed *doctor.Agent
		for i, a := range r.Agents {
			if !a.IsInstalled() || a.MeasuredText() != "2026-10-02 12:00 UTC on worker w1" {
				t.Errorf("%s: %s installed %v measured %q, want the Host's recorded probe on w1", c.file, a.Agent, a.Installed, a.MeasuredText())
			}
			if a.Authenticated == nil || !*a.Authenticated || a.Detail == "" {
				t.Errorf("%s: %s authenticated %v detail %q, want signed in with the probe's detail", c.file, a.Agent, a.Authenticated, a.Detail)
			}
			if a.Launch == nil {
				t.Errorf("%s: %s has no launch decoded", c.file, a.Agent)
				continue
			}
			if a.Launch.Result == "failed" {
				failed = &r.Agents[i]
			} else if a.LaunchText() != "not checked" {
				t.Errorf("%s: %s launch %q, want not checked", c.file, a.Agent, a.LaunchText())
			}
		}
		if failed == nil {
			t.Fatalf("%s: no failed launch decoded", c.file)
		}
		if failed.LaunchText() != "FAILED, exit 134" || failed.Launch.StderrTail == nil ||
			!strings.Contains(*failed.Launch.StderrTail, "[redacted]") || strings.Contains(*failed.Launch.StderrTail, "sk-ant") {
			t.Errorf("%s: failed launch %q, stderr %v", c.file, failed.LaunchText(), failed.Launch.StderrTail)
		}

		rows := map[string]doctor.Check{}
		for _, ch := range doctor.InstanceChecks(&r, nil, time.Now()) {
			rows[ch.Name] = ch
		}
		if got := rows["run memory"].Detail; !strings.HasPrefix(got, c.runMemory) {
			t.Errorf("%s: run memory %q, want prefix %q", c.file, got, c.runMemory)
		}
		if got := rows["running limit"]; got.Verdict != doctor.OK || !strings.HasPrefix(got.Detail, "4, from the configuration bound (the Host's answer: ") {
			t.Errorf("%s: running limit %s %q", c.file, got.Verdict, got.Detail)
		}
		if got := rows["agents"]; got.Verdict != doctor.Warn || !strings.Contains(got.Detail, failed.Agent+" ") ||
			!strings.Contains(got.Detail, "launch FAILED, exit 134") {
			t.Errorf("%s: agents row %s %q", c.file, got.Verdict, got.Detail)
		}

		if r.Concierge == nil || len(r.Concierge.Sessions) != 2 {
			t.Fatalf("%s: concierge not decoded: %+v", c.file, r.Concierge)
		}
		var concierge []string
		for _, ch := range doctor.InstanceChecks(&r, nil, time.Now()) {
			if ch.Name == "concierge" {
				if ch.Verdict != doctor.Info {
					t.Errorf("%s: concierge row %s, want information", c.file, ch.Verdict)
				}
				concierge = append(concierge, ch.Detail)
			}
		}
		want := []string{
			"person@example.test: on worker-1, last activity 2026-10-02 12:20 UTC (call), would end at 2026-10-02 13:20 UTC, 300 MB",
			"user-2: on worker-1, last activity 2026-10-02 12:30 UTC (typed), open in a browser, so not ended, memory not measured",
		}
		if strings.Join(concierge, "\n") != strings.Join(want, "\n") {
			t.Errorf("%s: concierge rows\n%s\nwant\n%s", c.file, strings.Join(concierge, "\n"), strings.Join(want, "\n"))
		}

		var out bytes.Buffer
		doctor.RenderAgents(&out, r.Agents, true)
		if !strings.Contains(out.String(), "  launch      FAILED, exit 134") || !strings.Contains(out.String(), "[redacted]") {
			t.Errorf("%s: agents output lacks the failed launch:\n%s", c.file, out.String())
		}
	}
}
