package cli_test

import (
	"encoding/json"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
	"time"

	"github.com/djlsystems/yawble/cli/internal/cli"
	"github.com/djlsystems/yawble/cli/internal/engine"
)

// `solution install` asks the Host through /data/plugins/.solution and reads
// /data/plugins/.solution-report.json. The scripted engine fakes the Host's side: each report's
// body is a route's JSON body (testdata/solution-*.json), shaped as the contract's preview,
// install and update answers; the plans are the Host's real check of the Job Tracker sample, with
// one connection slot added so the picker is exercised.

const (
	installStage  = "/data/plugins/.solutions/n1"
	installInside = installStage + "/job-tracker"
)

var programs = []string{"podman", "docker"}

// routeBody reads a testdata body, changed by edit when given.
func routeBody(t *testing.T, name string, edit func(map[string]any)) map[string]any {
	t.Helper()
	b, err := os.ReadFile(filepath.Join("testdata", name))
	must(t, err)
	var body map[string]any
	must(t, json.Unmarshal(b, &body))
	if edit != nil {
		edit(body)
	}
	return body
}

// hostReport is .solution-report.json answering request <request> with a route's status and body.
func hostReport(t *testing.T, request string, status int, body map[string]any) engine.Result {
	t.Helper()
	b, err := json.Marshal(map[string]any{"request": request, "status": status, "body": body})
	must(t, err)
	return engine.Result{Stdout: string(b) + "\n"}
}

// installScript is a running instance on program whose Host answers the solution reports in turn.
// The stage is n1, so the requests are n2, n3, ...
func installScript(t *testing.T, program string, reports ...engine.Result) *engine.Scripted {
	t.Helper()
	t.Cleanup(cli.FastSolution())
	var s *engine.Scripted
	if program == "podman" {
		s = runningScript()
	} else {
		s = engine.NewScripted()
		s.On("docker version", engine.Result{Stdout: "27.1.0\n"})
		s.On("docker container inspect", engine.Result{Stdout: "running|" + testImage + "|\n"})
	}
	if len(reports) == 0 {
		reports = []engine.Result{{}}
	}
	s.OnSequence(program+" exec yawble sh -c "+cli.SolutionReportScript+" sh /data/plugins", reports...)
	return s
}

func installDeps(s *engine.Scripted, program, answers string) cli.Deps {
	deps := depsFor(s, program)
	deps.Stdin = strings.NewReader(answers)
	return deps
}

type sentRequest struct {
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

// requests are the .solution requests written, in order, parsed back from the script's argument.
func requests(t *testing.T, s *engine.Scripted, program string) []sentRequest {
	t.Helper()
	prefix := program + " exec yawble sh -c " + cli.SolutionRequestScript + " sh /data/plugins "
	var got []sentRequest
	for _, c := range s.Calls {
		if rest, ok := strings.CutPrefix(c, prefix); ok {
			var r sentRequest
			if err := json.Unmarshal([]byte(rest), &r); err != nil {
				t.Fatalf("request is not JSON: %v\n%s", err, rest)
			}
			got = append(got, r)
		}
	}
	return got
}

func inOrder(t *testing.T, s *engine.Scripted, want ...string) {
	t.Helper()
	at := 0
	for _, c := range s.Calls {
		if at < len(want) && c == want[at] {
			at++
		}
	}
	if at != len(want) {
		t.Fatalf("calls out of order; wanted %q next, got:\n%s", want[at], strings.Join(s.Calls, "\n"))
	}
}

func containsAll(t *testing.T, what, text string, lines ...string) {
	t.Helper()
	for _, l := range lines {
		if !strings.Contains(text, l) {
			t.Errorf("%s lacks %q:\n%s", what, l, text)
		}
	}
}

func named(name string) func(map[string]any) {
	return func(b map[string]any) { b["teamName"] = name }
}

func TestSolutionInstallOfANewTeamAsksEveryQuestionStagesInstallsAndCleansUp(t *testing.T) {
	for _, program := range programs {
		t.Run(program, func(t *testing.T) {
			folder := solutionPackage(t)
			resume := filepath.Join(t.TempDir(), "resume.pdf")
			must(t, os.WriteFile(resume, []byte("%PDF"), 0o644))
			s := installScript(t, program,
				hostReport(t, "older", 200, routeBody(t, "solution-install-done.json", nil)), // a report from before: not this request's
				hostReport(t, "n2", 200, routeBody(t, "solution-preview-install.json", nil)),
				hostReport(t, "n3", 200, routeBody(t, "solution-preview-install.json", named("Job Hunt"))),
				hostReport(t, "n4", 200, routeBody(t, "solution-install-done.json", nil)),
			)
			// The team's name, the Scout's sources, the second connection, the resume, yes.
			answers := "Job Hunt\nsample\n2\n" + resume + "\ny\n"

			code, out, errOut := run(t, installDeps(s, program, answers), "solution", "install", folder)
			if code != 0 {
				t.Fatalf("exit %d: %s %s\n%s", code, out, errOut, strings.Join(s.Calls, "\n"))
			}

			exec := program + " exec yawble "
			inOrder(t, s,
				exec+"sh -c "+cli.SolutionInstallStageScript+" sh /data/plugins n1",
				program+" cp "+folder+" yawble:"+installInside,
				exec+"sh -c "+cli.SolutionInstallSealScript+" sh "+installStage,
				exec+"sh -c "+cli.SolutionInstallFolderScript+" sh "+installStage+"/.documents/Resume",
				program+" cp "+resume+" yawble:"+installStage+"/.documents/Resume/resume.pdf",
				exec+"sh -c "+cli.SolutionInstallSealScript+" sh "+installStage,
				exec+"rm -rf "+installStage,
			)

			reqs := requests(t, s, program)
			if len(reqs) != 3 {
				t.Fatalf("want preview, preview, install; got %+v", reqs)
			}
			if reqs[0].Action != "preview" || reqs[0].Request != "n2" || reqs[0].Folder != installInside || reqs[0].Team != nil {
				t.Errorf("first preview %+v", reqs[0])
			}
			if reqs[1].Action != "preview" || reqs[1].Team == nil || *reqs[1].Team != "Job Hunt" {
				t.Errorf("the typed name was not previewed: %+v", reqs[1])
			}
			in := reqs[2]
			if in.Action != "install" || in.Request != "n4" || in.Folder != installInside || in.Team != nil || in.TeamName == nil || *in.TeamName != "Job Hunt" || in.Agent != nil {
				t.Errorf("install %+v", in)
			}
			if b, _ := json.Marshal(in.Settings); string(b) != `{"Scout":{"sources":["sample"]}}` {
				t.Errorf("settings %s", b)
			}
			if b, _ := json.Marshal(in.Connections); string(b) != `{"Scout":{"board":"c-91be"}}` {
				t.Errorf("connections %s", b)
			}
			if b, _ := json.Marshal(in.Documents); string(b) != `{"Resume":["/data/plugins/.solutions/n1/.documents/Resume/resume.pdf"]}` {
				t.Errorf("documents %s", b)
			}

			containsAll(t, "stdout", out,
				"Job Tracker 1.0.0 (job-tracker) passes its check. Installing "+folder+" would create:",
				"  Scan for postings: every 3600 seconds, wakes Scout; wakes the Manager: never; no daily cap",
				"Team name [Job Tracker]: ",
				"Scout's setting sources (list, optional): Which job boards the Scout may read.",
				"  Choices: sample",
				"  Value, comma-separated [none]: ",
				"A connection for Scout's board (optional): The job board account the Scout signs in with.",
				"  1. Work Google (google, ann@example.com) connected",
				"  2. Job board (custom, ann) connected",
				"Documents in Resume/ (required): Your reference resume, .docx or PDF.",
				"Install Job Tracker 1.0.0 as team Job Hunt? [y/N] ",
				"  1. Install the plugins: done",
				"  6. Publish the sites: done",
				"  8. Record the package: done",
				"Installed Job Tracker 1.0.0 as team Job Hunt.",
				"Nothing is missing: the team is ready.",
			)
			if strings.Contains(out, "blocked") {
				t.Errorf("nothing was skipped:\n%s", out)
			}
		})
	}
}

func TestSolutionInstallWithTeamNamingAnInstalledTeamShowsTheDiffAndUpdates(t *testing.T) {
	for _, program := range programs {
		t.Run(program, func(t *testing.T) {
			folder := solutionPackage(t)
			s := installScript(t, program,
				hostReport(t, "n2", 200, routeBody(t, "solution-preview-update.json", nil)),
				hostReport(t, "n3", 200, routeBody(t, "solution-update-done.json", nil)),
			)
			// Keep the default, skip the connection and the resume, yes.
			code, out, errOut := run(t, installDeps(s, program, "\n\n\ny\n"), "solution", "install", folder, "--team", "Job Tracker")
			if code != 0 {
				t.Fatalf("exit %d: %s %s", code, out, errOut)
			}
			reqs := requests(t, s, program)
			if len(reqs) != 2 || reqs[0].Action != "preview" || reqs[0].Team == nil || *reqs[0].Team != "Job Tracker" {
				t.Fatalf("requests %+v", reqs)
			}
			up := reqs[1]
			if up.Action != "update" || up.Team == nil || *up.Team != "job-tracker" || up.TeamName != nil || up.Folder != installInside ||
				len(up.Settings) != 0 || len(up.Connections) != 0 || len(up.Documents) != 0 {
				t.Errorf("update %+v", up)
			}
			containsAll(t, "stdout", out,
				"Job Tracker 1.1.0 (job-tracker) passes its check.",
				"Updating team Job Tracker from job-tracker 1.0.0 to 1.1.0:",
				"  Members changed: Scout",
				"  Plugins changed: job-board 0.1.0 -> 0.2.0",
				"  Triggers added: Weekly digest",
				"  Triggers removed: New posting",
				"  Skills changed: job-search-playbook",
				"Update team Job Tracker from Job Tracker 1.0.0 to 1.1.0? [y/N] ",
				"  7. Create the triggers: done",
				"Updated team Job Tracker from Job Tracker 1.0.0 to 1.1.0.",
			)
			if strings.Contains(out, "Team name [") {
				t.Errorf("--team was given; the name must not be asked:\n%s", out)
			}
			if len(callsContaining(s, "rm -rf "+installStage)) != 1 {
				t.Errorf("the stage was not removed:\n%s", strings.Join(s.Calls, "\n"))
			}
		})
	}
}

func TestSolutionInstallUpdateShowsWhatItKeepsInsteadOfAskingAgain(t *testing.T) {
	for _, program := range programs {
		t.Run(program, func(t *testing.T) {
			s := installScript(t, program,
				hostReport(t, "n2", 200, routeBody(t, "solution-preview-update.json", func(b map[string]any) {
					b["kept"] = map[string]any{
						"settings":    []any{map[string]any{"member": "Scout", "setting": "sources", "value": []any{"sample"}}},
						"connections": []any{map[string]any{"member": "Scout", "slot": "board", "connection": nil}},
						"documents":   []any{map[string]any{"folder": "Resume", "files": []any{"resume.pdf"}}},
					}
				})),
				hostReport(t, "n3", 200, routeBody(t, "solution-update-done.json", nil)),
			)
			// Only the documents are asked (blank keeps them), then yes.
			code, out, errOut := run(t, installDeps(s, program, "\ny\n"), "solution", "install", solutionPackage(t), "--team", "Job Tracker")
			if code != 0 {
				t.Fatalf("exit %d: %s %s", code, out, errOut)
			}
			containsAll(t, "stdout", out,
				"Scout's setting sources: kept: sample. The update keeps it; change it in the member's settings.",
				"A connection for Scout's board: kept: not connected. The update keeps it; change it in the member's settings.",
				"Documents in Resume/ (required): Your reference resume, .docx or PDF. The Writer starts every letter from it.\n"+
					"  Already in Resume/: resume.pdf. The update keeps them.\n"+
					"  Another file on this computer (blank keeps them): \n"+
					"Update team Job Tracker from Job Tracker 1.0.0 to 1.1.0? [y/N] ",
				"Updated team Job Tracker from Job Tracker 1.0.0 to 1.1.0.",
			)
			for _, asked := range []string{"Value, comma-separated", "Number (blank skips)", "Skipped: required"} {
				if strings.Contains(out, asked) {
					t.Errorf("a kept input was asked again (%q):\n%s", asked, out)
				}
			}
			reqs := requests(t, s, program)
			if len(reqs) != 2 || reqs[1].Action != "update" || len(reqs[1].Settings) != 0 || len(reqs[1].Connections) != 0 || len(reqs[1].Documents) != 0 {
				t.Errorf("requests %+v", reqs)
			}
		})
	}
}

func TestSolutionInstallFailureNamesTheStepAndExitsOne(t *testing.T) {
	for _, program := range programs {
		t.Run(program, func(t *testing.T) {
			s := installScript(t, program,
				hostReport(t, "n2", 200, routeBody(t, "solution-preview-install.json", nil)),
				hostReport(t, "n3", 200, routeBody(t, "solution-install-failed.json", nil)),
			)
			code, out, errOut := run(t, installDeps(s, program, "\n\n\n\n"), "solution", "install", solutionPackage(t), "--yes")
			if code != 1 {
				t.Fatalf("exit %d: %s %s", code, out, errOut)
			}
			want := "Failed at step 6 (Publish the sites): The site tracker could not be published: the team already has a site named tracker. Nothing was left behind."
			if !strings.Contains(errOut, want) {
				t.Errorf("stderr lacks %q: %q", want, errOut)
			}
			containsAll(t, "stdout", out, "  1. Install the plugins: done", "  5. Copy the tools: done")
			if strings.Contains(out, "Publish the sites: done") || strings.Contains(out, "Installed ") {
				t.Errorf("a failed step is printed done:\n%s", out)
			}
			if len(callsContaining(s, "rm -rf "+installStage)) != 1 {
				t.Errorf("the stage was not removed:\n%s", strings.Join(s.Calls, "\n"))
			}
		})
	}
}

func TestSolutionInstallReasksTheTeamNameWhileTheHostRefusesIt(t *testing.T) {
	for _, program := range programs {
		t.Run(program, func(t *testing.T) {
			tooLong := strings.Repeat("x", 80)
			s := installScript(t, program,
				hostReport(t, "n2", 200, routeBody(t, "solution-preview-name-taken.json", nil)),
				hostReport(t, "n3", 200, routeBody(t, "solution-preview-name-taken.json", func(b map[string]any) {
					b["teamName"], b["nameRefusal"] = tooLong, "A team name is at most 64 characters."
				})),
				hostReport(t, "n4", 200, routeBody(t, "solution-preview-install.json", named("Job Hunt"))),
				hostReport(t, "n5", 200, routeBody(t, "solution-install-done.json", nil)),
			)
			// Blank keeps the refused default, so it is asked again; then too long; then a good one.
			answers := "\n" + tooLong + "\nJob Hunt\n\n\n\n"
			code, out, errOut := run(t, installDeps(s, program, answers), "solution", "install", solutionPackage(t), "--yes")
			if code != 0 {
				t.Fatalf("exit %d: %s %s", code, out, errOut)
			}
			if n := strings.Count(out, "Job Tracker cannot be the team's name: A team named Job Tracker already exists; choose another name."); n != 2 {
				t.Errorf("the default's refusal was shown %d times, want 2:\n%s", n, out)
			}
			containsAll(t, "stdout", out, tooLong+" cannot be the team's name: A team name is at most 64 characters.", "Team name ["+tooLong+"]: ")
			reqs := requests(t, s, program)
			if len(reqs) != 4 || reqs[1].Team == nil || *reqs[1].Team != tooLong || reqs[2].Team == nil || *reqs[2].Team != "Job Hunt" ||
				reqs[3].Action != "install" || *reqs[3].TeamName != "Job Hunt" {
				t.Errorf("requests %+v", reqs)
			}
		})
	}
}

func TestSolutionInstallSkippingARequiredDocumentSaysTheTeamIsBlocked(t *testing.T) {
	for _, program := range programs {
		t.Run(program, func(t *testing.T) {
			s := installScript(t, program,
				hostReport(t, "n2", 200, routeBody(t, "solution-preview-install.json", nil)),
				hostReport(t, "n3", 200, routeBody(t, "solution-install-done.json", func(b map[string]any) {
					b["teamName"] = "Job Tracker"
					b["missing"] = []any{map[string]any{"kind": "document", "name": "Resume", "member": nil,
						"description": "Your reference resume, .docx or PDF. The Writer starts every letter from it."}}
				})),
			)
			code, out, errOut := run(t, installDeps(s, program, "\n\n\n\ny\n"), "solution", "install", solutionPackage(t))
			if code != 0 {
				t.Fatalf("exit %d: %s %s", code, out, errOut)
			}
			containsAll(t, "stdout", out,
				"Documents in Resume/ (required): Your reference resume, .docx or PDF. The Writer starts every letter from it.\n"+
					"  A file on this computer (blank skips):   Skipped: required - the team shows as blocked until it is provided.",
				"Installed Job Tracker 1.0.0 as team Job Tracker.",
				"The team shows as blocked until this is provided:",
				"  Blocked: waiting for Resume - Your reference resume, .docx or PDF. The Writer starts every letter from it.",
			)
			reqs := requests(t, s, program)
			if len(reqs) != 2 || len(reqs[1].Documents) != 0 {
				t.Errorf("requests %+v", reqs)
			}
			if len(callsContaining(s, "/.documents")) != 0 {
				t.Errorf("a skipped document was staged:\n%s", strings.Join(s.Calls, "\n"))
			}
		})
	}
}

func TestSolutionInstallYesSkipsTheConfirmation(t *testing.T) {
	for _, program := range programs {
		t.Run(program, func(t *testing.T) {
			s := installScript(t, program,
				hostReport(t, "n2", 200, routeBody(t, "solution-preview-install.json", nil)),
				hostReport(t, "n3", 200, routeBody(t, "solution-install-done.json", named("Job Tracker"))),
			)
			// No answers at all: every question keeps its default or skips, and nothing asks for a yes.
			code, out, errOut := run(t, installDeps(s, program, ""), "solution", "install", solutionPackage(t), "--yes")
			if code != 0 {
				t.Fatalf("exit %d: %s %s", code, out, errOut)
			}
			if strings.Contains(out, "[y/N]") {
				t.Errorf("--yes asked anyway:\n%s", out)
			}
			reqs := requests(t, s, program)
			if len(reqs) != 2 || reqs[1].Action != "install" || *reqs[1].TeamName != "Job Tracker" {
				t.Errorf("requests %+v", reqs)
			}
		})
	}
}

func TestSolutionInstallDeclinedSendsNothing(t *testing.T) {
	for _, program := range programs {
		t.Run(program, func(t *testing.T) {
			s := installScript(t, program, hostReport(t, "n2", 200, routeBody(t, "solution-preview-install.json", nil)))
			resume := filepath.Join(t.TempDir(), "resume.pdf")
			must(t, os.WriteFile(resume, []byte("%PDF"), 0o644))

			code, out, errOut := run(t, installDeps(s, program, "\n\n\n"+resume+"\nn\n"), "solution", "install", solutionPackage(t))
			if code != 0 {
				t.Fatalf("exit %d: %s %s", code, out, errOut)
			}
			containsAll(t, "stdout", out, "Install Job Tracker 1.0.0 as team Job Tracker? [y/N] ", "nothing was installed")
			reqs := requests(t, s, program)
			if len(reqs) != 1 || reqs[0].Action != "preview" {
				t.Errorf("only the preview may be sent: %+v", reqs)
			}
			if len(callsContaining(s, resume)) != 0 {
				t.Errorf("a declined install copied the document:\n%s", strings.Join(s.Calls, "\n"))
			}
			if len(callsContaining(s, "rm -rf "+installStage)) != 1 {
				t.Errorf("the stage was not removed:\n%s", strings.Join(s.Calls, "\n"))
			}
		})
	}
}

func TestSolutionInstallOnAnImageThatNeverAnswersWithdrawsAndSaysToUpdate(t *testing.T) {
	for _, program := range programs {
		t.Run(program, func(t *testing.T) {
			s := installScript(t, program) // no report ever
			code, _, errOut := run(t, installDeps(s, program, ""), "solution", "install", solutionPackage(t), "--yes")
			if code != 1 || !strings.Contains(errOut, "too old") || !strings.Contains(errOut, "yawble update") || !strings.Contains(errOut, "withdrawn") {
				t.Errorf("exit %d stderr %q", code, errOut)
			}
			if len(callsContaining(s, program+" exec yawble sh -c "+cli.SolutionWithdrawScript+" sh /data/plugins n2")) != 1 {
				t.Errorf("the request was not withdrawn:\n%s", strings.Join(s.Calls, "\n"))
			}
			if len(callsContaining(s, "rm -rf "+installStage)) != 1 {
				t.Errorf("the stage was not removed:\n%s", strings.Join(s.Calls, "\n"))
			}
		})
	}
}

func TestSolutionInstallFromTheInstanceCopiesNoPackage(t *testing.T) {
	s := installScript(t, "podman",
		hostReport(t, "n2", 200, routeBody(t, "solution-preview-install.json", nil)),
		hostReport(t, "n3", 200, routeBody(t, "solution-install-done.json", named("Job Tracker"))),
	)
	from := "/data/documents/acme/job-tracker-1.0.0"
	code, out, errOut := run(t, installDeps(s, "podman", ""), "solution", "install", "--from-instance", from, "--yes")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	reqs := requests(t, s, "podman")
	if len(reqs) != 2 || reqs[0].Folder != from || reqs[1].Folder != from {
		t.Errorf("requests %+v", reqs)
	}
	if len(callsContaining(s, "podman cp")) != 0 || len(callsContaining(s, cli.SolutionInstallStageScript)) != 0 || len(callsContaining(s, "rm -rf")) != 0 {
		t.Errorf("staged anyway:\n%s", strings.Join(s.Calls, "\n"))
	}
}

func TestSolutionInstallOfAPackageThatFailsItsCheckPrintsTheProblems(t *testing.T) {
	refused := routeBody(t, "solution-check-refused.json", nil)
	s := installScript(t, "podman", hostReport(t, "n2", 200, refused))
	code, out, errOut := run(t, installDeps(s, "podman", ""), "solution", "install", solutionPackage(t), "--yes")
	if code != 1 || !strings.Contains(errOut, "does not pass its check: 2 problems") ||
		!strings.Contains(out, "solution.json triggers[1].member:") {
		t.Errorf("exit %d stdout %s stderr %q", code, out, errOut)
	}
	if reqs := requests(t, s, "podman"); len(reqs) != 1 {
		t.Errorf("requests %+v", reqs)
	}
}

func TestSolutionInstallSettingAnswersAreTypedByTheSettingsType(t *testing.T) {
	for _, c := range []struct {
		kind, answer string
		choices      []string
		want         string
	}{
		{"boolean", "y", nil, "true"},
		{"boolean", "false", nil, "false"},
		{"number", "2.5", nil, "2.5"},
		{"integer", "7", nil, "7"},
		{"string[]", "a, b ,c", nil, `["a","b","c"]`},
		{"list", "sample", []string{"sample"}, `["sample"]`},
		{"string", "hello there", nil, `"hello there"`},
	} {
		got, err := cli.ParseSetting(c.kind, c.answer, c.choices)
		b, _ := json.Marshal(got)
		if err != nil || string(b) != c.want {
			t.Errorf("%s %q: %s %v, want %s", c.kind, c.answer, b, err, c.want)
		}
	}
	for _, c := range []struct {
		kind, answer string
		choices      []string
	}{{"boolean", "maybe", nil}, {"integer", "1.5", nil}, {"number", "ten", nil}, {"list", "other", []string{"sample"}}} {
		if _, err := cli.ParseSetting(c.kind, c.answer, c.choices); err == nil {
			t.Errorf("%s %q was accepted", c.kind, c.answer)
		}
	}
}

// The stage, seal, request, report and withdraw scripts, run for real under sh (chown stubbed: it
// needs root and the image's users).
func TestSolutionInstallScriptsUnderSh(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("the scripts run in the Linux container; sh is not on Windows")
	}
	sh, err := exec.LookPath("sh")
	if err != nil {
		t.Skip("no sh")
	}
	stubs := t.TempDir()
	must(t, os.WriteFile(filepath.Join(stubs, "chown"), []byte("#!/bin/sh\nexit 0\n"), 0o755))
	root := filepath.Join(t.TempDir(), "plugins")
	runScript := func(script string, args ...string) string {
		t.Helper()
		cmd := exec.Command(sh, append([]string{"-c", script, "sh"}, args...)...)
		cmd.Env = append(os.Environ(), "PATH="+stubs+string(os.PathListSeparator)+os.Getenv("PATH"))
		out, err := cmd.CombinedOutput()
		if err != nil {
			t.Fatalf("%v: %s", err, out)
		}
		return string(out)
	}
	mode := func(p string) os.FileMode {
		t.Helper()
		info, err := os.Stat(p)
		must(t, err)
		return info.Mode().Perm()
	}

	runScript(cli.SolutionInstallStageScript, root, "abc")
	stage := filepath.Join(root, ".solutions", "abc")
	for _, d := range []string{root, filepath.Join(root, ".solutions"), stage} {
		if m := mode(d); m != 0o750 {
			t.Errorf("%s is %o, want 750", d, m)
		}
	}
	// What `cp` does: the package and a document arrive with this computer's modes.
	must(t, os.CopyFS(filepath.Join(stage, "job-tracker"), os.DirFS(solutionPackage(t))))
	docs := filepath.Join(stage, ".documents", "My Resume")
	runScript(cli.SolutionInstallFolderScript, docs)
	must(t, os.WriteFile(filepath.Join(docs, "resume.pdf"), []byte("%PDF"), 0o666))
	runScript(cli.SolutionInstallSealScript, stage)
	for p, want := range map[string]os.FileMode{
		filepath.Join(stage, "job-tracker"): 0o750, filepath.Join(stage, "job-tracker", "solution.json"): 0o640,
		docs: 0o750, filepath.Join(docs, "resume.pdf"): 0o640,
	} {
		if m := mode(p); m != want {
			t.Errorf("%s is %o, want %o", p, m, want)
		}
	}

	if got := runScript(cli.SolutionReportScript, root); got != "" {
		t.Errorf("no report yet should read empty: %q", got)
	}
	request := `{"request":"abc","action":"preview","folder":"/data/plugins/.solutions/abc/job tracker; rm -rf /"}`
	runScript(cli.SolutionRequestScript, root, request)
	path := filepath.Join(root, ".solution")
	if got, _ := os.ReadFile(path); string(got) != request+"\n" {
		t.Errorf(".solution %q", got)
	}
	if m := mode(path); m != 0o640 {
		t.Errorf(".solution is %o", m)
	}
	must(t, os.WriteFile(filepath.Join(root, ".solution-report.json"), []byte(`{"request":"abc"}`), 0o640))
	if got := runScript(cli.SolutionReportScript, root); got != `{"request":"abc"}` {
		t.Errorf("report %q", got)
	}
	runScript(cli.SolutionWithdrawScript, root, "another")
	if _, err := os.Stat(path); err != nil {
		t.Errorf("withdrew another request: %v", err)
	}
	runScript(cli.SolutionWithdrawScript, root, "abc")
	if _, err := os.Stat(path); !os.IsNotExist(err) {
		t.Errorf("not withdrawn: %v", err)
	}
}

// secretRows are the Host's secrets list: by key name, never a value. needed is nil in a preview
// where it waits on the Scout's sources.
func secretRows(needed map[string]any) []any {
	row := func(key, field, description string, set bool, when any) map[string]any {
		return map[string]any{
			"member": "Scout", "field": field, "key": key, "description": description, "required": false,
			"when": when, "set": set, "needed": needed[key],
			"setWith": "the operator CLI's `secret set " + key + "` (it prompts for the value), then its `up` to restart the Host",
		}
	}
	return []any{
		row("ADZUNA_APP_ID", "adzunaAppId", "Your Adzuna application id.", true, nil),
		row("USAJOBS_API_KEY", "usajobsApiKey", "Your USAJOBS API key.", false, nil),
		row("THEMUSE_API_KEY", "themuseApiKey", "Your The Muse API key.", false, map[string]any{"setting": "sources", "value": "themuse"}),
	}
}

func TestSolutionInstallPrintsEachSecretByKeyAndTheKeysStillUnset(t *testing.T) {
	for _, program := range programs {
		t.Run(program, func(t *testing.T) {
			preview := routeBody(t, "solution-preview-install.json", func(b map[string]any) {
				b["secrets"] = secretRows(map[string]any{"ADZUNA_APP_ID": true, "USAJOBS_API_KEY": true, "THEMUSE_API_KEY": nil})
			})
			done := routeBody(t, "solution-install-done.json", func(b map[string]any) {
				named("Job Tracker")(b)
				b["secrets"] = secretRows(map[string]any{"ADZUNA_APP_ID": true, "USAJOBS_API_KEY": true, "THEMUSE_API_KEY": false})
				b["unset"] = []string{"USAJOBS_API_KEY"}
			})
			s := installScript(t, program, hostReport(t, "n2", 200, preview), hostReport(t, "n3", 200, done))

			// No answers: the sources stay empty, so The Muse's key is not needed.
			code, out, errOut := run(t, installDeps(s, program, ""), "solution", "install", solutionPackage(t), "--yes")
			if code != 0 {
				t.Fatalf("exit %d: %s %s", code, out, errOut)
			}
			containsAll(t, "the output", out,
				"Secrets (bound by key name; values are set on the Host, never here):",
				"  ADZUNA_APP_ID (Scout): set on this Host.",
				"      Your Adzuna application id.",
				"  USAJOBS_API_KEY (Scout): not set - its source fails until it is set. Set it with: yawble secret set USAJOBS_API_KEY (it prompts for the value), then yawble up to restart the Host.",
				"  THEMUSE_API_KEY (Scout): not needed - Scout's sources leaves themuse off.",
				"These keys are still not set on the Host. Each one's source fails until it is set:",
				"  USAJOBS_API_KEY - yawble secret set USAJOBS_API_KEY (it prompts for the value), then yawble up to restart the Host",
			)
			// Printed twice: before the install (Your part) and in the result.
			if n := strings.Count(out, "Secrets (bound by key name"); n != 2 {
				t.Errorf("the secrets list was printed %d times, want 2:\n%s", n, out)
			}
			if strings.Contains(out, "  THEMUSE_API_KEY - ") || strings.Contains(out, "  ADZUNA_APP_ID - ") {
				t.Errorf("a set or unneeded key is listed as still to set:\n%s", out)
			}
		})
	}
}

func TestSolutionInstallNamesEachSchedulesFirstRun(t *testing.T) {
	now := time.Now()
	evening := time.Date(now.Year(), now.Month(), now.Day(), 20, 51, 0, 0, time.Local).UTC().Format(time.RFC3339)
	tomorrow := time.Date(now.Year(), now.Month(), now.Day()+1, 8, 0, 0, 0, time.Local)
	for _, program := range programs {
		t.Run(program, func(t *testing.T) {
			preview := routeBody(t, "solution-preview-install.json", func(b map[string]any) {
				trigger := b["plan"].(map[string]any)["triggers"].([]any)[0].(map[string]any)
				trigger["runAtInstall"] = true
				trigger["schedule"] = "runs once now, then every 3600 seconds"
			})
			done := routeBody(t, "solution-install-done.json", func(b map[string]any) {
				named("Job Tracker")(b)
				b["firstRuns"] = []map[string]any{
					{"trigger": "Scan for postings", "member": "Scout", "runAtInstall": true, "ranNow": true, "outcome": "fired", "at": evening},
					{"trigger": "Morning summary", "member": "Coordinator", "runAtInstall": false, "ranNow": false, "outcome": "scheduled", "at": evening},
					{"trigger": "Evening summary", "member": "Coordinator", "runAtInstall": true, "ranNow": false, "outcome": "capped", "at": tomorrow.UTC().Format(time.RFC3339)},
				}
			})
			s := installScript(t, program, hostReport(t, "n2", 200, preview), hostReport(t, "n3", 200, done))

			code, out, errOut := run(t, installDeps(s, program, ""), "solution", "install", solutionPackage(t), "--yes")
			if code != 0 {
				t.Fatalf("exit %d: %s %s", code, out, errOut)
			}
			containsAll(t, "the output", out,
				"  Scan for postings: runs once now, then every 3600 seconds, wakes Scout;",
				"Schedules:",
				"  Scan for postings ran now.",
				"  Morning summary first runs at 8:51 PM.",
				"  Evening summary did not run now (capped); it first runs at "+tomorrow.Format("Mon")+" 8:00 AM.",
			)
		})
	}
}
