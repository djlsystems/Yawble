package doctor_test

import (
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/doctor"
)

const hostStdout = `Removed FORCE_COLOR from this Host's environment so agent children do not inherit it.
{"at":"2026-09-23T21:04:19+00:00","dataRoot":{"path":"/data","writable":true,"freeBytes":983330902016},"database":{"path":"/data/messages.db","exists":true,"schema":{"applied":["auth-001","messages-001"],"pending":[],"unknown":[],"accepted":true},"error":null},"backups":{"directory":"/data/backups","dailyCount":1,"newestDailyAt":"2026-09-23T18:47:24+00:00"},"versionsRecordedAt":"2026-09-23T18:47:22+00:00","agents":[{"agent":"claude","installed":true,"version":"2.1.280 (Claude Code)","authenticated":true,"detail":"Authenticated via a saved login (.claude/.credentials.json).","credentialVariable":"ANTHROPIC_API_KEY"},{"agent":"agy","installed":false,"version":null,"authenticated":null,"detail":"'agy' is not on PATH.","credentialVariable":"GEMINI_API_KEY"}]}
`

func TestParseHostReportReadsTheLastLineOnly(t *testing.T) {
	r, err := doctor.ParseHostReport(hostStdout)
	if err != nil {
		t.Fatal(err)
	}
	if r.DataRoot.Path != "/data" || r.DataRoot.FreeBytes == nil || *r.DataRoot.FreeBytes != 983330902016 {
		t.Errorf("dataRoot %+v", r.DataRoot)
	}
	if r.Database.Schema == nil || !r.Database.Schema.Accepted || len(r.Database.Schema.Applied) != 2 {
		t.Errorf("database %+v", r.Database)
	}
	if len(r.Agents) != 2 || r.Agents[0].Agent != "claude" || r.Agents[0].Authenticated == nil || !*r.Agents[0].Authenticated || r.Agents[1].Authenticated != nil || r.Agents[1].Version != nil {
		t.Errorf("agents %+v", r.Agents)
	}
	if r.Agents[0].CredentialVariable == nil || *r.Agents[0].CredentialVariable != "ANTHROPIC_API_KEY" {
		t.Errorf("credential %+v", r.Agents[0])
	}
}

// A Host report without this field still parses.
func TestParseHostReportToleratesAMissingCredentialVariable(t *testing.T) {
	r, err := doctor.ParseHostReport(`{"agents":[{"agent":"grok","installed":true,"detail":"x"}]}`)
	if err != nil || len(r.Agents) != 1 || r.Agents[0].CredentialVariable != nil {
		t.Errorf("%+v %v", r, err)
	}
}

func TestProseWithoutJsonIsAnErrorSayingTheImagePredatesDoctor(t *testing.T) {
	_, err := doctor.ParseHostReport("Host log: /data/logs/host-1.log\nNow listening on: http://0.0.0.0:8080\n")
	if err == nil || !strings.Contains(err.Error(), "predates") {
		t.Errorf("err %v", err)
	}
	if _, err := doctor.ParseHostReport(""); err == nil {
		t.Error("empty stdout must be an error")
	}
}
