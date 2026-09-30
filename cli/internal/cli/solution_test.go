package cli_test

import (
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/cli"
	"github.com/djlsystems/yawble/cli/internal/engine"
)

// The Host's answers, recorded from the real Host's --solution-check over the Job Tracker sample and
// a broken copy of it; SolutionCheckCliFixtureTests in the .NET suite keeps them equal to what the
// Host prints today.
func hostAnswer(t *testing.T, name string) engine.Result {
	t.Helper()
	b, err := os.ReadFile(filepath.Join("testdata", name))
	must(t, err)
	// The Host prints its switch's report as the last line, after anything it logged.
	return engine.Result{Stdout: "info: starting\n" + string(b)}
}

const stage = "/tmp/yawble-solution-check/" + nonce

// solutionPackage is a folder holding solution.json: the CLI checks only that much itself.
func solutionPackage(t *testing.T) string {
	t.Helper()
	dir := filepath.Join(t.TempDir(), "job-tracker")
	must(t, os.MkdirAll(dir, 0o755))
	must(t, os.WriteFile(filepath.Join(dir, "solution.json"), []byte(`{"format":1}`), 0o644))
	return dir
}

func solutionScript(t *testing.T, program string, answer engine.Result) *engine.Scripted {
	t.Cleanup(cli.FastRescan(nonce))
	var s *engine.Scripted
	if program == "podman" {
		s = runningScript()
	} else {
		s = engine.NewScripted()
		s.On("docker version", engine.Result{Stdout: "27.1.0\n"})
		s.On("docker container inspect", engine.Result{Stdout: "running|" + testImage + "|\n"})
	}
	s.On(program+" exec yawble dotnet /app/Harness.Host.dll --solution-check", answer)
	return s
}

func depsFor(s *engine.Scripted, program string) cli.Deps {
	deps := stubbed(s)
	if program == "docker" {
		env := map[string]string{"YAWBLE_IMAGE": testImage, "YAWBLE_ENGINE": "docker"}
		deps.Env = func(k string) string { return env[k] }
		deps.LookPath = lookPath("docker")
	}
	return deps
}

func TestSolutionCheckCopiesThePackageInAsksTheHostPrintsThePlanAndCleansUp(t *testing.T) {
	for _, program := range []string{"podman", "docker"} {
		t.Run(program, func(t *testing.T) {
			folder := solutionPackage(t)
			s := solutionScript(t, program, hostAnswer(t, "solution-check-job-tracker.json"))

			code, out, errOut := run(t, depsFor(s, program), "solution", "check", folder)
			if code != 0 {
				t.Fatalf("exit %d: %s %s\n%s", code, out, errOut, strings.Join(s.Calls, "\n"))
			}

			// In order: an empty stage, the package copied into it, the Host's check of the copy, the
			// stage removed.
			want := []string{
				program + " exec yawble sh -c " + cli.SolutionStageScript + " sh " + stage,
				program + " cp " + folder + " yawble:" + stage + "/package",
				program + " exec yawble dotnet /app/Harness.Host.dll --solution-check " + stage + "/package",
				program + " exec yawble rm -rf " + stage,
			}
			at := 0
			for _, c := range s.Calls {
				if at < len(want) && c == want[at] {
					at++
				}
			}
			if at != len(want) {
				t.Fatalf("calls out of order; wanted %q next, got:\n%s", want[at], strings.Join(s.Calls, "\n"))
			}

			for _, line := range []string{
				"Job Tracker 1.0.0 (job-tracker) passes its check. Installing " + folder + " would create:",
				"Team: Job Tracker",
				"  Coordinator: agent, manager, preset chosen by the team",
				"  Scout: plugin job-board 0.1.0",
				`    setting keywords = ["engineer","developer"]`,
				"  job-board 0.1.0 (Job Board (sample)), publishes plugin.job-board.posting-found",
				"  Scan for postings: runs once now, then every 3600 seconds, wakes Scout; wakes the Manager: never; no daily cap",
				"  Apply pressed: on site.action where siteAction eq tracker/apply, wakes Writer; wakes the Manager: onHandbackOrFailure; daily cap 400,000 tokens",
				"  Resume changed: when files change in documents/Resume matching *, wakes Writer; wakes the Manager: onHandbackOrFailure; daily cap 300,000 tokens",
				"  Morning summary: cron 0 0 8 * * 1-5 (Europe/London), wakes Coordinator; wakes the Manager: never; daily cap 100,000 tokens",
				"  job-search-playbook (for manager, member): Use when working the Job Tracker team's search",
				"  tracker (3 files)",
				"Tools: tools/ copied to the team's solution/ folder, named {solution} in instructions (1 file): make-cover-letter.py",
				"  documents in Resume/ (required): Your reference resume, .docx or PDF.",
				"  Scout's setting sources (optional): Which job boards the Scout may read.",
			} {
				if !strings.Contains(out, line) {
					t.Errorf("the plan lacks %q:\n%s", line, out)
				}
			}

			// EVERY INSTRUCTION WHOLE: it becomes a prompt, so it is read before anything runs.
			if !strings.Contains(out, "    Instruction: {event.by} pressed Apply on {event.payload}. Run `python3 {solution}/make-cover-letter.py` with that posting's JSON and the resume's path to get a first draft, then finish the letter and save it as Drafts/<job id>.md in the team's documents. Set the posting's status to `drafted` on the tracker and hand back naming the file.\n") {
				t.Errorf("the Apply pressed instruction is not printed whole:\n%s", out)
			}
		})
	}
}

func TestSolutionCheckPrintsEachProblemWithItsFileAndFieldAndExitsOne(t *testing.T) {
	for _, program := range []string{"podman", "docker"} {
		t.Run(program, func(t *testing.T) {
			folder := solutionPackage(t)
			s := solutionScript(t, program, hostAnswer(t, "solution-check-refused.json"))

			code, out, errOut := run(t, depsFor(s, program), "solution", "check", folder)
			if code != 1 {
				t.Fatalf("exit %d: %s %s", code, out, errOut)
			}
			for _, line := range []string{
				folder + " does not pass its check (2 problems):",
				"  solution.json triggers[1].member: `triggers[1].member` 'Recruiter' names no member of this package; its members are Coordinator, Scout, Writer.",
				"  solution.json triggers[2].dailyTokenCap: `triggers[2].dailyTokenCap` must be a whole number of tokens, at least 1, or null for no cap.",
			} {
				if !strings.Contains(out, line) {
					t.Errorf("stdout lacks %q:\n%s", line, out)
				}
			}
			if !strings.Contains(errOut, "does not pass its check: 2 problems") {
				t.Errorf("stderr %q", errOut)
			}
			if len(callsContaining(s, "rm -rf "+stage)) != 1 {
				t.Errorf("the stage was not removed:\n%s", strings.Join(s.Calls, "\n"))
			}
		})
	}
}

func TestSolutionCheckFromTheInstanceCopiesNothing(t *testing.T) {
	s := solutionScript(t, "podman", hostAnswer(t, "solution-check-job-tracker.json"))

	code, out, errOut := run(t, stubbed(s), "solution", "check", "--from-instance", "/data/documents/acme/job-tracker-1.0.0")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if len(callsContaining(s, "podman exec yawble dotnet /app/Harness.Host.dll --solution-check /data/documents/acme/job-tracker-1.0.0")) != 1 ||
		len(callsContaining(s, "podman cp")) != 0 || len(callsContaining(s, "rm -rf")) != 0 {
		t.Errorf("calls:\n%s", strings.Join(s.Calls, "\n"))
	}
}

func TestSolutionCheckJSONPrintsTheHostsAnswer(t *testing.T) {
	s := solutionScript(t, "podman", hostAnswer(t, "solution-check-refused.json"))

	code, out, _ := run(t, stubbed(s), "solution", "check", "--json", solutionPackage(t))
	if code != 1 || !strings.Contains(out, `"field": "triggers[1].member"`) || !strings.Contains(out, `"ok": false`) {
		t.Errorf("exit %d:\n%s", code, out)
	}
}

func TestSolutionCheckRefusesAFolderWithoutSolutionJSONBeforeTouchingTheEngine(t *testing.T) {
	s := solutionScript(t, "podman", engine.Result{})
	empty := t.TempDir()

	code, _, errOut := run(t, stubbed(s), "solution", "check", empty)
	if code != 1 || !strings.Contains(errOut, "holds no solution.json") || len(s.Calls) != 0 {
		t.Errorf("exit %d stderr %q calls %q", code, errOut, s.Calls)
	}

	code, _, errOut = run(t, stubbed(s), "solution", "check", filepath.Join(empty, "missing"))
	if code != 1 || !strings.Contains(errOut, "is not a folder on this computer") {
		t.Errorf("exit %d stderr %q", code, errOut)
	}

	code, _, errOut = run(t, stubbed(s), "solution", "check", "--from-instance", "relative/path")
	if code != 1 || !strings.Contains(errOut, "absolute path inside the instance") {
		t.Errorf("exit %d stderr %q", code, errOut)
	}
}

func TestSolutionCheckOnAStoppedInstanceSaysToStartIt(t *testing.T) {
	s := stoppedScript()

	code, _, errOut := run(t, stubbed(s), "solution", "check", solutionPackage(t))
	if code != 1 || !strings.Contains(errOut, "yawble up") || len(callsContaining(s, "exec")) != 0 {
		t.Errorf("exit %d stderr %q", code, errOut)
	}
}

func TestSolutionCheckOnAnImageThatPredatesItSaysToUpdate(t *testing.T) {
	s := solutionScript(t, "podman", engine.Result{ExitCode: 1, Stderr: "Another host is already using this data root."})

	code, _, errOut := run(t, stubbed(s), "solution", "check", solutionPackage(t))
	if code != 1 || !strings.Contains(errOut, "yawble update") {
		t.Errorf("exit %d stderr %q", code, errOut)
	}
	if len(callsContaining(s, "rm -rf "+stage)) != 1 {
		t.Errorf("the stage was not removed:\n%s", strings.Join(s.Calls, "\n"))
	}
}

func TestSolutionCheckSaysSoWhenTheHostAnswersSomethingElse(t *testing.T) {
	s := solutionScript(t, "podman", engine.Result{Stdout: "Operator commands (the host does not serve when one is given):\n"})

	code, _, errOut := run(t, stubbed(s), "solution", "check", solutionPackage(t))
	if code != 1 || !strings.Contains(errOut, "cannot read") {
		t.Errorf("exit %d stderr %q", code, errOut)
	}
}
