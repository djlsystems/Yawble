package cli

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"path"
	"path/filepath"
	"strconv"
	"strings"
	"time"

	"github.com/spf13/cobra"

	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/instance"
)

// solutionsStage is where `solution install` stages a package from this computer, and the files
// chosen for its document inputs, one folder per install, removed afterwards. It is under the
// plugins root, which only root and the Host can write, so the Host reads the copy it was asked about.
const solutionsStage = pluginsRoot + "/.solutions"

// The web wizard's names for the install's steps, by number; the Host's report carries its own.
var solutionStepTitles = map[int]string{
	1: "Install the plugins", 2: "Create the team", 3: "Hire the members", 4: "Register the team skills",
	5: "Copy the tools", 6: "Publish the sites", 7: "Create the triggers", 8: "Record the package",
}

// The scripts take every value as a positional argument, never spliced into the text.
const (
	// $1 plugins root, $2 nonce: an empty stage folder, harness:agent 0750, as are the folders above it.
	solutionInstallStageScript = `set -e
mkdir -p "$1/.solutions"
rm -rf "$1/.solutions/$2"
mkdir "$1/.solutions/$2"
chown harness:agent "$1" "$1/.solutions" "$1/.solutions/$2"
chmod u=rwx,g=rx,o=,ug-s "$1" "$1/.solutions" "$1/.solutions/$2"`

	// $1 a folder inside the stage, made for `cp` to put a document in.
	solutionInstallFolderScript = `set -e
mkdir -p "$1"`

	// $1 the stage: everything copied into it gets the modes the plugin staging uses, whatever
	// they were here: harness:agent, directories 0750, files 0640. -h and ! -type l leave a
	// symlink's target alone.
	solutionInstallSealScript = `set -e
chown -R -h harness:agent "$1"
find "$1" -type d -exec chmod u=rwx,g=rx,o=,ug-s {} +
find "$1" ! -type d ! -type l -exec chmod 0640 {} +`

	// $1 plugins root, $2 the request's JSON: written beside .solution and moved in, so the Host
	// never reads half of it.
	solutionRequestScript = `set -e
mkdir -p "$1"
chown harness:agent "$1"
chmod u=rwx,g=rx,o=,ug-s "$1"
printf '%s\n' "$2" > "$1/.solution.tmp"
chown harness:agent "$1/.solution.tmp"
chmod 0640 "$1/.solution.tmp"
mv -f "$1/.solution.tmp" "$1/.solution"`

	solutionReportScript = `cat "$1/.solution-report.json" 2>/dev/null || true`

	// $1 plugins root, $2 nonce: removes the request if it is still this one.
	solutionWithdrawScript = `grep -qF "$2" "$1/.solution" 2>/dev/null && rm -f "$1/.solution"; true`
)

func newSolutionInstallCommand(deps Deps) *cobra.Command {
	var team string
	var fromInstance, yes bool
	cmd := &cobra.Command{
		Use:   "install <folder>",
		Short: "Install a solution package as a new team, or update a team installed from an earlier version",
		Long: "<folder> is a solution package: the folder holding solution.json. It is copied into the running instance " +
			"(" + solutionsStage + "/<nonce>/), and the Host says what installing it would do; that plan is printed the way " +
			"`solution check` prints it. Then the questions the web wizard asks: the team's name, each setting only a person " +
			"provides, a connection for each connection slot and a file from this computer for each document input. Blank " +
			"keeps a setting's default and skips the rest; a required input skipped leaves the team blocked until it is " +
			"provided. After a yes (--yes answers it) the Host installs the package step by step, and undoes every step when " +
			"one fails. The copy is removed afterwards.\n\n" +
			"--team names the team: a team installed from an earlier version of the same package is updated (the changes " +
			"are printed first, with the versions from and to), keeping its settings, bindings, documents and site data; any " +
			"other name is the new team's.\n\n" +
			"With --from-instance, <folder> is an absolute path inside the instance and the package is not copied.\n\n" +
			"Packages to install are on the marketplace, yawble.ai.",
		Example: "  yawble solution install ./job-tracker-1.0.0\n" +
			"  yawble solution install ./job-tracker-1.1.0 --team \"Job Tracker\"\n" +
			"  yawble solution install --from-instance /data/documents/acme/job-tracker-1.0.0 --yes",
		Args: cobra.ExactArgs(1),
		RunE: func(cmd *cobra.Command, args []string) error {
			folder := args[0]
			if err := packageFolder(folder, fromInstance); err != nil {
				return err
			}
			ctx := cmd.Context()
			e, err := runningEngine(ctx, deps, "solution install")
			if err != nil {
				return err
			}
			in := &solutionInstall{
				ctx: ctx, e: e, out: cmd.OutOrStdout(), answers: bufio.NewReader(cmd.InOrStdin()),
				folder: folder, stage: path.Join(solutionsStage, newNonce()),
			}
			defer in.removeStage()
			return in.run(fromInstance, team, yes)
		},
	}
	cmd.Flags().StringVar(&team, "team", "", "the team's name: an existing team from this package is updated, any other name is the new team's")
	cmd.Flags().BoolVar(&fromInstance, "from-instance", false, "<folder> is a path inside the instance; nothing is copied")
	cmd.Flags().BoolVar(&yes, "yes", false, "install without asking for a yes")
	return cmd
}

type solutionInstall struct {
	ctx     context.Context
	e       engine.Engine
	out     io.Writer
	answers *bufio.Reader
	folder  string // as given
	stage   string // /data/plugins/.solutions/<nonce>
	staged  bool
	inside  string // the package's folder inside the instance
}

// solutionRequest is <plugins>/.solution, one question for the Host's SolutionRequests.
type solutionRequest struct {
	Request     string                       `json:"request"`
	Action      string                       `json:"action"`
	Folder      string                       `json:"folder"`
	Team        *string                      `json:"team"`
	TeamName    *string                      `json:"teamName"`
	Agent       *string                      `json:"agent"`
	Settings    map[string]map[string]any    `json:"settings"`
	Connections map[string]map[string]string `json:"connections"`
	Documents   map[string][]string          `json:"documents"`
}

// solutionReport is <plugins>/.solution-report.json: the status and body the matching route answers.
type solutionReport struct {
	Request string          `json:"request"`
	Status  int             `json:"status"`
	Body    json.RawMessage `json:"body"`
}

// previewBody is POST /api/solutions/preview's answer.
type previewBody struct {
	OK          bool            `json:"ok"`
	Error       string          `json:"error"`
	Refusals    []refusalRow    `json:"refusals"`
	Mode        string          `json:"mode"`
	Team        string          `json:"team"`
	TeamName    string          `json:"teamName"`
	NameRefusal *string         `json:"nameRefusal"`
	From        string          `json:"from"`
	To          string          `json:"to"`
	Plan        *plan           `json:"plan"`
	Diff        *solutionDiff   `json:"diff"`
	Connections []connectionRow `json:"connections"`
	Kept        *keptPart       `json:"kept"`
	Secrets     []secretRow     `json:"secrets"`
}

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

// secretSetWith is the exact way to set key: this CLI prompts for the value, and the Host reads it
// when it restarts.
func secretSetWith(key string) string {
	return "yawble secret set " + key + " (it prompts for the value), then yawble up to restart the Host"
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
				return deref(c.Connection), true
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

// installBody is POST /api/solutions/install's (and update's) answer.
type installBody struct {
	OK         bool         `json:"ok"`
	Error      string       `json:"error"`
	Refusals   []refusalRow `json:"refusals"`
	Team       string       `json:"team"`
	TeamName   string       `json:"teamName"`
	Version    string       `json:"version"`
	From       string       `json:"from"`
	To         string       `json:"to"`
	Step       string       `json:"step"`
	StepNumber int          `json:"stepNumber"`
	Reason     string       `json:"reason"`
	Missing    []struct {
		Kind        string  `json:"kind"`
		Name        string  `json:"name"`
		Member      *string `json:"member"`
		Description string  `json:"description"`
	} `json:"missing"`
	Secrets   []secretRow   `json:"secrets"`
	Unset     []string      `json:"unset"`
	FirstRuns []firstRunRow `json:"firstRuns"`
	Steps     []struct {
		Step   string `json:"step"`
		Number int    `json:"number"`
		Title  string `json:"title"`
		Done   bool   `json:"done"`
	} `json:"steps"`
}

func (in *solutionInstall) run(fromInstance bool, team string, yes bool) error {
	in.inside = in.folder
	if !fromInstance {
		name := filepath.Base(in.folder)
		if abs, err := filepath.Abs(in.folder); err == nil {
			name = filepath.Base(abs)
		}
		if err := in.makeStage(); err != nil {
			return err
		}
		in.inside = path.Join(in.stage, name)
		if err := in.e.CopyTo(in.ctx, instance.ContainerName, in.folder, in.inside); err != nil {
			return err
		}
		if err := in.seal(); err != nil {
			return err
		}
	}

	var teamArg *string
	if team != "" {
		teamArg = &team
	}
	p, err := in.preview(teamArg)
	if err != nil {
		return err
	}
	renderPlan(in.out, in.folder, p.Plan)

	if p.Mode == "install" {
		if team != "" {
			if p.NameRefusal != nil {
				return fmt.Errorf("%s cannot be the team's name: %s", p.TeamName, *p.NameRefusal)
			}
		} else if p, err = in.askTeamName(p); err != nil {
			return err
		}
	}
	if p.Mode == "update" {
		renderDiff(in.out, p)
	}

	settings, err := in.askSettings(p.Plan, p.Kept)
	if err != nil {
		return err
	}
	connections := in.askConnections(p.Plan, p.Connections, p.Kept)
	documents := in.askDocuments(p.Plan, p.Kept)
	renderSecrets(in.out, p.Secrets, func(member, setting string) any {
		if raw, ok := p.Kept.setting(member, setting); ok {
			var kept any
			_ = json.Unmarshal(raw, &kept)
			return kept
		}
		return settings[member][setting]
	})

	question := fmt.Sprintf("Install %s %s as team %s?", p.Plan.Package.Name, p.Plan.Package.Version, p.TeamName)
	if p.Mode == "update" {
		question = fmt.Sprintf("Update team %s from %s %s to %s?", p.TeamName, p.Plan.Package.Name, p.From, p.To)
	}
	if !yes {
		fmt.Fprintf(in.out, "\n%s [y/N] ", question)
		answer, _ := in.ask()
		if a := strings.ToLower(answer); a != "y" && a != "yes" {
			fmt.Fprintln(in.out, "nothing was installed")
			return nil
		}
	}

	staged, err := in.stageDocuments(documents)
	if err != nil {
		return err
	}
	req := solutionRequest{Action: "install", Folder: in.inside, Settings: settings, Connections: connections, Documents: staged}
	if p.Mode == "update" {
		req.Action, req.Team = "update", &p.Team
	} else {
		req.TeamName = &p.TeamName
	}
	fmt.Fprintln(in.out)
	status, raw, err := in.askHost(req, installWait)
	if err != nil {
		return err
	}
	return in.result(p, status, raw)
}

// ask reads one answer, trimmed; eof says there is nothing more to read.
func (in *solutionInstall) ask() (string, bool) {
	line, err := in.answers.ReadString('\n')
	return strings.TrimSpace(line), err != nil
}

func (in *solutionInstall) makeStage() error {
	if in.staged {
		return nil
	}
	in.staged = true
	nonce := path.Base(in.stage)
	_, err := in.e.Exec(in.ctx, instance.ContainerName, "sh", "-c", solutionInstallStageScript, "sh", pluginsRoot, nonce)
	return err
}

func (in *solutionInstall) seal() error {
	_, err := in.e.Exec(in.ctx, instance.ContainerName, "sh", "-c", solutionInstallSealScript, "sh", in.stage)
	return err
}

// removeStage always runs, whatever happened: a staged package is never left in the instance.
func (in *solutionInstall) removeStage() {
	if in.staged {
		_, _ = in.e.Exec(context.WithoutCancel(in.ctx), instance.ContainerName, "rm", "-rf", in.stage)
	}
}

// preview asks what installing would do. An image that never answers predates the command.
func (in *solutionInstall) preview(team *string) (previewBody, error) {
	status, raw, err := in.askHost(solutionRequest{Action: "preview", Folder: in.inside, Team: team}, rescanWait)
	if errors.Is(err, errNoAnswer) {
		return previewBody{}, fmt.Errorf("the Host did not answer within %s, and the request was withdrawn: the instance's image is too old "+
			"for `solution install`; run `yawble update`, then try again", rescanWait)
	}
	if err != nil {
		return previewBody{}, err
	}
	var p previewBody
	if err := json.Unmarshal(raw, &p); err != nil {
		return previewBody{}, fmt.Errorf("the Host answered something this CLI cannot read: %q", string(raw))
	}
	switch {
	case status != 200:
		return previewBody{}, fmt.Errorf("%s cannot be installed: %s", in.folder, p.Error)
	case !p.OK && len(p.Refusals) > 0:
		renderRefusals(in.out, in.folder, p.Refusals)
		return previewBody{}, fmt.Errorf("%s does not pass its check: %s", in.folder, plural(len(p.Refusals), "problem"))
	case !p.OK:
		return p, errRefused{p.Error}
	case p.Plan == nil:
		return previewBody{}, fmt.Errorf("the Host answered something this CLI cannot read: %q", string(raw))
	}
	return p, nil
}

type errRefused struct{ reason string }

func (e errRefused) Error() string { return e.reason }

var errNoAnswer = errors.New("no answer")

// askHost writes one request and waits for the report carrying its nonce. A request no Host
// answered is withdrawn, so a Host started later does not carry it out.
func (in *solutionInstall) askHost(req solutionRequest, wait time.Duration) (int, json.RawMessage, error) {
	req.Request = newNonce()
	if req.Settings == nil {
		req.Settings = map[string]map[string]any{}
	}
	if req.Connections == nil {
		req.Connections = map[string]map[string]string{}
	}
	if req.Documents == nil {
		req.Documents = map[string][]string{}
	}
	request, _ := json.Marshal(req)
	if _, err := in.e.Exec(in.ctx, instance.ContainerName, "sh", "-c", solutionRequestScript, "sh", pluginsRoot, string(request)); err != nil {
		return 0, nil, err
	}
	deadline := time.Now().Add(wait)
	for {
		res, err := in.e.Exec(in.ctx, instance.ContainerName, "sh", "-c", solutionReportScript, "sh", pluginsRoot)
		if err != nil {
			return 0, nil, err
		}
		var r solutionReport
		if json.Unmarshal([]byte(strings.TrimSpace(res.Stdout)), &r) == nil && r.Request == req.Request {
			return r.Status, r.Body, nil
		}
		if time.Now().After(deadline) {
			_, _ = in.e.Exec(context.WithoutCancel(in.ctx), instance.ContainerName, "sh", "-c", solutionWithdrawScript, "sh", pluginsRoot, req.Request)
			if req.Action == "preview" {
				return 0, nil, errNoAnswer
			}
			return 0, nil, fmt.Errorf("the Host did not answer the %s request within %s, and the request was withdrawn; "+
				"the team board shows whether the team was made", req.Action, wait)
		}
		select {
		case <-in.ctx.Done():
			return 0, nil, in.ctx.Err()
		case <-time.After(rescanPoll):
		}
	}
}

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
	if !listed {
		fmt.Fprintln(out, "  nothing is added, changed or removed")
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
		kind := deref(s.Type)
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
		if len(choices) > 0 && !contains(choices, v) {
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
		return fmt.Errorf("%s does not pass its check: %s", in.folder, plural(len(b.Refusals), "problem"))
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
		return fmt.Errorf("Failed at step %d (%s): %s. Nothing was left behind.", b.StepNumber, title, strings.TrimSuffix(b.Reason, "."))
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
