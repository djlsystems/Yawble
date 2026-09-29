package cli

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"path"
	"path/filepath"
	"sort"
	"strings"

	"github.com/spf13/cobra"

	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/instance"
)

// solutionStageRoot is where a package from this computer is copied inside the container for the
// Host to check, one folder per check, removed afterwards. /tmp: it is not part of the volume.
const solutionStageRoot = "/tmp/yawble-solution-check"

// hostSolutionCheck is the Host's own operator switch, run inside the container like --doctor:
// the validator POST /api/solutions/check runs, printing that route's body as one JSON line.
var hostSolutionCheck = []string{"dotnet", "/app/Harness.Host.dll", "--solution-check"}

const (
	// $1 the stage folder: emptied and made, for `cp` to create the package inside it.
	solutionStageScript = `set -e
rm -rf "$1"
mkdir -p "$1"`
)

func newSolutionCommand(deps Deps) *cobra.Command {
	cmd := &cobra.Command{
		Use:   "solution",
		Short: "Check solution packages: a whole working team in one folder",
		Long: "A solution package is a folder holding solution.json and the plugins, skills, sites and tools a team " +
			"needs (docs/solutions.md). These commands ask the running instance about one.",
	}
	cmd.AddCommand(newSolutionCheckCommand(deps))
	return cmd
}

func newSolutionCheckCommand(deps Deps) *cobra.Command {
	var fromInstance, asJSON bool
	cmd := &cobra.Command{
		Use:   "check <folder>",
		Short: "Say what installing a solution package would create, or everything wrong with it",
		Long: "<folder> is a solution package: the folder holding solution.json. It is copied into the running instance " +
			"and the Host checks it with the rules POST /api/solutions/check applies: every member, trigger, skill, site, " +
			"tool and input, against this instance's Agent presets, events and runtimes. Nothing is installed and nothing " +
			"is written; the copy is removed afterwards.\n\n" +
			"A package that passes is printed as its install would create it, every trigger with its full instruction text " +
			"and daily cap, so it can be read before anything runs. One that does not is printed as its problems, each " +
			"naming the file and the field, and the command exits 1.\n\n" +
			"With --from-instance, <folder> is an absolute path inside the instance (a package a team wrote to its " +
			"documents) and nothing is copied.",
		Example: "  yawble solution check samples/solutions/job-tracker\n" +
			"  yawble solution check --from-instance /data/documents/acme/job-tracker-1.0.0\n" +
			"  yawble solution check ./job-tracker --json",
		Args: cobra.ExactArgs(1),
		RunE: func(cmd *cobra.Command, args []string) error {
			folder := args[0]
			if !fromInstance {
				info, err := os.Stat(folder)
				if err != nil || !info.IsDir() {
					return fmt.Errorf("%s is not a folder on this computer; give a solution package's folder, or --from-instance for a path inside the instance", folder)
				}
				if _, err := os.Stat(filepath.Join(folder, "solution.json")); err != nil {
					return fmt.Errorf("%s holds no solution.json; a solution package has one at its root", folder)
				}
			} else if !strings.HasPrefix(folder, "/") {
				return fmt.Errorf("with --from-instance, give the absolute path inside the instance (it starts with /)")
			}

			ctx := cmd.Context()
			e, err := runningEngine(ctx, deps, "solution check")
			if err != nil {
				return err
			}

			inside := folder
			if !fromInstance {
				stage := path.Join(solutionStageRoot, newNonce())
				defer func() { _, _ = e.Exec(context.WithoutCancel(ctx), instance.ContainerName, "rm", "-rf", stage) }()
				if _, err := e.Exec(ctx, instance.ContainerName, "sh", "-c", solutionStageScript, "sh", stage); err != nil {
					return err
				}
				inside = path.Join(stage, "package")
				if err := e.CopyTo(ctx, instance.ContainerName, folder, inside); err != nil {
					return err
				}
			}

			body, raw, err := hostCheck(ctx, e, inside)
			if err != nil {
				return err
			}
			out := cmd.OutOrStdout()
			if asJSON {
				var pretty any
				_ = json.Unmarshal([]byte(raw), &pretty)
				enc := json.NewEncoder(out)
				enc.SetIndent("", "  ")
				if err := enc.Encode(pretty); err != nil {
					return err
				}
			}
			if !body.OK {
				if !asJSON {
					renderRefusals(out, folder, body.Refusals)
				}
				return fmt.Errorf("%s does not pass its check: %s", folder, plural(len(body.Refusals), "problem"))
			}
			if !asJSON {
				renderPlan(out, folder, body.Plan)
			}
			return nil
		},
	}
	cmd.Flags().BoolVar(&fromInstance, "from-instance", false, "<folder> is a path inside the instance; nothing is copied")
	cmd.Flags().BoolVar(&asJSON, "json", false, "print the Host's answer as JSON")
	return cmd
}

// hostCheck runs the Host's --solution-check on a folder inside the container and reads its last line.
func hostCheck(ctx context.Context, e engine.Engine, folder string) (checkBody, string, error) {
	res, err := e.Exec(ctx, instance.ContainerName, append(append([]string{}, hostSolutionCheck...), folder)...)
	if err != nil {
		// Measured with --doctor: a Host that does not know a switch ignores it, tries to serve and is
		// refused by the data-root lock the live Host holds. That is the image's age, not a fault.
		if strings.Contains(err.Error(), "already using this data root") {
			return checkBody{}, "", fmt.Errorf("the instance's image does not know `solution check` yet; run `yawble update`, then try again")
		}
		return checkBody{}, "", fmt.Errorf("the Host's check could not be run: %w", err)
	}
	lines := strings.Split(strings.TrimSpace(res.Stdout), "\n")
	last := strings.TrimSpace(lines[len(lines)-1])
	var body checkBody
	if err := json.Unmarshal([]byte(last), &body); err != nil || (!body.OK && len(body.Refusals) == 0) || (body.OK && body.Plan == nil) {
		return checkBody{}, "", fmt.Errorf("the Host's check answered something this CLI cannot read: %q", last)
	}
	return body, last, nil
}

// checkBody is the Host's answer: SolutionEndpoints.Body, the route's own shape.
type checkBody struct {
	OK       bool         `json:"ok"`
	Plan     *plan        `json:"plan"`
	Refusals []refusalRow `json:"refusals"`
}

type refusalRow struct {
	File   string `json:"file"`
	Field  string `json:"field"`
	Reason string `json:"reason"`
}

type plan struct {
	Package struct {
		ID          string `json:"id"`
		Name        string `json:"name"`
		Version     string `json:"version"`
		Description string `json:"description"`
	} `json:"package"`
	Team struct {
		Name         string `json:"name"`
		Instructions string `json:"instructions"`
	} `json:"team"`
	Members []struct {
		Name          string                     `json:"name"`
		Kind          string                     `json:"kind"`
		Role          string                     `json:"role"`
		Preset        *string                    `json:"preset"`
		Instructions  string                     `json:"instructions"`
		PluginID      *string                    `json:"pluginId"`
		PluginVersion *string                    `json:"pluginVersion"`
		Settings      map[string]json.RawMessage `json:"settings"`
	} `json:"members"`
	Plugins []struct {
		ID      string   `json:"id"`
		Name    string   `json:"name"`
		Version string   `json:"version"`
		Events  []string `json:"events"`
	} `json:"plugins"`
	Triggers []struct {
		Name          string  `json:"name"`
		Kind          string  `json:"kind"`
		Member        string  `json:"member"`
		Instruction   string  `json:"instruction"`
		WakeManager   string  `json:"wakeManager"`
		DailyTokenCap *int64  `json:"dailyTokenCap"`
		IdleOnly      bool    `json:"idleOnly"`
		Schedule      *string `json:"schedule"`
		EventType     *string `json:"eventType"`
		Filter        *string `json:"filter"`
		FolderPath    *string `json:"folderPath"`
		FolderGlob    *string `json:"folderGlob"`
	} `json:"triggers"`
	Skills []struct {
		Name        string   `json:"name"`
		Description string   `json:"description"`
		Roles       []string `json:"roles"`
	} `json:"skills"`
	Sites []struct {
		Name  string   `json:"name"`
		Files []string `json:"files"`
	} `json:"sites"`
	Tools *struct {
		Folder      string   `json:"folder"`
		InstalledAs string   `json:"installedAs"`
		Files       []string `json:"files"`
	} `json:"tools"`
	Inputs struct {
		Connections []struct {
			Member      string `json:"member"`
			Slot        string `json:"slot"`
			Description string `json:"description"`
			Required    bool   `json:"required"`
		} `json:"connections"`
		Documents []struct {
			Folder      string `json:"folder"`
			Description string `json:"description"`
			Required    bool   `json:"required"`
		} `json:"documents"`
	} `json:"inputs"`
	PersonSettings []struct {
		Member      string `json:"member"`
		Setting     string `json:"setting"`
		Description string `json:"description"`
		Required    bool   `json:"required"`
	} `json:"personSettings"`
	Ignored []string `json:"ignored"`
}

func renderRefusals(out io.Writer, folder string, refusals []refusalRow) {
	fmt.Fprintf(out, "%s does not pass its check (%s):\n", folder, plural(len(refusals), "problem"))
	for _, r := range refusals {
		fmt.Fprintf(out, "  %s %s: %s\n", r.File, r.Field, r.Reason)
	}
}

// renderPlan prints what an install would create. Every instruction is printed whole: it becomes a
// prompt, so it is read before anything runs.
func renderPlan(out io.Writer, folder string, p *plan) {
	fmt.Fprintf(out, "%s %s (%s) passes its check. Installing %s would create:\n\n", p.Package.Name, p.Package.Version, p.Package.ID, folder)
	fmt.Fprintf(out, "Team: %s\n", p.Team.Name)
	block(out, "  Instructions: ", p.Team.Instructions)

	fmt.Fprintf(out, "\nMembers (%d):\n", len(p.Members))
	for _, m := range p.Members {
		if m.Kind == "plugin" {
			fmt.Fprintf(out, "  %s: plugin %s %s\n", m.Name, deref(m.PluginID), deref(m.PluginVersion))
			keys := make([]string, 0, len(m.Settings))
			for k := range m.Settings {
				keys = append(keys, k)
			}
			sort.Strings(keys)
			for _, k := range keys {
				fmt.Fprintf(out, "    setting %s = %s\n", k, compact(m.Settings[k]))
			}
			continue
		}
		preset := "chosen by the team"
		if m.Preset != nil {
			preset = *m.Preset
		}
		fmt.Fprintf(out, "  %s: agent, %s, preset %s\n", m.Name, m.Role, preset)
		block(out, "    Instructions: ", m.Instructions)
	}

	if len(p.Plugins) > 0 {
		fmt.Fprintf(out, "\nPlugins installed (%d):\n", len(p.Plugins))
		for _, pl := range p.Plugins {
			fmt.Fprintf(out, "  %s %s (%s)", pl.ID, pl.Version, pl.Name)
			if len(pl.Events) > 0 {
				fmt.Fprintf(out, ", publishes %s", strings.Join(pl.Events, ", "))
			}
			fmt.Fprintln(out)
		}
	}

	fmt.Fprintf(out, "\nTriggers (%d):\n", len(p.Triggers))
	for _, t := range p.Triggers {
		var when string
		switch t.Kind {
		case "schedule":
			when = deref(t.Schedule)
		case "event":
			when = "on " + deref(t.EventType)
			if t.Filter != nil {
				when += " where " + *t.Filter
			}
		case "folder":
			when = "when files change in documents/" + deref(t.FolderPath)
			if t.FolderGlob != nil {
				when += " matching " + *t.FolderGlob
			}
		}
		limit := "no daily cap"
		if t.DailyTokenCap != nil {
			limit = fmt.Sprintf("daily cap %s tokens", thousands(*t.DailyTokenCap))
		}
		fmt.Fprintf(out, "  %s: %s, wakes %s; wakes the Manager: %s; %s\n", t.Name, when, t.Member, t.WakeManager, limit)
		block(out, "    Instruction: ", t.Instruction)
	}

	if len(p.Skills) > 0 {
		fmt.Fprintf(out, "\nTeam skills (%d):\n", len(p.Skills))
		for _, s := range p.Skills {
			fmt.Fprintf(out, "  %s (for %s): %s\n", s.Name, strings.Join(s.Roles, ", "), s.Description)
		}
	}
	if len(p.Sites) > 0 {
		fmt.Fprintf(out, "\nSites published (%d):\n", len(p.Sites))
		for _, s := range p.Sites {
			fmt.Fprintf(out, "  %s (%s)\n", s.Name, plural(len(s.Files), "file"))
		}
	}
	if p.Tools != nil {
		fmt.Fprintf(out, "\nTools: %s/ copied to the team's %s/ folder, named {solution} in instructions (%s): %s\n",
			p.Tools.Folder, p.Tools.InstalledAs, plural(len(p.Tools.Files), "file"), strings.Join(p.Tools.Files, ", "))
	}

	asks := len(p.Inputs.Documents) + len(p.PersonSettings) + len(p.Inputs.Connections)
	if asks > 0 {
		fmt.Fprintf(out, "\nYou will be asked for:\n")
		for _, d := range p.Inputs.Documents {
			fmt.Fprintf(out, "  documents in %s/ (%s): %s\n", d.Folder, requirement(d.Required), d.Description)
		}
		for _, s := range p.PersonSettings {
			fmt.Fprintf(out, "  %s's setting %s (%s): %s\n", s.Member, s.Setting, requirement(s.Required), s.Description)
		}
		for _, c := range p.Inputs.Connections {
			fmt.Fprintf(out, "  a connection for %s's %s (%s): %s\n", c.Member, c.Slot, requirement(c.Required), c.Description)
		}
	}
	if len(p.Ignored) > 0 {
		fmt.Fprintf(out, "\nIgnored (not read by this version): %s\n", strings.Join(p.Ignored, ", "))
	}
}

// block prints a label and text, the text's own lines kept and indented under the label.
func block(out io.Writer, label, text string) {
	if strings.TrimSpace(text) == "" {
		return
	}
	indent := strings.Repeat(" ", len(label))
	fmt.Fprintf(out, "%s%s\n", label, strings.ReplaceAll(strings.TrimRight(text, "\n"), "\n", "\n"+indent))
}

func compact(raw json.RawMessage) string {
	var v any
	if json.Unmarshal(raw, &v) != nil {
		return string(raw)
	}
	b, _ := json.Marshal(v)
	return string(b)
}

func requirement(required bool) string {
	if required {
		return "required"
	}
	return "optional"
}

func plural(n int, noun string) string {
	if n == 1 {
		return "1 " + noun
	}
	return fmt.Sprintf("%d %ss", n, noun)
}

func thousands(n int64) string {
	s := fmt.Sprintf("%d", n)
	for i := len(s) - 3; i > 0; i -= 3 {
		s = s[:i] + "," + s[i:]
	}
	return s
}
