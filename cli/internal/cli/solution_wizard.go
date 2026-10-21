package cli

import (
	"encoding/json"
	"errors"
	"fmt"
	"github.com/djlsystems/yawble/cli/internal/text"
	"os"
	"path"
	"path/filepath"
	"slices"
	"strconv"
	"strings"

	"github.com/djlsystems/yawble/cli/internal/instance"
)

// askTeamName asks for the new team's name until the Host accepts one, asking it again about
// each name typed. A name that is a team installed from an earlier version turns this into its update.
func (in *solutionInstall) askTeamName(p previewBody) (previewBody, error) {
	fmt.Fprintln(in.out)
	for {
		if p.NameRefusal != nil {
			fmt.Fprintf(in.out, "%s cannot be the team's name: %s\n", p.TeamName, *p.NameRefusal)
		}
		fmt.Fprintf(in.out, "Team name [%s]: ", p.TeamName)
		name, eof := in.ask()
		if name == "" || name == p.TeamName {
			if p.NameRefusal == nil {
				return p, nil
			}
			if eof {
				return p, fmt.Errorf("no team name was given that can be used; run again and choose another, or give one with --team")
			}
			continue
		}
		next, err := in.preview(&name)
		var refused errRefused
		if errors.As(err, &refused) {
			fmt.Fprintf(in.out, "%s cannot be the team's name: %s\n", name, refused.reason)
			if eof {
				return p, fmt.Errorf("no team name was given that can be used; run again and choose another, or give one with --team")
			}
			continue
		}
		if err != nil {
			return p, err
		}
		p = next
		if p.Mode == "update" {
			fmt.Fprintf(in.out, "%s is a team installed from %s %s: it will be updated.\n", p.TeamName, p.Plan.Package.ID, p.From)
			return p, nil
		}
		if p.NameRefusal == nil {
			return p, nil
		}
		if eof {
			return p, fmt.Errorf("%s cannot be the team's name: %s", p.TeamName, *p.NameRefusal)
		}
	}
}

// askSettings asks for each setting only a person provides. A blank answer keeps the default. On
// an update a kept member's setting is shown, not asked: the update keeps it.
func (in *solutionInstall) askSettings(p *plan, kept *keptPart) (map[string]map[string]any, error) {
	settings := map[string]map[string]any{}
	for _, s := range p.PersonSettings {
		if value, ok := kept.setting(s.Member, s.Setting); ok {
			fmt.Fprintf(in.out, "\n%s's setting %s: kept: %s. The update keeps it; change it in the member's settings.\n", s.Member, s.Setting, keptWords(value))
			continue
		}
		kind := text.OrEmpty(s.Type)
		if kind == "" {
			kind = "string"
		}
		fmt.Fprintf(in.out, "\n%s's setting %s (%s, %s): %s\n", s.Member, s.Setting, kind, requirement(s.Required), s.Description)
		if len(s.Choices) > 0 {
			fmt.Fprintf(in.out, "  Choices: %s\n", strings.Join(s.Choices, ", "))
		}
		shown := "none"
		var list []any
		if json.Unmarshal(s.Default, &list) == nil && list != nil {
			if len(list) > 0 {
				items := make([]string, len(list))
				for i, v := range list {
					items[i] = fmt.Sprint(v)
				}
				shown = strings.Join(items, ", ")
			}
		} else if len(s.Default) > 0 && string(s.Default) != "null" {
			shown = compact(s.Default)
		}
		hint := ""
		switch {
		case isListType(kind):
			hint = ", comma-separated"
		case kind == "boolean":
			hint = ", y or n"
		}
		for {
			fmt.Fprintf(in.out, "  Value%s [%s]: ", hint, shown)
			answer, eof := in.ask()
			if answer == "" {
				break
			}
			v, err := parseSetting(kind, answer, s.Choices)
			if err != nil {
				fmt.Fprintf(in.out, "  %s\n", err)
				if eof {
					break
				}
				continue
			}
			if settings[s.Member] == nil {
				settings[s.Member] = map[string]any{}
			}
			settings[s.Member][s.Setting] = v
			break
		}
	}
	return settings, nil
}

func isListType(kind string) bool {
	return kind == "list" || kind == "array" || strings.HasSuffix(kind, "[]")
}

// parseSetting turns a typed answer into the JSON value the setting's type takes.
func parseSetting(kind, answer string, choices []string) (any, error) {
	check := func(v string) error {
		if len(choices) > 0 && !slices.Contains(choices, v) {
			return fmt.Errorf("%q is not one of %s", v, strings.Join(choices, ", "))
		}
		return nil
	}
	switch {
	case kind == "boolean":
		switch strings.ToLower(answer) {
		case "y", "yes", "true":
			return true, nil
		case "n", "no", "false":
			return false, nil
		}
		return nil, fmt.Errorf("answer y or n")
	case kind == "number" || kind == "integer":
		n, err := strconv.ParseFloat(answer, 64)
		if err != nil || (kind == "integer" && n != float64(int64(n))) {
			return nil, fmt.Errorf("%q is not a %s", answer, kind)
		}
		if kind == "integer" {
			return int64(n), nil
		}
		return n, nil
	case isListType(kind):
		list := []string{}
		for _, item := range strings.Split(answer, ",") {
			if item = strings.TrimSpace(item); item != "" {
				if err := check(item); err != nil {
					return nil, err
				}
				list = append(list, item)
			}
		}
		return list, nil
	default:
		if err := check(answer); err != nil {
			return nil, err
		}
		return answer, nil
	}
}

// askConnections asks, for each connection slot, which of the instance's connections it uses.
// On an update a kept member's binding is shown, not asked: the update keeps it.
func (in *solutionInstall) askConnections(p *plan, available []connectionRow, kept *keptPart) map[string]map[string]string {
	chosen := map[string]map[string]string{}
	for _, c := range p.Inputs.Connections {
		if id, ok := kept.connection(c.Member, c.Slot); ok {
			words := "not connected"
			for _, a := range available {
				if a.ID == id {
					words = fmt.Sprintf("%s (%s, %s)", a.Name, a.Provider, a.Account)
				}
			}
			if id != "" && words == "not connected" {
				words = id
			}
			fmt.Fprintf(in.out, "\nA connection for %s's %s: kept: %s. The update keeps it; change it in the member's settings.\n", c.Member, c.Slot, words)
			if id == "" {
				in.blocked(c.Required)
			}
			continue
		}
		fmt.Fprintf(in.out, "\nA connection for %s's %s (%s): %s\n", c.Member, c.Slot, requirement(c.Required), c.Description)
		if len(available) == 0 {
			fmt.Fprintln(in.out, "  This instance has no connections yet: add one in Admin -> Connections or with `yawble connect`, then bind it in the member's settings.")
			in.blocked(c.Required)
			continue
		}
		for i, a := range available {
			fmt.Fprintf(in.out, "  %d. %s (%s, %s) %s\n", i+1, a.Name, a.Provider, a.Account, a.Status)
		}
		for {
			fmt.Fprint(in.out, "  Number (blank skips): ")
			answer, eof := in.ask()
			if answer == "" {
				in.blocked(c.Required)
				break
			}
			n, err := strconv.Atoi(answer)
			if err != nil || n < 1 || n > len(available) {
				fmt.Fprintf(in.out, "  Choose a number from 1 to %d.\n", len(available))
				if eof {
					in.blocked(c.Required)
					break
				}
				continue
			}
			if chosen[c.Member] == nil {
				chosen[c.Member] = map[string]string{}
			}
			chosen[c.Member][c.Slot] = available[n-1].ID
			break
		}
	}
	return chosen
}

func (in *solutionInstall) blocked(required bool) {
	if required {
		fmt.Fprintln(in.out, "  Skipped: required - the team shows as blocked until it is provided.")
	}
}

// On an update the files already in the folder are named, and skipping keeps them.
func (in *solutionInstall) askDocuments(p *plan, kept *keptPart) map[string]string {
	chosen := map[string]string{}
	for _, d := range p.Inputs.Documents {
		fmt.Fprintf(in.out, "\nDocuments in %s/ (%s): %s\n", d.Folder, requirement(d.Required), d.Description)
		present := kept.files(d.Folder)
		prompt := "  A file on this computer (blank skips): "
		if len(present) > 0 {
			fmt.Fprintf(in.out, "  Already in %s/: %s. The update keeps them.\n", d.Folder, strings.Join(present, ", "))
			prompt = "  Another file on this computer (blank keeps them): "
		}
		for {
			fmt.Fprint(in.out, prompt)
			answer, eof := in.ask()
			if answer == "" {
				in.blocked(d.Required && len(present) == 0)
				break
			}
			if info, err := os.Stat(answer); err != nil || !info.Mode().IsRegular() {
				fmt.Fprintf(in.out, "  %s is not a file on this computer.\n", answer)
				if eof {
					in.blocked(d.Required && len(present) == 0)
					break
				}
				continue
			}
			chosen[d.Folder] = answer
			break
		}
	}
	return chosen
}

// stageDocuments copies each chosen file to <stage>/.documents/<input folder>/<file name>.
func (in *solutionInstall) stageDocuments(chosen map[string]string) (map[string][]string, error) {
	staged := map[string][]string{}
	if len(chosen) == 0 {
		return staged, nil
	}
	if err := in.makeStage(); err != nil {
		return nil, err
	}
	root := path.Join(in.stage, ".documents")
	for folder, local := range chosen {
		dir := path.Join(root, folder)
		if !strings.HasPrefix(dir, root+"/") {
			return nil, fmt.Errorf("the document input %q is not a folder name this CLI can stage", folder)
		}
		if _, err := in.e.Exec(in.ctx, instance.ContainerName, "sh", "-c", solutionInstallFolderScript, "sh", dir); err != nil {
			return nil, err
		}
		target := path.Join(dir, filepath.Base(local))
		if err := in.e.CopyTo(in.ctx, instance.ContainerName, local, target); err != nil {
			return nil, err
		}
		staged[folder] = []string{target}
	}
	return staged, in.seal()
}
