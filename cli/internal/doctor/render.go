package doctor

import (
	"fmt"
	"io"
	"strings"
)

// Render prints one line per check, verdict first in a fixed column, and the fix on its own line
// underneath when there is one. No colour: the verdict words carry the meaning on every terminal.
func Render(w io.Writer, checks []Check) {
	for _, c := range checks {
		fmt.Fprintf(w, "%-5s %-14s %s\n", c.Verdict, c.Name, c.Detail)
		if c.Fix != "" {
			fmt.Fprintf(w, "%-5s %-14s fix: %s\n", "", "", c.Fix)
		}
	}
}

// keyRows are the rows the short list always shows, whatever their verdict: whether the
// instance runs, whether an agent can work in it, and whether it is backed up.
var keyRows = map[string]bool{"machine": true, "engine": true, "container": true, "health": true, "agents": true, "backups": true}

// Short is the default list: the key rows and every row that warns or fails, so each line is
// one a person can act on or wants to know. The rest - information, skips, rows that are ok -
// is counted for the line that points at --details.
func Short(checks []Check) ([]Check, int) {
	var shown []Check
	for _, c := range checks {
		if keyRows[c.Name] || c.Verdict == Warn || c.Verdict == Fail {
			shown = append(shown, c)
		}
	}
	return shown, len(checks) - len(shown)
}

// RenderAgents prints, first, whether any agent can run, then one block per agent from the
// Host's report with what to do. With one agent able to run, the others are not in use: their
// sign-in is "no", not a fault, and their hint is how to use them, not a fix.
func RenderAgents(w io.Writer, agents []Agent) {
	anyRuns := AnyCanRun(agents)
	if line := agentsSummary(agents, anyRuns); line != "" {
		fmt.Fprintln(w, line)
		fmt.Fprintln(w)
	}
	for i, a := range agents {
		if i > 0 {
			fmt.Fprintln(w)
		}
		notInUse := anyRuns && a.NotInUse()
		fmt.Fprintln(w, a.Agent)
		if notInUse {
			fmt.Fprintln(w, "  in use      no")
		}
		switch {
		case a.Updating != nil:
			fmt.Fprintln(w, "  installed   updating")
		case a.Installed == nil:
			fmt.Fprintln(w, "  installed   not measured")
		case !*a.Installed:
			fmt.Fprintln(w, "  installed   no")
		case a.Version != nil && *a.Version != "":
			fmt.Fprintf(w, "  installed   yes, %s\n", a.UpdatedText())
		default:
			fmt.Fprintln(w, "  installed   yes")
		}
		switch {
		case a.NotInstalled():
		case a.Authenticated == nil:
			fmt.Fprintln(w, "  signed in   not measured")
		case *a.Authenticated:
			fmt.Fprintln(w, "  signed in   yes")
		case notInUse:
			fmt.Fprintln(w, "  signed in   no")
		default:
			fmt.Fprintln(w, "  signed in   NO")
		}
		if source := a.SourceText(); source != "" {
			fmt.Fprintf(w, "  source      %s\n", source)
		}
		if measured := a.MeasuredText(); measured != "" {
			fmt.Fprintf(w, "  measured    %s\n", measured)
		}
		if a.IsInstalled() {
			fmt.Fprintf(w, "  launch      %s\n", a.LaunchText())
			if l := a.Launch; l != nil {
				if l.Detail != nil && *l.Detail != "" {
					fmt.Fprintf(w, "  launch why  %s\n", *l.Detail)
				}
				if l.StderrTail != nil && *l.StderrTail != "" {
					for _, line := range strings.Split(strings.TrimRight(*l.StderrTail, "\n"), "\n") {
						fmt.Fprintf(w, "  stderr      %s\n", line)
					}
				}
			}
		}
		if a.Detail != "" {
			fmt.Fprintf(w, "  detail      %s\n", a.Detail)
		}
		if hint := SignInHint(a); hint != "" {
			label := "to fix   "
			if notInUse {
				label = "to use it"
			}
			fmt.Fprintf(w, "  %s   %s\n", label, hint)
		}
	}
}

// agentsSummary is the one line above the blocks: which agents can run and that the rest are not
// in use, or that none can run yet. "" when nothing was measured, which is not a fault either.
func agentsSummary(agents []Agent, anyRuns bool) string {
	var running, idle []string
	measured := false
	for _, a := range agents {
		switch {
		case a.CanRun():
			running = append(running, a.Agent)
		case anyRuns && a.NotInUse():
			idle = append(idle, a.Agent)
		}
		if a.Installed != nil && a.Updating == nil {
			measured = true
		}
	}
	switch {
	case anyRuns && len(idle) == 0:
		return andList(running) + " can run agent work."
	case anyRuns:
		verb := " are"
		if len(idle) == 1 {
			verb = " is"
		}
		return andList(running) + " can run agent work; " + andList(idle) + verb + " not in use (sign one in only if you want it)."
	case measured:
		return "No agent can run yet: sign one in as its \"to fix\" line says."
	}
	return ""
}

// andList joins names the way a sentence does: "a", "a and b", "a, b and c".
func andList(names []string) string {
	if len(names) < 2 {
		return strings.Join(names, "")
	}
	return strings.Join(names[:len(names)-1], ", ") + " and " + names[len(names)-1]
}
