package doctor

import (
	"fmt"
	"strings"
	"time"
)

// ConciergeRecord is the report's `concierge` section: the Host's own record of each running
// Concierge session - whose, on which worker, what it last did, and when the idle window would end
// it. The Host writes it when a session starts or ends and at every sweep; nothing here derives a
// time of its own.
type ConciergeRecord struct {
	RecordedAt string             `json:"recordedAt"`
	Window     string             `json:"window"`
	Sessions   []ConciergeSession `json:"sessions"`
}

// ConciergeSession is one session. LastViewerAt and WouldEndAt are nil while a browser has it open;
// ResidentBytes is nil when its worker has not measured it.
type ConciergeSession struct {
	User           string  `json:"user"`
	Email          *string `json:"email"`
	Worker         *string `json:"worker"`
	StartedAt      string  `json:"startedAt"`
	Viewer         bool    `json:"viewer"`
	LastViewerAt   *string `json:"lastViewerAt"`
	LastActivityAt string  `json:"lastActivityAt"`
	LastActivity   string  `json:"lastActivity"`
	WouldEndAt     *string `json:"wouldEndAt"`
	ResidentBytes  *int64  `json:"residentBytes"`
}

// Who names the session's person: the email when the Host had one, else the user id.
func (s ConciergeSession) Who() string {
	if s.Email != nil && *s.Email != "" {
		return *s.Email
	}
	return s.User
}

// Text is one session as the doctor says it.
func (s ConciergeSession) Text() string {
	worker := "no worker"
	if s.Worker != nil && *s.Worker != "" {
		worker = "on " + *s.Worker
	}
	parts := []string{worker, "last activity " + stamp(s.LastActivityAt) + " (" + s.LastActivity + ")"}
	switch {
	case s.Viewer:
		parts = append(parts, "open in a browser, so not ended")
	case s.WouldEndAt != nil:
		parts = append(parts, "would end at "+stamp(*s.WouldEndAt))
	}
	if s.ResidentBytes != nil {
		parts = append(parts, fmt.Sprintf("%d MB", *s.ResidentBytes>>20))
	} else {
		parts = append(parts, "memory not measured")
	}
	return strings.Join(parts, ", ")
}

// ConciergeRows are the doctor's information rows for the running Concierge sessions: none when the
// report carries no record (an older Host, or none written yet), one saying so when none runs, and
// one per session otherwise. Information, never a verdict.
func ConciergeRows(r *HostReport) []Check {
	if r == nil || r.Concierge == nil {
		return nil
	}
	if len(r.Concierge.Sessions) == 0 {
		return []Check{{"concierge", Info, "no Concierge session is running (idle window " + r.Concierge.Window + ")", ""}}
	}
	var rows []Check
	for _, s := range r.Concierge.Sessions {
		rows = append(rows, Check{"concierge", Info, s.Who() + ": " + s.Text(), ""})
	}
	return rows
}

// stamp is a recorded time as the doctor writes times, or the text as it came when it is not one.
func stamp(at string) string {
	t, err := time.Parse(time.RFC3339, at)
	if err != nil {
		return at
	}
	return t.UTC().Format("2006-01-02 15:04 UTC")
}
