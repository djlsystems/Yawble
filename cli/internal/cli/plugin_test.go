package cli_test

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
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

const (
	nonce       = "n0nce"
	execPrefix  = "podman exec -e HARNESS_WORKER_KEY= yawble sh -c "
	prepareCall = execPrefix + cli.PluginPrepareScript + " sh /data/plugins "
	pluginStage = "/data/plugins/.incoming/sample-echo-0.1.0"
	stageCall   = execPrefix + cli.PluginStageScript + " sh " + pluginStage + " sample-echo"
	requestCall = execPrefix + cli.PluginRequestScript + " sh /data/plugins " + nonce
	layoutCall  = execPrefix + cli.PluginLayoutScript + " sh /data/plugins"
	reportCall  = execPrefix + cli.PluginReportScript + " sh /data/plugins"
)

const echoManifest = `{
  // The Host skips comments and trailing commas; so does the install.
  "schemaVersion": 1,
  "id": "sample-echo",
  "name": "Sample Echo",
  "description": "Test.",
  "version": "0.1.0",
  "protocol": "harness.member/1",
  "executable": { "path": "sample-echo", "args": [] },
  "config": { "mode": { "type": "string", "enum": ["upper", "reverse"], "default": "upper" } },
  "secrets": { "token": { "description": "x", "required": false } },
  "skills": ["skills/sample-echo.md"],
  "platforms": null,
}`

// builtPlugin lays out one built version the way a Windows copy arrives: the launcher has NO
// execute bit. edit may change the manifest text.
func builtPlugin(t *testing.T, edit func(string) string) string {
	t.Helper()
	dir := filepath.Join(t.TempDir(), "sample-echo", "0.1.0")
	must(t, os.MkdirAll(filepath.Join(dir, "skills"), 0o755))
	must(t, os.MkdirAll(filepath.Join(dir, "lib"), 0o755))
	manifest := echoManifest
	if edit != nil {
		manifest = edit(manifest)
	}
	must(t, os.WriteFile(filepath.Join(dir, "plugin.json"), []byte(manifest), 0o644))
	must(t, os.WriteFile(filepath.Join(dir, "sample-echo"), []byte("#!/bin/sh\nexec dotnet lib/SampleEcho.dll\n"), 0o644))
	must(t, os.WriteFile(filepath.Join(dir, "skills", "sample-echo.md"), []byte("---\nname: x\n---\n"), 0o644))
	must(t, os.WriteFile(filepath.Join(dir, "lib", "SampleEcho.dll"), []byte("x"), 0o644))
	return dir
}

func must(t *testing.T, err error) {
	t.Helper()
	if err != nil {
		t.Fatal(err)
	}
}

func report(plugins, refused, members string) engine.Result {
	return engine.Result{Stdout: `{"request":"` + nonce + `","plugins":[` + plugins + `],"refused":[` + refused + `],"members":[` + members + `]}` + "\n"}
}

const echoInstalled = `{"id":"sample-echo","version":"0.1.0","name":"Sample Echo"}`

// pluginScript is a running instance whose Host answers each rescan with reports, in turn; by
// default, sample-echo 0.1.0 installed.
func pluginScript(t *testing.T, reports ...engine.Result) *engine.Scripted {
	t.Cleanup(cli.FastRescan(nonce))
	s := runningScript()
	s.On(prepareCall, engine.Result{Stdout: "ready\n"})
	if len(reports) == 0 {
		reports = []engine.Result{report(echoInstalled, "", "")}
	}
	s.OnSequence(reportCall, reports...)
	return s
}

func callsContaining(s *engine.Scripted, part string) []string {
	var found []string
	for _, c := range s.Calls {
		if strings.Contains(c, part) {
			found = append(found, c)
		}
	}
	return found
}

// directScript is a running instance whose Host answers the install request for the staging folder
// with answers in turn; by default, sample-echo 0.1.0 installed.
func directScript(t *testing.T, answers ...engine.Result) *engine.Scripted {
	s := pluginScript(t)
	if len(answers) == 0 {
		answers = []engine.Result{installAnswer(nonce, 200, true, false, "")}
	}
	s.OnSequence(installReportCall, answers...)
	return s
}

func TestPluginInstallCopiesToTheStageAndTheHostInstallsItAndPrintsItsVerdict(t *testing.T) {
	folder := builtPlugin(t, nil)
	if runtime.GOOS != "windows" {
		if info, _ := os.Stat(filepath.Join(folder, "sample-echo")); info.Mode()&0o111 != 0 {
			t.Fatal("the fixture's launcher must have no execute bit")
		}
	}
	s := directScript(t)
	code, out, errOut := run(t, stubbed(s), "plugin", "install", folder)
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}

	// In order: check the version is free, copy to the staging folder, give it the Host's owner and
	// modes, ask the Host's installer to install it from there, read its answer, take the stage away.
	want := []string{
		prepareCall + "sample-echo 0.1.0 0",
		"podman cp " + folder + " yawble:" + pluginStage,
		stageCall,
		installRequestFor(pluginStage, false),
		installReportCall,
		"podman exec -e HARNESS_WORKER_KEY= yawble rm -rf " + pluginStage,
	}
	at := 0
	for _, c := range s.Calls {
		if at < len(want) && c == want[at] {
			at++
		}
	}
	if at != len(want) {
		t.Fatalf("missing, in order, %q\ncalls:\n%s", want[at], strings.Join(s.Calls, "\n"))
	}

	// The CLI never makes a version active itself: the Host's installer does, with every check.
	for _, script := range []string{cli.PluginPrepareScript, cli.PluginStageScript} {
		if strings.Contains(script, "active") {
			t.Errorf("a script writes active itself:\n%s", script)
		}
	}
	// The stage script sets every mode itself, so a launcher copied without its execute bit
	// (from Windows) is executable, and the Host can read what root copied.
	for _, line := range []string{
		`chown -R -h harness:agent "$s"`,
		`find "$s" -type d -exec chmod u=rwx,g=rx,o=,ug-s {} +`,
		`find "$s" ! -type d ! -type l -exec chmod 0640 {} +`,
		`for x in "$@"; do chmod 0750 "$s/$x"; done`,
	} {
		if !strings.Contains(cli.PluginStageScript, line) {
			t.Errorf("the stage script lacks %q", line)
		}
	}
	for _, want := range []string{"copied sample-echo 0.1.0 into the instance", "the Host installed sample-echo 0.1.0 from " + folder + " and made it the active version", "plugin:sample-echo"} {
		if !strings.Contains(out, want) {
			t.Errorf("output lacks %q:\n%s", want, out)
		}
	}
}

func TestPluginInstallRefusesAnInstalledVersionUnlessForced(t *testing.T) {
	folder := builtPlugin(t, nil)
	s := directScript(t)
	s.On(prepareCall+"sample-echo 0.1.0 0", engine.Result{Stdout: "exists\n"})
	code, _, errOut := run(t, stubbed(s), "plugin", "install", folder)
	if code != 1 || !strings.Contains(errOut, "already installed") || !strings.Contains(errOut, "--force") {
		t.Errorf("exit %d stderr %q", code, errOut)
	}
	if len(callsContaining(s, "podman cp")) != 0 {
		t.Errorf("copied although refused:\n%s", strings.Join(s.Calls, "\n"))
	}

	s = directScript(t, installAnswer(nonce, 200, true, true, ""))
	code, out, errOut := run(t, stubbed(s), "plugin", "install", folder, "--force")
	if code != 0 || len(callsContaining(s, prepareCall+"sample-echo 0.1.0 1")) != 1 || len(callsContaining(s, "podman cp")) != 1 ||
		len(callsContaining(s, installRequestFor(pluginStage, true))) != 1 || !strings.Contains(out, "the Host replaced sample-echo 0.1.0") {
		t.Errorf("--force: exit %d %s %s\n%s", code, out, errOut, strings.Join(s.Calls, "\n"))
	}
}

// Each manifest refusal names the field, as the Host would, and nothing reaches the engine.
func TestPluginInstallRefusesABadFolderByTheFieldBeforeCopying(t *testing.T) {
	cases := []struct {
		name, want string
		edit       func(string) string
		folder     func(string) string
	}{
		{"no manifest", "has no plugin.json", nil, func(d string) string { must(t, os.Remove(filepath.Join(d, "plugin.json"))); return d }},
		{"not json", "plugin.json is not JSON", func(string) string { return "{" }, nil},
		{"schema", "`schemaVersion` 2 is not one this Host reads", func(m string) string { return strings.Replace(m, `"schemaVersion": 1`, `"schemaVersion": 2`, 1) }, nil},
		{"id", "`id` 'Sample_Echo' must be lowercase", func(m string) string { return strings.Replace(m, `"id": "sample-echo"`, `"id": "Sample_Echo"`, 1) }, nil},
		{"no version", "`version` is required", func(m string) string { return strings.Replace(m, `"version": "0.1.0",`, "", 1) }, nil},
		{"protocol", "`protocol` 'other/9' is not one this Host speaks", func(m string) string { return strings.Replace(m, "harness.member/1", "other/9", 1) }, nil},
		{"escape", "`executable.path` '../x' must be a relative path", func(m string) string { return strings.Replace(m, `"path": "sample-echo"`, `"path": "../x"`, 1) }, nil},
		{"missing executable", "its executable bin/run does not exist", func(m string) string { return strings.Replace(m, `"path": "sample-echo"`, `"path": "bin/run"`, 1) }, nil},
		{"secret value", "`secrets.token` carries a value", func(m string) string { return strings.Replace(m, `"required": false`, `"value": "sk"`, 1) }, nil},
		{"bad default", "`mode` must be one of: upper, reverse", func(m string) string { return strings.Replace(m, `"default": "upper"`, `"default": "lower"`, 1) }, nil},
		{"missing skill", "its skill skills/sample-echo.md does not exist", nil, func(d string) string { must(t, os.Remove(filepath.Join(d, "skills", "sample-echo.md"))); return d }},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			folder := builtPlugin(t, c.edit)
			if c.folder != nil {
				folder = c.folder(folder)
			}
			s := pluginScript(t)
			code, _, errOut := run(t, stubbed(s), "plugin", "install", folder)
			if code != 1 || !strings.Contains(errOut, c.want) {
				t.Errorf("exit %d stderr %q, want %q", code, errOut, c.want)
			}
			if len(s.Calls) != 0 {
				t.Errorf("the engine was called before the refusal:\n%s", strings.Join(s.Calls, "\n"))
			}
		})
	}
}

// An executable that is a symlink out of the folder is refused, as the catalog refuses it.
func TestPluginInstallRefusesAnExecutableThatResolvesOutsideTheFolder(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("symlinks need privileges on Windows")
	}
	folder := builtPlugin(t, nil)
	must(t, os.Remove(filepath.Join(folder, "sample-echo")))
	must(t, os.Symlink("/bin/sh", filepath.Join(folder, "sample-echo")))
	s := pluginScript(t)
	code, _, errOut := run(t, stubbed(s), "plugin", "install", folder)
	if code != 1 || !strings.Contains(errOut, "resolves outside") || len(s.Calls) != 0 {
		t.Errorf("exit %d stderr %q calls %q", code, errOut, s.Calls)
	}
}

// The Host's refusal is printed in its words and exits 1, and the stage goes all the same: the
// minimum an installed solution team needs, and a manifest the catalog refuses once written.
func TestPluginInstallPrintsTheHostsRefusalAndExitsOne(t *testing.T) {
	folder := builtPlugin(t, nil)
	for name, c := range map[string]struct {
		answer engine.Result
		want   string
	}{
		"older than a solution team needs": {installAnswer(nonce, 409, false, false, "sample-echo 0.1.0 is older than what Fixture Pack 1.0.0 on team Inbox needs (0.2.0 or newer)."),
			folder + " was not installed: sample-echo 0.1.0 is older than what Fixture Pack 1.0.0 on team Inbox needs (0.2.0 or newer)."},
		"refused once written": {installAnswer(nonce, 200, false, false, "its executable sample-echo is not executable (chmod +x it)."),
			"the Host wrote sample-echo 0.1.0 but refuses it: its executable sample-echo is not executable"},
	} {
		t.Run(name, func(t *testing.T) {
			s := directScript(t, c.answer)
			code, _, errOut := run(t, stubbed(s), "plugin", "install", folder)
			if code != 1 || !strings.Contains(errOut, c.want) {
				t.Errorf("exit %d stderr %q, want %q", code, errOut, c.want)
			}
			if len(callsContaining(s, "rm -rf "+pluginStage)) != 1 {
				t.Errorf("the stage was not taken away:\n%s", strings.Join(s.Calls, "\n"))
			}
		})
	}
}

// The report must answer THIS request: an older report is not the Host's verdict on this install,
// and a request no Host answers is withdrawn.
func TestPluginInstallWaitsForTheReportThatAnswersItsRequestAndSaysSoWhenNoneComes(t *testing.T) {
	folder := builtPlugin(t, nil)
	s := directScript(t, installAnswer("older", 200, true, false, ""))
	code, _, errOut := run(t, stubbed(s), "plugin", "install", folder)
	if code != 1 || !strings.Contains(errOut, "did not answer the install request") {
		t.Errorf("exit %d stderr %q", code, errOut)
	}
	if len(callsContaining(s, execPrefix+cli.PluginInstallWithdrawScript+" sh /data/plugins "+nonce)) != 1 || len(callsContaining(s, "rm -rf "+pluginStage)) != 1 {
		t.Errorf("the request was not withdrawn, or the stage was left:\n%s", strings.Join(s.Calls, "\n"))
	}

	s = directScript(t, installAnswer("older", 200, true, false, ""), engine.Result{}, installAnswer(nonce, 200, true, false, ""))
	code, out, errOut := run(t, stubbed(s), "plugin", "install", folder)
	if code != 0 || len(callsContaining(s, installReportCall)) != 3 || !strings.Contains(out, "installed") {
		t.Errorf("exit %d %s %s\n%s", code, out, errOut, strings.Join(s.Calls, "\n"))
	}
}

func TestPluginInstallOnAStoppedInstanceSaysUpFirst(t *testing.T) {
	folder := builtPlugin(t, nil)
	t.Cleanup(cli.FastRescan(nonce))
	s := engine.NewScripted()
	s.On("podman version", engine.Result{Stdout: "6.0.2\n"})
	s.On("podman container inspect", engine.Result{Stdout: "exited|" + testImage + "|" + currentLabel() + "\n"})
	code, _, errOut := run(t, stubbed(s), "plugin", "install", folder)
	if code != 1 || !strings.Contains(errOut, "yawble up") || len(callsContaining(s, "exec")) != 0 {
		t.Errorf("exit %d stderr %q", code, errOut)
	}
}

func TestPluginInstallOnDockerCopiesWithDocker(t *testing.T) {
	folder := builtPlugin(t, nil)
	t.Cleanup(cli.FastRescan(nonce))
	s := engine.NewScripted()
	s.On("docker version", engine.Result{Stdout: "27.1.0\n"})
	s.On("docker container inspect", engine.Result{Stdout: "running|" + testImage + "|\n"})
	s.On("docker exec -e HARNESS_WORKER_KEY= yawble sh -c "+cli.PluginPrepareScript, engine.Result{Stdout: "ready\n"})
	s.On("docker exec -e HARNESS_WORKER_KEY= yawble sh -c "+cli.PluginInstallReportScript, installAnswer(nonce, 200, true, false, ""))
	deps := stubbed(s)
	env := map[string]string{"YAWBLE_IMAGE": testImage, "YAWBLE_ENGINE": "docker"}
	deps.Env = func(k string) string { return env[k] }
	deps.LookPath = lookPath("docker")
	code, out, errOut := run(t, deps, "plugin", "install", folder)
	if code != 0 || len(callsContaining(s, "docker cp "+folder+" yawble:"+pluginStage)) != 1 {
		t.Errorf("exit %d %s %s\n%s", code, out, errOut, strings.Join(s.Calls, "\n"))
	}
}

func TestPluginListShowsEachVersionActiveOrNotAndTheHostsVerdict(t *testing.T) {
	s := pluginScript(t, report(`{"id":"sample-echo","version":"0.2.0"}`, `{"id":"broken","reason":"`+"`protocol` 'x' is not one this Host speaks"+`"},{"id":"Bad_Dir","reason":"the directory name is not a plugin id."}`, ""))
	s.On(layoutCall, engine.Result{Stdout: "sample-echo\t0.2.0\t0.1.0\t0.2.0\nbroken\t\t1.0\n"})
	code, out, errOut := run(t, stubbed(s), "plugin", "list")
	if code != 0 {
		t.Fatalf("exit %d %s %s", code, out, errOut)
	}
	if len(callsContaining(s, requestCall)) != 1 {
		t.Errorf("list did not ask the Host to rescan:\n%s", strings.Join(s.Calls, "\n"))
	}
	lines := strings.Split(strings.TrimSpace(out), "\n")
	want := [][]string{
		{"PLUGIN", "VERSION", "ACTIVE", "STATE"},
		{"Bad_Dir", "-", "no", "refused: the directory name is not a plugin id."},
		{"broken", "1.0", "yes", "refused: `protocol` 'x' is not one this Host speaks"},
		{"sample-echo", "0.1.0", "no", "kept"},
		{"sample-echo", "0.2.0", "yes", "installed"},
	}
	if len(lines) != len(want) {
		t.Fatalf("got\n%s", out)
	}
	for i, fields := range want {
		for _, f := range fields {
			if !strings.Contains(lines[i], f) {
				t.Errorf("line %d %q lacks %q", i, lines[i], f)
			}
		}
	}

	s = pluginScript(t)
	s.On(layoutCall, engine.Result{Stdout: "sample-echo\t0.1.0\t0.1.0\n"})
	_, out, _ = run(t, stubbed(s), "plugin", "list", "--json")
	var rows []map[string]any
	if err := json.Unmarshal([]byte(out), &rows); err != nil || len(rows) != 1 || rows[0]["active"] != true || rows[0]["state"] != "installed" {
		t.Errorf("json %v %s", err, out)
	}
}

func TestPluginListWithNothingInstalledSaysHowToInstall(t *testing.T) {
	s := pluginScript(t, report("", "", ""))
	code, out, _ := run(t, stubbed(s), "plugin", "list")
	if code != 0 || !strings.Contains(out, "yawble plugin install") {
		t.Errorf("exit %d %s", code, out)
	}
}

func TestPluginRemoveAsksFirstAndYesAnswers(t *testing.T) {
	s := pluginScript(t)
	s.On(layoutCall, engine.Result{Stdout: "sample-echo\t0.1.0\t0.1.0\n"})
	code, _, errOut := run(t, stubbed(s), "plugin", "remove", "sample-echo")
	if code != 2 || !strings.Contains(errOut, "--yes") || len(callsContaining(s, "rm -rf")) != 0 {
		t.Errorf("without a terminal or --yes: exit %d %q", code, errOut)
	}

	s = pluginScript(t)
	s.On(layoutCall, engine.Result{Stdout: "sample-echo\t0.1.0\t0.1.0\n"})
	deps := stubbed(s)
	deps.Interactive, deps.Stdin = true, strings.NewReader("n\n")
	code, out, _ := run(t, deps, "plugin", "remove", "sample-echo")
	if code != 0 || !strings.Contains(out, "nothing was removed") || len(callsContaining(s, "rm -rf")) != 0 {
		t.Errorf("answered no: exit %d %q", code, out)
	}

	for _, flag := range []string{"--yes", "-y"} {
		s = pluginScript(t, report(echoInstalled, "", ""), report("", "", ""))
		s.On(layoutCall, engine.Result{Stdout: "sample-echo\t0.1.0\t0.1.0\n"})
		code, out, errOut = run(t, stubbed(s), "plugin", "remove", "sample-echo", flag)
		if code != 0 || len(callsContaining(s, "podman exec -e HARNESS_WORKER_KEY= yawble rm -rf -- /data/plugins/sample-echo")) != 1 || !strings.Contains(out, "the Host no longer lists sample-echo") {
			t.Errorf("%s: exit %d %s %s\n%s", flag, code, out, errOut, strings.Join(s.Calls, "\n"))
		}
	}
}

func TestPluginRemoveRefusesWhileAMemberIsHiredOnItNamingTheMembers(t *testing.T) {
	s := pluginScript(t, report(echoInstalled, "", `{"plugin":"sample-echo","team":"alpha","member":"Echo"},{"plugin":"other","team":"alpha","member":"X"},{"plugin":"sample-echo","team":"beta","member":"Mirror"}`))
	s.On(layoutCall, engine.Result{Stdout: "sample-echo\t0.1.0\t0.1.0\n"})
	code, _, errOut := run(t, stubbed(s), "plugin", "remove", "sample-echo", "--yes")
	if code != 1 || !strings.Contains(errOut, "in use by alpha/Echo, beta/Mirror") || strings.Contains(errOut, "alpha/X") {
		t.Errorf("exit %d stderr %q", code, errOut)
	}
	if len(callsContaining(s, "rm -rf")) != 0 {
		t.Errorf("removed although hired on:\n%s", strings.Join(s.Calls, "\n"))
	}
}

func TestPluginRemoveOfOneVersion(t *testing.T) {
	// A kept version goes without asking the Host who is hired: no member runs it.
	s := pluginScript(t, report(`{"id":"sample-echo","version":"0.2.0"}`, "", `{"plugin":"sample-echo","team":"alpha","member":"Echo"}`))
	s.On(layoutCall, engine.Result{Stdout: "sample-echo\t0.2.0\t0.1.0\t0.2.0\n"})
	code, out, errOut := run(t, stubbed(s), "plugin", "remove", "sample-echo", "--version", "0.1.0", "--yes")
	if code != 0 || len(callsContaining(s, "rm -rf -- /data/plugins/sample-echo/0.1.0")) != 1 || !strings.Contains(out, "the Host reports sample-echo 0.2.0 installed") {
		t.Errorf("kept version: exit %d %s %s\n%s", code, out, errOut, strings.Join(s.Calls, "\n"))
	}

	// The active version, while others are kept, is refused: members would be left on nothing.
	s = pluginScript(t)
	s.On(layoutCall, engine.Result{Stdout: "sample-echo\t0.2.0\t0.1.0\t0.2.0\n"})
	code, _, errOut = run(t, stubbed(s), "plugin", "remove", "sample-echo", "--version", "0.2.0", "--yes")
	if code != 1 || !strings.Contains(errOut, "is the active version") || len(callsContaining(s, "rm -rf")) != 0 {
		t.Errorf("active version: exit %d %q", code, errOut)
	}

	s = pluginScript(t)
	s.On(layoutCall, engine.Result{Stdout: "sample-echo\t0.2.0\t0.1.0\t0.2.0\n"})
	code, _, errOut = run(t, stubbed(s), "plugin", "remove", "sample-echo", "--version", "9.9", "--yes")
	if code != 1 || !strings.Contains(errOut, "has no version 9.9 (it has: 0.1.0, 0.2.0)") {
		t.Errorf("unknown version: exit %d %q", code, errOut)
	}
}

func TestPluginRemoveOfAPluginThatIsNotInstalledOrNotAnID(t *testing.T) {
	s := pluginScript(t)
	s.On(layoutCall, engine.Result{Stdout: "other\t1.0\t1.0\n"})
	code, _, errOut := run(t, stubbed(s), "plugin", "remove", "sample-echo", "--yes")
	if code != 1 || !strings.Contains(errOut, "plugin sample-echo is not installed") {
		t.Errorf("exit %d %q", code, errOut)
	}

	s = pluginScript(t)
	code, _, errOut = run(t, stubbed(s), "plugin", "remove", "../etc", "--yes")
	if code != 2 || !strings.Contains(errOut, "is not a plugin id") || len(s.Calls) != 0 {
		t.Errorf("exit %d %q %q", code, errOut, s.Calls)
	}
}

// The container scripts, run for real under sh against a temporary folder. Only chown is stubbed:
// it needs root and the image's users. This is what makes a launcher copied from Windows, with no
// execute bit, executable.
func TestPluginScriptsLayOutAVersionUnderSh(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("the scripts run in the Linux container; sh is not on Windows")
	}
	sh, err := exec.LookPath("sh")
	if err != nil {
		t.Skip("no sh")
	}
	stubs := t.TempDir()
	must(t, os.WriteFile(filepath.Join(stubs, "chown"), []byte("#!/bin/sh\nexit 0\n"), 0o755))
	// A setgid parent, as a volume from an older start may be: what is made in it inherits the bit,
	// and an octal chmod would keep it on a directory.
	parent := t.TempDir()
	must(t, os.Chmod(parent, 0o770|os.ModeSetgid))
	root := filepath.Join(parent, "plugins")
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
	mode := func(rel string) os.FileMode {
		t.Helper()
		info, err := os.Stat(filepath.Join(root, rel))
		if err != nil {
			t.Fatal(err)
		}
		if info.Mode()&(os.ModeSetgid|os.ModeSetuid) != 0 {
			t.Errorf("%s keeps a setuid or setgid bit: %v", rel, info.Mode())
		}
		return info.Mode().Perm()
	}

	if got := runScript(cli.PluginPrepareScript, root, "sample-echo", "0.1.0", "0"); strings.TrimSpace(got) != "ready" {
		t.Fatalf("prepare: %q", got)
	}
	// What `podman cp` does: the folder's contents become the staging folder, modes as they came.
	folder := builtPlugin(t, nil)
	staged := filepath.Join(root, ".incoming", "sample-echo-0.1.0")
	must(t, os.CopyFS(staged, os.DirFS(folder)))
	must(t, os.Chmod(filepath.Join(staged, "plugin.json"), 0o666))
	runScript(cli.PluginStageScript, staged, "sample-echo")

	for rel, want := range map[string]os.FileMode{
		".": 0o750, ".incoming": 0o750, ".incoming/sample-echo-0.1.0": 0o750, ".incoming/sample-echo-0.1.0/skills": 0o750,
		".incoming/sample-echo-0.1.0/plugin.json": 0o640, ".incoming/sample-echo-0.1.0/lib/SampleEcho.dll": 0o640,
		".incoming/sample-echo-0.1.0/sample-echo": 0o750,
	} {
		if got := mode(rel); got != want {
			t.Errorf("%s is %o, want %o", rel, got, want)
		}
	}
	if _, err := os.Stat(filepath.Join(root, "sample-echo")); !os.IsNotExist(err) {
		t.Errorf("the CLI laid out a version itself, which is the Host's to do: %v", err)
	}

	// The Host's installer lays the version out; a version that is there is refused without force,
	// and its stage is cleared with it.
	must(t, os.MkdirAll(filepath.Join(root, "sample-echo", "0.1.0"), 0o750))
	if got := runScript(cli.PluginPrepareScript, root, "sample-echo", "0.1.0", "0"); strings.TrimSpace(got) != "exists" {
		t.Errorf("an installed version: %q", got)
	}
	if got := runScript(cli.PluginPrepareScript, root, "sample-echo", "0.1.0", "1"); strings.TrimSpace(got) != "ready" {
		t.Errorf("forced: %q", got)
	}
	if _, err := os.Stat(staged); !os.IsNotExist(err) {
		t.Errorf("an earlier stage is left: %v", err)
	}

	// The layout `plugin list` reads: the active line, then each version folder.
	must(t, os.MkdirAll(filepath.Join(root, "sample-echo", "0.2.0"), 0o750))
	must(t, os.WriteFile(filepath.Join(root, "sample-echo", "active"), []byte("0.2.0\n"), 0o640))
	if got := runScript(cli.PluginLayoutScript, root); got != "sample-echo\t0.2.0\t0.1.0\t0.2.0\n" {
		t.Errorf("layout %q", got)
	}

	runScript(cli.PluginRequestScript, root, "abc")
	if req, _ := os.ReadFile(filepath.Join(root, ".rescan")); string(req) != "abc\n" {
		t.Errorf(".rescan %q", req)
	}
	if got := runScript(cli.PluginReportScript, root); got != "" {
		t.Errorf("no report yet should read empty: %q", got)
	}
}

func TestPluginRemoveSaysSoWhenTheHostStillListsIt(t *testing.T) {
	s := pluginScript(t, report(echoInstalled, "", ""))
	s.On(layoutCall, engine.Result{Stdout: "sample-echo\t0.1.0\t0.1.0\n"})
	code, _, errOut := run(t, stubbed(s), "plugin", "remove", "sample-echo", "--yes")
	if code != 1 || !strings.Contains(errOut, "the Host still lists sample-echo") {
		t.Errorf("exit %d %q", code, errOut)
	}
}

// --from-instance: the CLI asks the Host through .install and prints .install-report.json. The
// scripted engine fakes the Host's side of that exchange; the sentences are PluginInstaller's.
const (
	instanceFolder     = "/data/teams/acme/repos/Tools/main/build/sample-echo/0.1.0"
	installRequestCall = execPrefix + cli.PluginInstallRequestScript + " sh /data/plugins "
	installReportCall  = execPrefix + cli.PluginInstallReportScript + " sh /data/plugins"
)

func installRequestFor(path string, replace bool) string {
	b, _ := json.Marshal(path)
	return fmt.Sprintf(`%s{"request":%q,"path":%s,"replace":%t}`, installRequestCall, nonce, b, replace)
}

func installAnswer(request string, status int, installed, replaced bool, reason string) engine.Result {
	r := map[string]any{"request": request, "at": "2026-09-28T00:00:00Z", "status": status,
		"id": "sample-echo", "version": "0.1.0", "installed": installed, "replaced": replaced, "reason": nil}
	if reason != "" {
		r["reason"] = reason
	}
	b, _ := json.Marshal(r)
	return engine.Result{Stdout: string(b) + "\n"}
}

// fromInstanceScript is a running instance whose Host answers the install report with answers in
// turn; an older report (another nonce) comes first, as it would on a volume used before.
func fromInstanceScript(t *testing.T, answers ...engine.Result) *engine.Scripted {
	s := pluginScript(t)
	s.OnSequence(installReportCall, append([]engine.Result{installAnswer("older", 200, true, false, "")}, answers...)...)
	return s
}

// No copy of the install in the CLI: no podman cp, no prepare/place scripts, no rescan of its own.
func assertOnlyTheRequest(t *testing.T, s *engine.Scripted) {
	t.Helper()
	for _, part := range []string{"podman cp", cli.PluginPrepareScript, cli.PluginStageScript, cli.PluginRequestScript} {
		if c := callsContaining(s, part); len(c) != 0 {
			t.Errorf("ran %q itself:\n%s", part, strings.Join(c, "\n"))
		}
	}
}

func TestPluginInstallFromInstanceAsksTheHostAndPrintsItsVerdict(t *testing.T) {
	s := fromInstanceScript(t, installAnswer(nonce, 200, true, false, ""))
	code, out, errOut := run(t, stubbed(s), "plugin", "install", "--from-instance", instanceFolder)
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if len(callsContaining(s, installRequestFor(instanceFolder, false))) != 1 {
		t.Errorf("the request was not written once:\n%s", strings.Join(s.Calls, "\n"))
	}
	if len(callsContaining(s, installReportCall)) != 2 {
		t.Errorf("an older report was taken for the answer:\n%s", strings.Join(s.Calls, "\n"))
	}
	assertOnlyTheRequest(t, s)
	want := "the Host installed sample-echo 0.1.0 from " + instanceFolder + " and made it the active version; hire it as plugin:sample-echo"
	if !strings.Contains(out, want) {
		t.Errorf("output lacks %q:\n%s", want, out)
	}
}

func TestPluginInstallFromInstancePrintsTheHostsRefusal(t *testing.T) {
	for name, c := range map[string]struct {
		answer engine.Result
		want   string
	}{
		"bad manifest": {installAnswer(nonce, 400, false, false, "The Host refuses this plugin: `requires` names 'ruby', which is not a runtime this Host provides (it provides dotnet, node, python3); ship anything else inside the plugin's folder."),
			instanceFolder + " was not installed: The Host refuses this plugin: `requires` names 'ruby'"},
		"missing file": {installAnswer(nonce, 400, false, false, "The Host refuses sample-echo 0.1.0: its executable sample-echo does not exist."),
			"The Host refuses sample-echo 0.1.0: its executable sample-echo does not exist."},
		"catalog refuses after writing": {installAnswer(nonce, 200, false, false, "needs python3, which is not installed on this instance"),
			"the Host wrote sample-echo 0.1.0 but refuses it: needs python3, which is not installed on this instance"},
	} {
		t.Run(name, func(t *testing.T) {
			s := fromInstanceScript(t, c.answer)
			code, _, errOut := run(t, stubbed(s), "plugin", "install", "--from-instance", instanceFolder)
			if code != 1 || !strings.Contains(errOut, c.want) {
				t.Errorf("exit %d stderr %q, want %q", code, errOut, c.want)
			}
			assertOnlyTheRequest(t, s)
		})
	}
}

func TestPluginInstallFromInstanceRefusesAnInstalledVersionUnlessForced(t *testing.T) {
	s := fromInstanceScript(t, installAnswer(nonce, 409, false, false, "sample-echo 0.1.0 is already installed; choose Replace (the CLI's --force) to replace it."))
	code, _, errOut := run(t, stubbed(s), "plugin", "install", "--from-instance", instanceFolder)
	if code != 1 || !strings.Contains(errOut, "sample-echo 0.1.0 is already installed; choose Replace (the CLI's --force)") {
		t.Errorf("exit %d stderr %q", code, errOut)
	}
	if len(callsContaining(s, installRequestFor(instanceFolder, false))) != 1 {
		t.Errorf("without --force the request must not replace:\n%s", strings.Join(s.Calls, "\n"))
	}

	s = fromInstanceScript(t, installAnswer(nonce, 200, true, true, ""))
	code, out, errOut := run(t, stubbed(s), "plugin", "install", "--from-instance", instanceFolder, "--force")
	if code != 0 || len(callsContaining(s, installRequestFor(instanceFolder, true))) != 1 || !strings.Contains(out, "the Host replaced sample-echo 0.1.0") {
		t.Errorf("--force: exit %d %s %s\n%s", code, out, errOut, strings.Join(s.Calls, "\n"))
	}
	assertOnlyTheRequest(t, s)
}

// Outside the data root, and a relative path, are the Host's to refuse, in its words.
func TestPluginInstallFromInstanceRefusesAFolderOutsideTheDataRoot(t *testing.T) {
	for path, reason := range map[string]string{
		"/srv/build/0.1.0": "/srv/build/0.1.0 is outside the data root (/data); only a folder inside the instance can be installed this way.",
		"build/0.1.0":      "Give the absolute path of one built plugin version folder (the folder holding plugin.json).",
	} {
		s := fromInstanceScript(t, installAnswer(nonce, 400, false, false, reason))
		code, _, errOut := run(t, stubbed(s), "plugin", "install", "--from-instance", path)
		if code != 1 || !strings.Contains(errOut, path+" was not installed: "+reason) {
			t.Errorf("%s: exit %d stderr %q", path, code, errOut)
		}
		if len(callsContaining(s, installRequestFor(path, false))) != 1 {
			t.Errorf("%s: not asked of the Host:\n%s", path, strings.Join(s.Calls, "\n"))
		}
		assertOnlyTheRequest(t, s)
	}
}

func TestPluginInstallFromInstanceWithdrawsARequestTheHostNeverAnswers(t *testing.T) {
	s := fromInstanceScript(t)
	code, _, errOut := run(t, stubbed(s), "plugin", "install", "--from-instance", instanceFolder)
	if code != 1 || !strings.Contains(errOut, "the Host did not answer the install request") || !strings.Contains(errOut, "withdrawn") {
		t.Errorf("exit %d stderr %q", code, errOut)
	}
	if len(callsContaining(s, execPrefix+cli.PluginInstallWithdrawScript+" sh /data/plugins "+nonce)) != 1 {
		t.Errorf("the request was not withdrawn:\n%s", strings.Join(s.Calls, "\n"))
	}
}

// The request, report and withdraw scripts, run for real under sh (chown stubbed: it needs root).
func TestPluginInstallRequestScriptsUnderSh(t *testing.T) {
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

	if got := runScript(cli.PluginInstallReportScript, root); got != "" {
		t.Errorf("no report yet should read empty: %q", got)
	}
	request := `{"request":"abc","path":"/data/x y/0.1.0","replace":true}`
	runScript(cli.PluginInstallRequestScript, root, request)
	path := filepath.Join(root, ".install")
	if got, _ := os.ReadFile(path); string(got) != request+"\n" {
		t.Errorf(".install %q", got)
	}
	if info, err := os.Stat(path); err != nil || info.Mode().Perm() != 0o640 {
		t.Errorf(".install mode: %v %v", info, err)
	}
	if _, err := os.Stat(path + ".tmp"); !os.IsNotExist(err) {
		t.Errorf("the temporary request is left: %v", err)
	}

	runScript(cli.PluginInstallWithdrawScript, root, "another")
	if _, err := os.Stat(path); err != nil {
		t.Errorf("withdrew another request: %v", err)
	}
	runScript(cli.PluginInstallWithdrawScript, root, "abc")
	if _, err := os.Stat(path); !os.IsNotExist(err) {
		t.Errorf("not withdrawn: %v", err)
	}
	runScript(cli.PluginInstallWithdrawScript, root, "abc")
}

// Ctrl-C while the Host is being waited on withdraws the request, so a Host started later never
// carries out an install nobody is waiting for.
func TestAnInterruptedPluginInstallWithdrawsItsRequest(t *testing.T) {
	s := fromInstanceScript(t)
	ctx, cancel := context.WithCancel(context.Background())
	time.AfterFunc(20*time.Millisecond, cancel)
	var out, errb bytes.Buffer
	deps := stubbed(s)
	deps.Stdout, deps.Stderr, deps.ConfigDir = &out, &errb, t.TempDir()
	seedWorkerKey(t, deps.ConfigDir)

	code := cli.ExecuteContext(cli.NewRoot(deps), ctx, []string{"plugin", "install", "--from-instance", instanceFolder})

	if code != 1 {
		t.Errorf("exit %d stderr %q", code, errb.String())
	}
	if len(callsContaining(s, execPrefix+cli.PluginInstallWithdrawScript+" sh /data/plugins "+nonce)) != 1 {
		t.Errorf("the request was not withdrawn:\n%s", strings.Join(s.Calls, "\n"))
	}
}
