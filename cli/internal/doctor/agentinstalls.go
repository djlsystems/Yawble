package doctor

import (
	"context"
	"errors"
	"fmt"
	"io"
	"strings"
	"time"
)

// AgentInstalls is the report's `agentInstalls` section: what the Host's background install of the
// agent CLIs and Google Chrome is doing (AgentInstallsAtStart), as it last recorded it. Nil: an older
// Host, a Host with no installer (run outside the image), or nothing recorded yet.
type AgentInstalls struct {
	RecordedAt   string             `json:"recordedAt"`
	Worker       *string            `json:"worker"`
	Items        []AgentInstallItem `json:"items"`
	GoogleChrome *GoogleChrome      `json:"googleChrome"`
}

// AgentInstallItem is one CLI. State is checking, installing, updating, ready, failed or missing.
type AgentInstallItem struct {
	Agent   string  `json:"agent"`
	Command string  `json:"command"`
	State   string  `json:"state"`
	Detail  *string `json:"detail"`
	Worker  *string `json:"worker"`
	Since   *string `json:"since"`
}

// GoogleChrome is the sign-in page's browser, as its installer's status says: installing (with a
// percent of the download), ready or failed.
type GoogleChrome struct {
	State   string  `json:"state"`
	Percent *int    `json:"percent"`
	Detail  *string `json:"detail"`
}

// Item finds a command's item; nil when the record has none.
func (a *AgentInstalls) Item(command string) *AgentInstallItem {
	if a == nil {
		return nil
	}
	for i := range a.Items {
		if strings.EqualFold(a.Items[i].Command, command) || strings.EqualFold(a.Items[i].Agent, command) {
			return &a.Items[i]
		}
	}
	return nil
}

// InProgress says whether anything is still checking, installing or updating.
func (a *AgentInstalls) InProgress() bool {
	if a == nil {
		return false
	}
	for _, item := range a.Items {
		if item.Busy() {
			return true
		}
	}
	return a.GoogleChrome != nil && a.GoogleChrome.State == "installing"
}

// Busy is an item still being checked, installed or updated: launches of it wait.
func (i AgentInstallItem) Busy() bool {
	return i.State == "checking" || i.State == "installing" || i.State == "updating"
}

// Word is the item in one or two words, as the up line and status print it: done, installing,
// updating, checking, failed.
func (i AgentInstallItem) Word() string {
	switch i.State {
	case "ready":
		return "done"
	case "missing":
		return "not installed"
	}
	return i.State
}

// Word is Chrome in one word, or its percent while it downloads.
func (c *GoogleChrome) Word() string {
	switch {
	case c == nil:
		return "not known"
	case c.State == "installing" && c.Percent != nil:
		return fmt.Sprintf("%d%%", *c.Percent)
	case c.State == "ready":
		return "done"
	}
	return c.State
}

// ProgressText is "claude installing, codex done, google-chrome 40%".
func (a *AgentInstalls) ProgressText() string {
	parts := make([]string, 0, len(a.Items)+1)
	for _, item := range a.Items {
		parts = append(parts, item.Command+" "+item.Word())
	}
	if a.GoogleChrome != nil {
		parts = append(parts, "google-chrome "+a.GoogleChrome.Word())
	}
	return strings.Join(parts, ", ")
}

// ProgressLine is the line `yawble up` prints while it waits:
// "worker worker-1: getting agents ready (claude installing, codex done, google-chrome 40%)".
func (a *AgentInstalls) ProgressLine() string {
	who := "this instance"
	if a.Worker != nil && *a.Worker != "" {
		who = "worker " + *a.Worker
	}
	return fmt.Sprintf("%s: getting agents ready (%s)", who, a.ProgressText())
}

// Failed names the items whose install failed.
func (a *AgentInstalls) Failed() []string {
	var out []string
	for _, item := range a.Items {
		if item.State == "failed" {
			out = append(out, item.Command)
		}
	}
	if a.GoogleChrome != nil && a.GoogleChrome.State == "failed" {
		out = append(out, "google-chrome")
	}
	return out
}

// StillInstallingLine is what `yawble up` ends with when agents are still installing at its bound.
func StillInstallingLine(url string) string {
	return fmt.Sprintf("Yawble is up at %s; agents are still being installed - `yawble status` shows when they are ready", url)
}

// AgentsWaitBound is how long `yawble up` waits for the agents after control answers and the workers
// connect; AgentsWaitInterval is how often it reads the report meanwhile.
var (
	AgentsWaitBound    = 45 * time.Second
	AgentsWaitInterval = 2 * time.Second
)

// WaitForAgents prints the agents' progress while they are checked, installed or updated, for at most
// AgentsWaitBound, and returns nil whatever it finds: agents still installing never fail `up`, and a
// report that cannot be read, or carries no such section (an older Host, or one run outside the image,
// which installs nothing), ends the wait. The Host records the section before it answers, so a Host
// that has it has it here. A worker that fails to connect is waited for, and is an error, before this.
func WaitForAgents(ctx context.Context, fetch func(context.Context) (*HostReport, error), url string, out io.Writer) error {
	started := time.Now()
	said := ""
	for {
		r, err := fetch(ctx)
		if errors.Is(ctx.Err(), context.Canceled) {
			return ctx.Err()
		}
		if err != nil || r == nil || r.AgentInstalls == nil {
			return nil
		}
		installs := r.AgentInstalls
		switch {
		case !installs.InProgress():
			if failed := installs.Failed(); len(failed) > 0 {
				fmt.Fprintf(out, "agents: %s could not be installed; the others are ready. `yawble status` says why, and `yawble up` tries again\n", strings.Join(failed, ", "))
			} else if said != "" {
				fmt.Fprintf(out, "agents are ready (%s)\n", installs.ProgressText())
			}
			return nil
		default:
			if line := installs.ProgressLine(); line != said {
				fmt.Fprintln(out, line)
				said = line
			}
		}
		if time.Since(started) >= AgentsWaitBound {
			fmt.Fprintln(out, StillInstallingLine(url))
			return nil
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(AgentsWaitInterval):
		}
	}
}
