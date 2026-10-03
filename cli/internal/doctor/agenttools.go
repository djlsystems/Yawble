package doctor

import (
	"fmt"
	"io"
	"strings"
	"time"
)

// THE AGENT TOOLS SECTION: per preset, whether a member gets only the platform's tools. The Host
// lists what each CLI would load (its pre-flight, at start and after every catalog save) and
// records it; its --doctor report carries that record as `agentTools`, and everything below is
// read from it. Nothing here runs a CLI.

// AgentTools mirrors the Host's AgentToolsRecord.
type AgentTools struct {
	At      string             `json:"at"`
	Presets []PresetToolReport `json:"presets"`
}

// PresetToolReport is one preset's pre-flight. Verdict is the Host's word: isolated, foreignFound,
// notVerified, notMeasured, concierge or notAModel.
type PresetToolReport struct {
	Preset      string       `json:"preset"`
	Mode        string       `json:"mode"`
	Command     string       `json:"command"`
	Verdict     string       `json:"verdict"`
	Foreign     []ListedItem `json:"foreign"`
	Loaded      []ListedItem `json:"loaded"`
	SwitchedOff []ListedItem `json:"switchedOff"`
	Gaps        []string     `json:"gaps"`
	Ran         []string     `json:"ran"`
	Detail      *string      `json:"detail"`
}

// ListedItem is one thing a CLI's listing named: a server, connector, plugin, skill or hook.
type ListedItem struct {
	Kind   string  `json:"kind"`
	Name   string  `json:"name"`
	Source *string `json:"source"`
	Off    *string `json:"off"`
}

// updating is whether a preset was not listed because the platform's update held its CLI: the Host's
// sentence for that state begins "Updating".
func updating(p PresetToolReport) bool {
	return p.Detail != nil && strings.HasPrefix(*p.Detail, "Updating ")
}

// offersTools is the Host's ListedKinds.OffersTools: skills and hooks are listed, never tools.
func offersTools(kind string) bool {
	return kind == "server" || kind == "connector" || kind == "plugin"
}

// AgentToolsCheck is the one table row: ok when every member preset is isolated, warn when one
// would be offered a foreign tool or carries no declaration. A preset that could not be listed is
// named as not measured and does not turn the row green or amber. The Concierge never warns.
func AgentToolsCheck(r *HostReport, err error) Check {
	const name = "agent tools"
	switch {
	case err != nil || r == nil:
		return Check{name, Skip, "no report", ""}
	case r.AgentTools == nil:
		return Check{name, Skip, "the Host has not recorded a pre-flight yet (it lists the agent CLIs after it starts)", ""}
	}

	var parts []string
	verdict := OK
	for _, p := range r.AgentTools.Presets {
		switch p.Verdict {
		case "isolated":
			parts = append(parts, p.Preset+" isolated")
		case "foreignFound":
			parts = append(parts, p.Preset+" FOREIGN TOOLS: "+names(p.Foreign))
			verdict = Warn
		case "notVerified":
			parts = append(parts, p.Preset+" not verified")
			verdict = Warn
		case "notMeasured":
			if updating(p) {
				parts = append(parts, p.Preset+" updating")
			} else {
				parts = append(parts, p.Preset+" not measured")
			}
		}
	}
	if len(parts) == 0 {
		return Check{name, Skip, "no member preset runs a language model", ""}
	}
	fix := ""
	if verdict == Warn {
		fix = "Admin → Agents names each one: declare the preset's isolation, switch the tool off in its launch, or move it into a Connection"
	}
	return Check{name, verdict, strings.Join(parts, " · "), fix}
}

// RenderAgentTools prints one block per preset under the table: its verdict, what its CLI would
// load, what the launch switched off, and its recorded gaps. The Concierge's tools are listed as
// what it has, not as a warning.
func RenderAgentTools(w io.Writer, tools *AgentTools) {
	if tools == nil {
		return
	}
	at := tools.At
	if t, err := time.Parse(time.RFC3339, tools.At); err == nil {
		at = t.UTC().Format("2006-01-02 15:04 UTC")
	}
	fmt.Fprintf(w, "\nagent tools, as the Host listed them at %s\n", at)
	for _, p := range tools.Presets {
		if p.Verdict == "notAModel" {
			continue
		}
		fmt.Fprintf(w, "\n%s (%s, %s)\n", p.Preset, p.Mode, p.Command)
		switch p.Verdict {
		case "isolated":
			fmt.Fprintln(w, "  state       isolated: harness and its own tools only")
		case "foreignFound":
			fmt.Fprintf(w, "  state       FOREIGN TOOLS FOUND: %s\n", names(p.Foreign))
		case "notVerified":
			fmt.Fprintln(w, "  state       NOT VERIFIED: the preset declares no isolation")
			if len(p.Foreign) > 0 {
				fmt.Fprintf(w, "  would get   %s\n", names(p.Foreign))
			}
		case "notMeasured":
			fmt.Fprintln(w, "  state       not measured")
		case "concierge":
			fmt.Fprintln(w, "  state       Concierge: keeps every tool the person set up (information)")
		}
		if p.Detail != nil && *p.Detail != "" {
			fmt.Fprintf(w, "  detail      %s\n", *p.Detail)
		}
		if p.Verdict == "concierge" {
			if tools := toolItems(p.Loaded); len(tools) > 0 {
				fmt.Fprintf(w, "  has         %s\n", names(tools))
			} else {
				fmt.Fprintln(w, "  has         no servers, connectors or plugins")
			}
		} else if loaded := toolItems(p.Loaded); len(loaded) > 0 {
			fmt.Fprintf(w, "  loads       %s\n", names(loaded))
		}
		if off := toolItems(p.SwitchedOff); len(off) > 0 {
			var parts []string
			for _, i := range off {
				parts = append(parts, i.Name+" ("+deref(i.Off)+")")
			}
			fmt.Fprintf(w, "  off         %s\n", strings.Join(parts, ", "))
		}
		if n := count(p.Loaded, "skill"); n > 0 {
			fmt.Fprintf(w, "  skills      %d (instructions, not tools)\n", n)
		}
		for _, g := range p.Gaps {
			fmt.Fprintf(w, "  gap         %s\n", g)
		}
	}
}

func toolItems(items []ListedItem) []ListedItem {
	var out []ListedItem
	for _, i := range items {
		if offersTools(i.Kind) {
			out = append(out, i)
		}
	}
	return out
}

func names(items []ListedItem) string {
	var out []string
	for _, i := range items {
		out = append(out, i.Name)
	}
	return strings.Join(out, ", ")
}

func count(items []ListedItem, kind string) int {
	n := 0
	for _, i := range items {
		if i.Kind == kind {
			n++
		}
	}
	return n
}

func deref(s *string) string {
	if s == nil {
		return ""
	}
	return *s
}
