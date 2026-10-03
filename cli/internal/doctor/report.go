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
	"time"
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
	// The Host's last pre-flight of its agent CLIs (agenttools.go). Nil: not recorded, not measured.
	AgentTools *AgentTools `json:"agentTools"`
	// Wip is the Host's own running limit and per-run memory (hostfigures.go). Nil: an older Host,
	// and the doctor says the figures are not known.
	Wip *HostWip `json:"wip"`
	// Workers is what control recorded of its workers (workers.go). Nil: the single-process
	// form, an older Host, or nothing recorded yet.
	Workers *WorkersRecord `json:"workers"`
	// Concierge is what the Host recorded of its running Concierge sessions (concierge.go). Nil: an
	// older Host, or nothing recorded yet.
	Concierge *ConciergeRecord `json:"concierge"`
}

type Schema struct {
	Applied  []string `json:"applied"`
	Pending  []string `json:"pending"`
	Unknown  []string `json:"unknown"`
	Accepted bool     `json:"accepted"`
}

// Agent is one command from the Host's auth-probes.json. Installed and Authenticated nil are "not
// measured": the Host's last sign-in probe had no worker answer for it, or there has been none.
// Not measured is never "not installed" and fails no check.
type Agent struct {
	Agent              string  `json:"agent"`
	Installed          *bool   `json:"installed"`
	Version            *string `json:"version"`
	Authenticated      *bool   `json:"authenticated"`
	Detail             string  `json:"detail"`
	CredentialVariable *string `json:"credentialVariable"`
	// Updating is the Host's sentence when its last probe found the command held by the platform's
	// update, which it did not ask: the install is being replaced, never "not installed".
	Updating *string `json:"updating"`
	// Kind and LanguageModel are the Host's own words for what a member runs (MemberRef.KindOf,
	// AgentLaunch.LanguageModel). The Host's report lists the auth-probes.json commands today, every
	// one a model agent, and sends neither; an entry that says it is a plugin or runs no model has
	// no sign-in, and is dropped when the report is read.
	Kind          string `json:"kind,omitempty"`
	LanguageModel *bool  `json:"languageModel,omitempty"`
	// UpdatedAt is when the installed version arrived: the first line of the Host's CLI version
	// history - a start, or a person's update through the platform - that had it after a different
	// one. Nil when the kept history never saw it change; VersionsSince is how far back that reaches.
	UpdatedAt     *string `json:"updatedAt,omitempty"`
	VersionsSince *string `json:"versionsSince,omitempty"`
	// Launch is the Host's check that the CLI starts the way a member run launches it. Nil: an
	// older Host that does not check, which reads "not known" and never ok.
	Launch *Launch `json:"launch"`
	// CredentialSource is how the preset signs in: "home" (the shared home) or "issued" (the
	// credential stored for its command). Nil: an older Host, and no source is shown.
	CredentialSource *string `json:"credentialSource"`
	// IssuedSet is whether its command's issued credential is set; nil under home.
	IssuedSet *bool `json:"issuedSet"`
	// MeasuredAt is when the Host's sign-in probe that Installed and Authenticated come from ran,
	// and MeasuredOn the worker that answered it; nil when nothing was measured, or an older Host.
	MeasuredAt *string `json:"measuredAt"`
	MeasuredOn *string `json:"measuredOn"`
}

// IsInstalled is whether the Host measured the command as installed. Not measured is not.
func (a Agent) IsInstalled() bool { return a.Installed != nil && *a.Installed }

// NotInstalled is whether the Host measured the command as missing. Not measured is not.
func (a Agent) NotInstalled() bool { return a.Installed != nil && !*a.Installed }

// MeasuredText says when the sign-in was measured and on which worker, "" when it was not.
func (a Agent) MeasuredText() string {
	if a.MeasuredAt == nil || *a.MeasuredAt == "" {
		return ""
	}
	at := *a.MeasuredAt
	if t, err := time.Parse(time.RFC3339Nano, at); err == nil {
		at = t.UTC().Format("2006-01-02 15:04 UTC")
	}
	if a.MeasuredOn == nil || *a.MeasuredOn == "" {
		return at
	}
	return at + " on worker " + *a.MeasuredOn
}

// Issued says whether the agent signs in through its command's issued credential.
func (a Agent) Issued() bool { return a.CredentialSource != nil && *a.CredentialSource == "issued" }

// SourceText is the source in one phrase: "home", "issued (set)", "issued (NOT set)" or "issued";
// "" when the Host did not say.
func (a Agent) SourceText() string {
	switch {
	case a.CredentialSource == nil || *a.CredentialSource == "":
		return ""
	case !a.Issued():
		return *a.CredentialSource
	case a.IssuedSet == nil:
		return "issued"
	case *a.IssuedSet:
		return "issued (set)"
	default:
		return "issued (NOT set)"
	}
}

// Launch is one CLI's launch check. Result is the Host's word: "ok", "failed" or "not checked".
type Launch struct {
	Result     string  `json:"result"`
	ExitCode   *int    `json:"exitCode"`
	StderrTail *string `json:"stderrTail"`
	Detail     *string `json:"detail"`
}

// LaunchText is the launch result in one phrase: "ok", "FAILED, exit 137", "not checked" or
// "not known". Only the Host's "ok" reads ok.
func (a Agent) LaunchText() string {
	l := a.Launch
	switch {
	case l == nil:
		return "not known"
	case l.Result == "ok":
		return "ok"
	case l.Result == "failed" && l.ExitCode != nil:
		return fmt.Sprintf("FAILED, exit %d", *l.ExitCode)
	case l.Result == "failed":
		return "FAILED"
	case l.Result == "not checked":
		return "not checked"
	case l.Result == "updating":
		return "updating"
	default:
		return fmt.Sprintf("not known (the Host said %q)", l.Result)
	}
}

// UpdatedText says which version is installed and when it last changed, in one phrase, or "" when
// the report carried no version.
func (a Agent) UpdatedText() string {
	if a.Version == nil || *a.Version == "" {
		return ""
	}
	day := func(stamp string) string {
		if t, err := time.Parse(time.RFC3339, stamp); err == nil {
			return t.UTC().Format("2006-01-02 15:04 UTC")
		}
		return stamp
	}
	switch {
	case a.UpdatedAt != nil && *a.UpdatedAt != "":
		return *a.Version + ", updated " + day(*a.UpdatedAt)
	case a.VersionsSince != nil && *a.VersionsSince != "":
		return *a.Version + ", unchanged since " + day(*a.VersionsSince)
	default:
		return *a.Version
	}
}

// IsModelAgent is an entry that signs in to a model provider: no kind or kind "agent", and not
// marked as running no model.
func (a Agent) IsModelAgent() bool {
	return (a.Kind == "" || a.Kind == "agent") && (a.LanguageModel == nil || *a.LanguageModel)
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
		// `yawble agents` lists agents only, and the doctor's agents row counts only them: a plugin
		// member or a non-model preset is never "NOT signed in".
		agents := r.Agents[:0]
		for _, a := range r.Agents {
			if a.IsModelAgent() {
				agents = append(agents, a)
			}
		}
		r.Agents = agents
		return r, nil
	}
	return r, ErrNoReport
}
