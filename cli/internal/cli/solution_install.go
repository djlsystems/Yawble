package cli

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"github.com/djlsystems/yawble/cli/internal/text"
	"io"
	"path"
	"path/filepath"
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
	cmd.Flags().BoolVarP(&yes, "yes", "y", false, "install without asking for a yes")
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
		return previewBody{}, fmt.Errorf("%s does not pass its check: %s", in.folder, text.Plural(len(p.Refusals), "problem"))
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
	x := hostExchange{root: pluginsRoot, request: solutionRequestScript, report: solutionReportScript, withdraw: solutionWithdrawScript, poll: rescanPoll}
	answer, err := x.ask(in.ctx, in.e, string(request), req.Request, wait)
	if errors.Is(err, errNoAnswer) {
		if req.Action == "preview" {
			return 0, nil, errNoAnswer
		}
		return 0, nil, fmt.Errorf("the Host did not answer the %s request within %s, and the request was withdrawn; "+
			"the team board shows whether the team was made", req.Action, wait)
	}
	if err != nil {
		return 0, nil, err
	}
	var r solutionReport
	_ = json.Unmarshal([]byte(answer), &r)
	return r.Status, r.Body, nil
}
