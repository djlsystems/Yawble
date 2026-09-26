// Package doctor turns what was observed into verdicts. Host-side checks are pure functions over
// an Observed struct; in-container checks are pure functions over the Host's own --doctor report,
// which is parsed here and computed there, once, in C#. Nothing is estimated: a check that could
// not be made is Skip with the reason, and Skip is not failure.
package doctor

import (
	"encoding/json"
	"errors"
	"fmt"
	"strings"
)

// HostReport mirrors HostDoctor's JSON, camelCase. Pointers are fields the Host reports as null
// (not measured) or that an older Host does not have at all. Unknown fields are ignored.
type HostReport struct {
	At       string `json:"at"`
	DataRoot struct {
		Path      string `json:"path"`
		Writable  bool   `json:"writable"`
		FreeBytes *int64 `json:"freeBytes"`
	} `json:"dataRoot"`
	Database struct {
		Path   string  `json:"path"`
		Exists bool    `json:"exists"`
		Schema *Schema `json:"schema"`
		Error  *string `json:"error"`
	} `json:"database"`
	Backups struct {
		Directory     string  `json:"directory"`
		DailyCount    int     `json:"dailyCount"`
		NewestDailyAt *string `json:"newestDailyAt"`
	} `json:"backups"`
	VersionsRecordedAt *string `json:"versionsRecordedAt"`
	Agents             []Agent `json:"agents"`
}

type Schema struct {
	Applied  []string `json:"applied"`
	Pending  []string `json:"pending"`
	Unknown  []string `json:"unknown"`
	Accepted bool     `json:"accepted"`
}

// Agent is one command from the Host's auth-probes.json. Authenticated nil is "not measured".
type Agent struct {
	Agent              string  `json:"agent"`
	Installed          bool    `json:"installed"`
	Version            *string `json:"version"`
	Authenticated      *bool   `json:"authenticated"`
	Detail             string  `json:"detail"`
	CredentialVariable *string `json:"credentialVariable"`
}

// ErrNoReport is stdout with no JSON object on its last line: the Host in the image does not
// know --doctor and printed its ordinary start-up prose instead.
var ErrNoReport = errors.New("the instance's image predates yawble doctor; update it to a Yawble image with the --doctor switch")

// ParseHostReport reads the LAST non-empty line of the Host's stdout as the report. The Host may
// print before its operator commands run, so earlier lines are not the report.
func ParseHostReport(stdout string) (HostReport, error) {
	var r HostReport
	lines := strings.Split(strings.ReplaceAll(stdout, "\r\n", "\n"), "\n")
	for i := len(lines) - 1; i >= 0; i-- {
		line := strings.TrimSpace(lines[i])
		if line == "" {
			continue
		}
		if !strings.HasPrefix(line, "{") {
			return r, ErrNoReport
		}
		if err := json.Unmarshal([]byte(line), &r); err != nil {
			return r, fmt.Errorf("the instance's doctor report could not be read: %w", err)
		}
		return r, nil
	}
	return r, ErrNoReport
}
