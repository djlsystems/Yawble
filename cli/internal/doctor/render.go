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

// RenderAgents prints one block per agent from the Host's report, with the sign-in hint.
func RenderAgents(w io.Writer, agents []Agent) {
	for i, a := range agents {
		if i > 0 {
			fmt.Fprintln(w)
		}
		fmt.Fprintln(w, a.Agent)
		switch {
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
			fmt.Fprintf(w, "  to fix      %s\n", hint)
		}
	}
}
