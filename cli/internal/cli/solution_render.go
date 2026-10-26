package cli

import (
	"encoding/json"
	"fmt"
	"github.com/djlsystems/yawble/cli/internal/text"
	"io"
	"strings"
	"time"
)

// secretRow is one secret a package's plugin member binds, by KEY NAME - never a value: whether
// the Host has it set, whether it is needed (nil in a preview, where it waits on the person's
// answer to When's setting), and the exact way to set it.
type secretRow struct {
	Member      string `json:"member"`
	Field       string `json:"field"`
	Key         string `json:"key"`
	Description string `json:"description"`
	Required    bool   `json:"required"`
	When        *struct {
		Setting string `json:"setting"`
		Value   string `json:"value"`
	} `json:"when"`
	Set     bool   `json:"set"`
	Needed  *bool  `json:"needed"`
	SetWith string `json:"setWith"`
}

// needed answers from the Host's word, else from the value chosen for the setting it depends on.
func (r secretRow) needed(value any) bool {
	if r.Needed != nil {
		return *r.Needed
	}
	if r.When == nil {
		return true
	}
	switch v := value.(type) {
	case []any:
		for _, item := range v {
			if item == r.When.Value {
				return true
			}
		}
	case []string:
		for _, item := range v {
			if item == r.When.Value {
				return true
			}
		}
	case string:
		return v == r.When.Value
	}
	return false
}

// secretSetWith is the exact way to set key: the screen first - its field in the member's settings,
// read by the member's next run - and this CLI second, which prompts for the value for the Host to
// read when it restarts.
func secretSetWith(key string) string {
	return "its field in the member's settings on the screen, read by the next run; or yawble secret set " + key +
		" (it prompts for the value), then yawble up to restart the Host"
}

// renderSecrets prints each secret by key name: set, not set (its source fails until it is, and
// how to set it), or not needed for a setting left off. valueOf answers the chosen setting.
func renderSecrets(out io.Writer, rows []secretRow, valueOf func(member, setting string) any) {
	if len(rows) == 0 {
		return
	}
	fmt.Fprintln(out, "\nSecrets (bound by key name; values are set on the Host, never here):")
	for _, r := range rows {
		var value any
		if r.When != nil && valueOf != nil {
			value = valueOf(r.Member, r.When.Setting)
		}
		switch {
		case !r.needed(value):
			fmt.Fprintf(out, "  %s (%s): not needed - %s's %s leaves %s off.\n", r.Key, r.Member, r.Member, r.When.Setting, r.When.Value)
		case r.Set:
			fmt.Fprintf(out, "  %s (%s): set on this Host.\n", r.Key, r.Member)
		default:
			fmt.Fprintf(out, "  %s (%s): not set - its source fails until it is set. Set it with: %s.\n", r.Key, r.Member, secretSetWith(r.Key))
		}
		if r.Description != "" {
			fmt.Fprintf(out, "      %s\n", r.Description)
		}
	}
}

// keptPart is what an update keeps of the person's part: a kept member's person-only settings
// and bindings (never changed by an update) and the files already in each document folder.
type keptPart struct {
	Settings []struct {
		Member  string          `json:"member"`
		Setting string          `json:"setting"`
		Value   json.RawMessage `json:"value"`
	} `json:"settings"`
	Connections []struct {
		Member     string  `json:"member"`
		Slot       string  `json:"slot"`
		Connection *string `json:"connection"`
	} `json:"connections"`
	Documents []struct {
		Folder string   `json:"folder"`
		Files  []string `json:"files"`
	} `json:"documents"`
}

// setting is a kept member's current value, and whether the update keeps it.
func (k *keptPart) setting(member, setting string) (json.RawMessage, bool) {
	if k != nil {
		for _, s := range k.Settings {
			if s.Member == member && s.Setting == setting {
				return s.Value, true
			}
		}
	}
	return nil, false
}

// connection is a kept member's bound connection id ("" when unbound), and whether it is kept.
func (k *keptPart) connection(member, slot string) (string, bool) {
	if k != nil {
		for _, c := range k.Connections {
			if c.Member == member && c.Slot == slot {
				return text.OrEmpty(c.Connection), true
			}
		}
	}
	return "", false
}

// files are the files already in a document folder.
func (k *keptPart) files(folder string) []string {
	if k != nil {
		for _, d := range k.Documents {
			if d.Folder == folder {
				return d.Files
			}
		}
	}
	return nil
}

// keptWords is a kept value in words: a list joined, nothing as "not set".
func keptWords(raw json.RawMessage) string {
	var list []any
	switch {
	case len(raw) == 0 || string(raw) == "null" || string(raw) == `""`:
		return "not set"
	case json.Unmarshal(raw, &list) == nil:
		if len(list) == 0 {
			return "not set"
		}
		items := make([]string, len(list))
		for i, v := range list {
			items[i] = fmt.Sprint(v)
		}
		return strings.Join(items, ", ")
	}
	var text string
	if json.Unmarshal(raw, &text) == nil {
		return text
	}
	return compact(raw)
}

type diffSection struct {
	Added   []string `json:"added"`
	Changed []string `json:"changed"`
	Removed []string `json:"removed"`
}

type solutionDiff struct {
	Members  diffSection `json:"members"`
	Triggers diffSection `json:"triggers"`
	Skills   diffSection `json:"skills"`
	Sites    diffSection `json:"sites"`
	Tools    diffSection `json:"tools"`
	Plugins  diffSection `json:"plugins"`
	// Moves is each kept plugin member the update moves onto the plugin the package now names.
	Moves []diffLine `json:"moves"`
	// NoLongerFire is each trigger a person made by hand on a moved-from plugin's events.
	NoLongerFire []diffLine `json:"noLongerFire"`
}

// diffLine is one of the diff's sentences, worded by the Host.
type diffLine struct {
	Line string `json:"line"`
}

type connectionRow struct {
	ID       string `json:"id"`
	Name     string `json:"name"`
	Provider string `json:"provider"`
	Account  string `json:"account"`
	Status   string `json:"status"`
}

// firstRunRow is one schedule's first run in the install's answer: ran now, or when it first runs.
type firstRunRow struct {
	Trigger      string  `json:"trigger"`
	Member       string  `json:"member"`
	RunAtInstall bool    `json:"runAtInstall"`
	RanNow       bool    `json:"ranNow"`
	Outcome      string  `json:"outcome"`
	At           *string `json:"at"`
}

// renderFirstRuns names each schedule's first run: "Fetch jobs ran now", "Fetch jobs first runs at
// 8:51 PM", or for a first run at install that did not happen, why and when it first runs instead.
func renderFirstRuns(out io.Writer, runs []firstRunRow, now time.Time) {
	if len(runs) == 0 {
		return
	}
	fmt.Fprintln(out, "\nSchedules:")
	for _, r := range runs {
		if r.RanNow {
			fmt.Fprintf(out, "  %s ran now.\n", r.Trigger)
			continue
		}
		at := " runs on its schedule"
		if r.At != nil {
			if when, err := time.Parse(time.RFC3339Nano, *r.At); err == nil {
				at = " first runs at " + firstRunTime(when, now)
			}
		}
		switch {
		case !r.RunAtInstall || r.Outcome == "scheduled":
			fmt.Fprintf(out, "  %s%s.\n", r.Trigger, at)
		case r.Outcome == "failed":
			fmt.Fprintf(out, "  %s could not run now; it%s.\n", r.Trigger, at)
		default:
			fmt.Fprintf(out, "  %s did not run now (%s); it%s.\n", r.Trigger, r.Outcome, at)
		}
	}
}

// firstRunTime is a first run's local time: "8:51 PM" today, "Thu 8:00 AM" on another day.
func firstRunTime(when, now time.Time) string {
	when, now = when.Local(), now.Local()
	if when.Year() == now.Year() && when.YearDay() == now.YearDay() {
		return when.Format("3:04 PM")
	}
	return when.Format("Mon 3:04 PM")
}

func renderDiff(out io.Writer, p previewBody) {
	fmt.Fprintf(out, "\nUpdating team %s from %s %s to %s:\n", p.TeamName, p.Plan.Package.ID, p.From, p.To)
	if p.Diff == nil {
		fmt.Fprintln(out, "  no changes listed")
		return
	}
	listed := false
	for _, s := range []struct {
		name string
		d    diffSection
	}{{"Members", p.Diff.Members}, {"Plugins", p.Diff.Plugins}, {"Triggers", p.Diff.Triggers}, {"Skills", p.Diff.Skills}, {"Sites", p.Diff.Sites}, {"Tools", p.Diff.Tools}} {
		for _, row := range []struct {
			verb  string
			names []string
		}{{"added", s.d.Added}, {"changed", s.d.Changed}, {"removed", s.d.Removed}} {
			if len(row.names) > 0 {
				fmt.Fprintf(out, "  %s %s: %s\n", s.name, row.verb, strings.Join(row.names, ", "))
				listed = true
			}
		}
	}
	for _, l := range append(append([]diffLine{}, p.Diff.Moves...), p.Diff.NoLongerFire...) {
		fmt.Fprintf(out, "  %s.\n", l.Line)
		listed = true
	}
	if !listed {
		fmt.Fprintln(out, "  nothing is added, changed or removed")
	}
}

// result prints each step the Host made, or the one that failed, then what is still missing.
func (in *solutionInstall) result(p previewBody, status int, raw json.RawMessage) error {
	var b installBody
	if err := json.Unmarshal(raw, &b); err != nil {
		return fmt.Errorf("the Host answered something this CLI cannot read: %q", string(raw))
	}
	if status != 200 {
		return fmt.Errorf("%s was not installed: %s", in.folder, b.Error)
	}
	if !b.OK && len(b.Refusals) > 0 {
		renderRefusals(in.out, in.folder, b.Refusals)
		return fmt.Errorf("%s does not pass its check: %s", in.folder, text.Plural(len(b.Refusals), "problem"))
	}
	for _, s := range b.Steps {
		if s.Done {
			fmt.Fprintf(in.out, "  %d. %s: done\n", s.Number, s.Title)
		}
	}
	if !b.OK {
		if b.StepNumber == 0 {
			return fmt.Errorf("%s was not installed: %s", in.folder, b.Error)
		}
		title := solutionStepTitles[b.StepNumber]
		for _, s := range b.Steps {
			if s.Number == b.StepNumber && s.Title != "" {
				title = s.Title
			}
		}
		return fmt.Errorf("the install failed at step %d (%s): %s. Nothing was left behind.", b.StepNumber, title, strings.TrimSuffix(b.Reason, "."))
	}
	if p.Mode == "update" {
		fmt.Fprintf(in.out, "\nUpdated team %s from %s %s to %s.\n", b.TeamName, p.Plan.Package.Name, p.From, p.To)
	} else {
		fmt.Fprintf(in.out, "\nInstalled %s %s as team %s.\n", p.Plan.Package.Name, b.Version, b.TeamName)
	}
	if len(b.Missing) == 0 {
		fmt.Fprintln(in.out, "Nothing is missing: the team is ready.")
	} else {
		fmt.Fprintf(in.out, "The team shows as blocked until %s provided:\n", map[bool]string{true: "this is", false: "these are"}[len(b.Missing) == 1])
		for _, m := range b.Missing {
			name := m.Name
			if m.Member != nil && *m.Member != "" {
				name = *m.Member + "'s " + name
			}
			fmt.Fprintf(in.out, "  Blocked: waiting for %s - %s\n", name, m.Description)
		}
	}
	renderFirstRuns(in.out, b.FirstRuns, time.Now())
	renderSecrets(in.out, b.Secrets, nil)
	if len(b.Unset) > 0 {
		fmt.Fprintln(in.out, "\nThese keys are still not set on the Host. Each one's source fails until it is set:")
		for _, key := range b.Unset {
			fmt.Fprintf(in.out, "  %s - %s\n", key, secretSetWith(key))
		}
	}
	return nil
}
