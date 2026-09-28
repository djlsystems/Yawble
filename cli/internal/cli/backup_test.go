package cli_test

import (
	"archive/tar"
	"bytes"
	"compress/gzip"
	"encoding/json"
	"errors"
	"io"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/backup"
	"github.com/djlsystems/yawble/cli/internal/cli"
	"github.com/djlsystems/yawble/cli/internal/engine"
)

// helper is the start of every helper container's command line: the same flags on both engines.
func helper(program, entrypoint string, readOnly bool) string {
	mount := "yawble-data:/data"
	if readOnly {
		mount += ":ro"
	}
	return program + " run --rm --pull never --network none --user 0 --entrypoint " + entrypoint + " -v " + mount + " " + testImage
}

func stdinHelper(program, entrypoint string) string {
	return program + " run --rm -i --pull never --network none --user 0 --entrypoint " + entrypoint + " -v yawble-data:/data " + testImage
}

// volumeStream is what the helper's `tar -cf - .` prints for a small instance.
func volumeStream(t *testing.T) string {
	t.Helper()
	var b bytes.Buffer
	tw := tar.NewWriter(&b)
	for name, body := range map[string]string{"./messages.db": "SQLite format 3", "./teams/alpha/doc.md": "# hello", "./agent-home/.claude/.credentials.json": "{}"} {
		if err := tw.WriteHeader(&tar.Header{Name: name, Typeflag: tar.TypeReg, Mode: 0o600, Size: int64(len(body))}); err != nil {
			t.Fatal(err)
		}
		_, _ = io.WriteString(tw, body)
	}
	_ = tw.Close()
	return b.String()
}

const agentListing = "311\t/data/teams/Alpha/workspaces/Ann\tnode /data/npm-global/bin/claude -p hi\n"

// backupScript is an instance on program in the state given ("running" or "exited"), with no
// agents running unless listing says so.
func backupScript(t *testing.T, program, state, listing string) *engine.Scripted {
	s := engine.NewScripted()
	s.On(program+" version", engine.Result{Stdout: "6.0.2\n"})
	s.On(program+" container inspect", engine.Result{Stdout: state + "|" + testImage + "|" + currentLabel() + "\n"})
	s.On(program+" exec yawble runuser -u agent", engine.Result{Stdout: listing})
	s.On(helper(program, "sqlite3", true), engine.Result{Stdout: "0001-initial\n0002-teams\n"})
	s.On(helper(program, "tar", true), engine.Result{Stdout: volumeStream(t)})
	return s
}

func backupDeps(t *testing.T, s *engine.Scripted, program string) cli.Deps {
	deps := stubbed(s)
	deps.LookPath = lookPath(program)
	deps.ConfigDir = t.TempDir()
	return deps
}

func readArchive(t *testing.T, path string) (map[string]any, []string) {
	t.Helper()
	f, err := os.Open(path)
	if err != nil {
		t.Fatal(err)
	}
	defer f.Close()
	gz, err := gzip.NewReader(f)
	if err != nil {
		t.Fatal(err)
	}
	tr := tar.NewReader(gz)
	var manifest map[string]any
	var names []string
	for {
		h, err := tr.Next()
		if errors.Is(err, io.EOF) {
			return manifest, names
		}
		if err != nil {
			t.Fatal(err)
		}
		names = append(names, h.Name)
		if h.Name == "manifest.json" {
			if err := json.NewDecoder(tr).Decode(&manifest); err != nil {
				t.Fatal(err)
			}
		}
	}
}

func TestBackupStopsARunningInstanceWritesTheArchiveAndStartsItAgain(t *testing.T) {
	for _, program := range []string{"podman", "docker"} {
		t.Run(program, func(t *testing.T) {
			s := backupScript(t, program, "running", "")
			deps := backupDeps(t, s, program)
			path := filepath.Join(t.TempDir(), "b.tar.gz")
			code, out, errOut := run(t, deps, "backup", "--output", path)
			if code != 0 {
				t.Fatalf("exit %d: %s %s", code, out, errOut)
			}
			stop, tarRun, start := indexOf(s.Calls, program+" stop yawble"), indexOf(s.Calls, helper(program, "tar", true)), indexOf(s.Calls, program+" start yawble")
			if stop < 0 || tarRun < stop || start < tarRun {
				t.Errorf("want stop, then the helper's tar, then start:\n%s", calls(s))
			}
			if !strings.Contains(out, "stopping yawble") || !strings.Contains(out, "starting yawble again") || !strings.Contains(out, "Yawble is up") {
				t.Errorf("out %q", out)
			}
			if _, names := readArchive(t, path); names[0] != "manifest.json" || !strings.Contains(strings.Join(names, " "), "data/teams/alpha/doc.md") {
				t.Errorf("archive %v", names)
			}
		})
	}
}

func TestBackupOfAStoppedInstanceLeavesItStopped(t *testing.T) {
	for _, program := range []string{"podman", "docker"} {
		t.Run(program, func(t *testing.T) {
			s := backupScript(t, program, "exited", "")
			deps := backupDeps(t, s, program)
			path := filepath.Join(t.TempDir(), "b.tar.gz")
			code, out, errOut := run(t, deps, "backup", "--output", path)
			if code != 0 {
				t.Fatalf("exit %d: %s %s", code, out, errOut)
			}
			c := calls(s)
			if strings.Contains(c, program+" stop ") || strings.Contains(c, program+" start ") {
				t.Errorf("a stopped instance was touched:\n%s", c)
			}
			if !strings.Contains(out, "stays stopped") || !exists(path) {
				t.Errorf("out %q", out)
			}
		})
	}
}

func TestBackupNamesRunningAgentsAndNeedsAYes(t *testing.T) {
	for _, program := range []string{"podman", "docker"} {
		t.Run(program, func(t *testing.T) {
			// No terminal, no --yes: refused, nothing stopped.
			s := backupScript(t, program, "running", agentListing)
			deps := backupDeps(t, s, program)
			path := filepath.Join(t.TempDir(), "b.tar.gz")
			code, out, errOut := run(t, deps, "backup", "--output", path)
			if code != 2 || !strings.Contains(out, "this stops Alpha/Ann (claude)") || !strings.Contains(errOut, "--yes") {
				t.Errorf("exit %d out %q err %q", code, out, errOut)
			}
			if indexOf(s.Calls, program+" stop yawble") >= 0 || exists(path) {
				t.Errorf("stopped or written without a yes:\n%s", calls(s))
			}

			// At a terminal, answered no.
			s = backupScript(t, program, "running", agentListing)
			deps = backupDeps(t, s, program)
			deps.Interactive, deps.Stdin = true, strings.NewReader("n\n")
			code, out, _ = run(t, deps, "backup", "--output", path)
			if code != 0 || !strings.Contains(out, "nothing was backed up") || indexOf(s.Calls, program+" stop yawble") >= 0 {
				t.Errorf("exit %d out %q", code, out)
			}

			// --yes answers.
			s = backupScript(t, program, "running", agentListing)
			deps = backupDeps(t, s, program)
			code, out, errOut = run(t, deps, "backup", "--output", path, "--yes")
			if code != 0 || indexOf(s.Calls, program+" stop yawble") < 0 || !exists(path) {
				t.Errorf("exit %d out %q err %q", code, out, errOut)
			}
		})
	}
}

func TestBackupLeavesOutCachesAndAgentProgramsUnlessFull(t *testing.T) {
	for _, program := range []string{"podman", "docker"} {
		t.Run(program, func(t *testing.T) {
			s := backupScript(t, program, "exited", "")
			deps := backupDeps(t, s, program)
			dir := t.TempDir()
			if code, out, errOut := run(t, deps, "backup", "--output", filepath.Join(dir, "a.tar.gz")); code != 0 {
				t.Fatalf("exit %d: %s %s", code, out, errOut)
			}
			line := s.Calls[indexOf(s.Calls, helper(program, "tar", true))]
			want := helper(program, "tar", true) + " -C /data --numeric-owner --anchored --wildcards"
			for _, e := range backup.DefaultExcludes {
				want += " --exclude=" + e
			}
			if line != want+" -cf - ." {
				t.Errorf("tar line\n got %s\nwant %s -cf - .", line, want)
			}
			for _, cache := range []string{"./npm-cache", "./pip-cache", "./go-cache", "./nuget", "./ms-playwright", "./npm-global/bin/claude", "./agent-home/.grok/downloads"} {
				if !strings.Contains(line, "--exclude="+cache+" ") {
					t.Errorf("%s not left out", cache)
				}
			}
			if strings.Contains(line, "--exclude=./agent-home ") || strings.Contains(line, "--exclude=./agent-home/.claude") {
				t.Errorf("agent logins left out: %s", line)
			}

			s = backupScript(t, program, "exited", "")
			deps = backupDeps(t, s, program)
			if code, out, errOut := run(t, deps, "backup", "--full", "--output", filepath.Join(dir, "b.tar.gz")); code != 0 {
				t.Fatalf("exit %d: %s %s", code, out, errOut)
			}
			if line := s.Calls[indexOf(s.Calls, helper(program, "tar", true))]; line != helper(program, "tar", true)+" -C /data --numeric-owner -cf - ." {
				t.Errorf("--full tar line %s", line)
			}
		})
	}
}

func TestBackupManifestCarriesEveryFieldAndTheFileIsOwnerOnly(t *testing.T) {
	for _, program := range []string{"podman", "docker"} {
		t.Run(program, func(t *testing.T) {
			s := backupScript(t, program, "exited", "")
			deps := backupDeps(t, s, program)
			if err := os.WriteFile(filepath.Join(deps.ConfigDir, "env"), []byte("ANTHROPIC_API_KEY=sk-ant-secret\nGH_TOKEN=ghp_secret\n"), 0o600); err != nil {
				t.Fatal(err)
			}
			path := filepath.Join(t.TempDir(), "b.tar.gz")
			code, out, errOut := run(t, deps, "backup", "--output", path)
			if code != 0 {
				t.Fatalf("exit %d: %s %s", code, out, errOut)
			}
			m, _ := readArchive(t, path)
			for _, field := range []string{"format", "yawbleVersion", "image", "imageVersion", "engine", "engineVersion", "processor", "createdAt", "schemaSteps", "full", "excluded", "bytes", "providers"} {
				if _, ok := m[field]; !ok {
					t.Errorf("manifest lacks %s: %v", field, m)
				}
			}
			if m["engine"] != program || m["image"] != testImage || m["imageVersion"] != "2026.09.24.1" || m["processor"] != "arm64" || m["engineVersion"] != "6.0.2" {
				t.Errorf("manifest %v", m)
			}
			if steps, _ := json.Marshal(m["schemaSteps"]); string(steps) != `["0001-initial","0002-teams"]` {
				t.Errorf("schemaSteps %s", steps)
			}
			if b, _ := m["bytes"].(map[string]any); b["files"] != float64(3) || b["data"] != float64(len("SQLite format 3")+len("# hello")+2) {
				t.Errorf("bytes %v", m["bytes"])
			}
			raw, _ := os.ReadFile(path)
			if providers, _ := json.Marshal(m["providers"]); string(providers) != `["ANTHROPIC_API_KEY","GH_TOKEN"]` || bytes.Contains(raw, []byte("secret")) {
				t.Errorf("providers %s", providers)
			}
			if runtime.GOOS != "windows" {
				if info, _ := os.Stat(path); info.Mode().Perm() != 0o600 {
					t.Errorf("mode %04o", info.Mode().Perm())
				}
			}
			if !strings.Contains(out, "holds secrets") || !strings.Contains(out, "readable by you only") {
				t.Errorf("no secrets line: %q", out)
			}
		})
	}
}

func TestBackupDefaultNameIsTimestampedInTheCurrentFolder(t *testing.T) {
	s := backupScript(t, "podman", "exited", "")
	deps := backupDeps(t, s, "podman")
	dir := t.TempDir()
	t.Chdir(dir)
	code, out, errOut := run(t, deps, "backup")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	entries, _ := os.ReadDir(dir)
	if len(entries) != 1 || !strings.HasPrefix(entries[0].Name(), "yawble-backup-") || !strings.HasSuffix(entries[0].Name(), ".tar.gz") || len(entries[0].Name()) != len("yawble-backup-20260928-101500.tar.gz") {
		t.Errorf("files %v", entries)
	}
}

// --- restore ---

// backupFile is a backup made by Yawble version, holding the small instance, with providers.
func backupFile(t *testing.T, dir, version string, providers ...string) string {
	t.Helper()
	path := filepath.Join(dir, "yawble-backup-20260920-080000.tar.gz")
	stream := volumeStream(t)
	m := backup.Manifest{Image: "ghcr.io/djlsystems/yawble:" + version, ImageVersion: version, Engine: "podman", Processor: "amd64", Providers: providers}
	if _, err := backup.Write(path, m, func(w io.Writer) error { _, err := io.WriteString(w, stream); return err }); err != nil {
		t.Fatal(err)
	}
	return path
}

// restoreScript is this computer's instance on program: the image here, the volume empty or
// not, the container stopped (or running).
func restoreScript(t *testing.T, program string, volumeHolds, state string) *engine.Scripted {
	s := engine.NewScripted()
	s.On(program+" version", engine.Result{Stdout: "28.0.1\n"})
	s.On(program+" container inspect", engine.Result{Stdout: state + "|" + testImage + "|" + currentLabel() + "\n"})
	s.On(helper(program, "find", true), engine.Result{Stdout: volumeHolds})
	s.On(helper(program, "sqlite3", true), engine.Result{Stdout: "0001-initial\n"})
	s.On(helper(program, "tar", true), engine.Result{Stdout: volumeStream(t)})
	return s
}

func restoreDeps(t *testing.T, s *engine.Scripted, program string) cli.Deps {
	deps := backupDeps(t, s, program)
	// The first `up` asks about GitHub once; this computer has been asked.
	if err := os.WriteFile(filepath.Join(deps.ConfigDir, "config.toml"), []byte("githubAsked = true\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	return deps
}

// restoredNames reads the tar restore handed the extracting helper on stdin.
func restoredNames(t *testing.T, s *engine.Scripted) []string {
	t.Helper()
	if len(s.Inputs) == 0 {
		t.Fatal("nothing was handed to the extracting helper")
	}
	tr := tar.NewReader(strings.NewReader(s.Inputs[len(s.Inputs)-1]))
	var names []string
	for {
		h, err := tr.Next()
		if err != nil {
			return names
		}
		names = append(names, h.Name)
	}
}

func TestRestoreIntoAnEmptyVolumeAsksNothingThenStartsAndWaitsForHealth(t *testing.T) {
	for _, program := range []string{"podman", "docker"} {
		t.Run(program, func(t *testing.T) {
			s := restoreScript(t, program, "", "exited")
			deps := restoreDeps(t, s, program)
			deps.HTTP.Transport = &healthStub{codes: []int{503, 200}}
			file := backupFile(t, t.TempDir(), "2026.09.24.1")
			code, out, errOut := run(t, deps, "restore", file)
			if code != 0 {
				t.Fatalf("exit %d: %s %s", code, out, errOut)
			}
			c := calls(s)
			extract := indexOf(s.Calls, stdinHelper(program, "tar")+" -C /data --numeric-owner -xpf -")
			start := indexOf(s.Calls, program+" start yawble")
			if extract < 0 || start < extract {
				t.Errorf("want the extract, then the start:\n%s", c)
			}
			if strings.Contains(c, "rm -rf") || strings.Contains(out, "Type replace") {
				t.Errorf("an empty volume was cleared or asked about:\n%s\n%s", c, out)
			}
			if names := strings.Join(restoredNames(t, s), " "); !strings.Contains(names, "./teams/alpha/doc.md") || strings.Contains(names, "manifest") {
				t.Errorf("restored %s", names)
			}
			if !strings.Contains(out, "restored "+file) || !strings.Contains(out, "Yawble is up at") {
				t.Errorf("out %q", out)
			}
		})
	}
}

func TestRestoreRefusesAVolumeWithDataWithoutReplace(t *testing.T) {
	for _, program := range []string{"podman", "docker"} {
		t.Run(program, func(t *testing.T) {
			s := restoreScript(t, program, "/data/messages.db\n/data/teams\n", "running")
			deps := restoreDeps(t, s, program)
			code, out, errOut := run(t, deps, "restore", backupFile(t, t.TempDir(), "2026.09.24.1"))
			if code == 0 || !strings.Contains(errOut, "--replace") {
				t.Errorf("exit %d out %q err %q", code, out, errOut)
			}
			c := calls(s)
			if strings.Contains(c, " -xpf ") || strings.Contains(c, "rm -rf") || strings.Contains(c, program+" stop yawble") {
				t.Errorf("changed something:\n%s", c)
			}
		})
	}
}

func TestRestoreReplaceNeedsTheTypedWordOrYes(t *testing.T) {
	for _, program := range []string{"podman", "docker"} {
		t.Run(program, func(t *testing.T) {
			holds := "/data/messages.db\n"
			// From a script without --yes: refused, nothing changed.
			s := restoreScript(t, program, holds, "exited")
			deps := restoreDeps(t, s, program)
			dir := t.TempDir()
			file := backupFile(t, dir, "2026.09.24.1")
			code, _, errOut := run(t, deps, "restore", file, "--replace")
			if code != 2 || !strings.Contains(errOut, "replace") || strings.Contains(calls(s), " -xpf ") {
				t.Errorf("exit %d err %q", code, errOut)
			}

			// At a terminal, a wrong word changes nothing.
			s = restoreScript(t, program, holds, "exited")
			deps = restoreDeps(t, s, program)
			deps.Interactive, deps.Stdin = true, strings.NewReader("yes\n")
			code, out, _ := run(t, deps, "restore", file, "--replace")
			if code != 0 || !strings.Contains(out, "nothing was changed") || strings.Contains(calls(s), " -xpf ") {
				t.Errorf("exit %d out %q", code, out)
			}

			// The typed word replaces.
			s = restoreScript(t, program, holds, "exited")
			deps = restoreDeps(t, s, program)
			deps.Interactive, deps.Stdin = true, strings.NewReader("replace\n")
			if code, out, errOut := run(t, deps, "restore", file, "--replace"); code != 0 || indexOf(s.Calls, stdinHelper(program, "tar")) < 0 {
				t.Errorf("exit %d out %q err %q", code, out, errOut)
			}
		})
	}
}

func TestRestoreReplaceYesBacksUpTheCurrentVolumeFirst(t *testing.T) {
	for _, program := range []string{"podman", "docker"} {
		t.Run(program, func(t *testing.T) {
			s := restoreScript(t, program, "/data/messages.db\n", "running")
			deps := restoreDeps(t, s, program)
			dir := t.TempDir()
			file := backupFile(t, dir, "2026.09.24.1")
			code, out, errOut := run(t, deps, "restore", file, "--replace", "--yes")
			if code != 0 {
				t.Fatalf("exit %d: %s %s", code, out, errOut)
			}
			stop := indexOf(s.Calls, program+" stop yawble")
			safety := indexOf(s.Calls, helper(program, "tar", true))
			clear := indexOf(s.Calls, helper(program, "find", false)+" /data -mindepth 1 -maxdepth 1 -exec rm -rf {} +")
			extract := indexOf(s.Calls, stdinHelper(program, "tar"))
			if stop < 0 || safety < stop || clear < safety || extract < clear {
				t.Errorf("want stop, backup of the current volume, clear, extract:\n%s", calls(s))
			}
			var before string
			entries, _ := os.ReadDir(dir)
			for _, e := range entries {
				if strings.HasSuffix(e.Name(), "-before-restore.tar.gz") {
					before = filepath.Join(dir, e.Name())
				}
			}
			if before == "" || !strings.Contains(out, before) {
				t.Errorf("no backup of the current volume beside the file, or not named: %v\n%s", entries, out)
			}
			if m, err := backup.ReadManifest(before); err != nil || m.Bytes.Files != 3 {
				t.Errorf("the safety backup: %+v %v", m, err)
			}
		})
	}
}

func TestRestoreRefusesABackupFromANewerYawble(t *testing.T) {
	for _, program := range []string{"podman", "docker"} {
		t.Run(program, func(t *testing.T) {
			s := restoreScript(t, program, "", "exited")
			deps := restoreDeps(t, s, program)
			code, out, errOut := run(t, deps, "restore", backupFile(t, t.TempDir(), "2026.10.02.1"))
			if code == 0 {
				t.Fatalf("a newer backup was restored: %s", out)
			}
			for _, want := range []string{"2026.10.02.1", "2026.09.24.1", "yawble update"} {
				if !strings.Contains(errOut, want) {
					t.Errorf("err %q lacks %q", errOut, want)
				}
			}
			if len(s.Calls) != 0 {
				t.Errorf("the engine was touched:\n%s", calls(s))
			}
		})
	}
}

// An older backup is fine: the Host moves its database forward at start.
func TestRestoreTakesAnOlderBackup(t *testing.T) {
	s := restoreScript(t, "podman", "", "exited")
	deps := restoreDeps(t, s, "podman")
	if code, out, errOut := run(t, deps, "restore", backupFile(t, t.TempDir(), "2026.09.01.3")); code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
}

func TestRestoreListsTheProvidersWithoutValues(t *testing.T) {
	for _, program := range []string{"podman", "docker"} {
		t.Run(program, func(t *testing.T) {
			// Made on another computer whose keys were set; this one has only GH_TOKEN so far.
			s := restoreScript(t, program, "", "exited")
			deps := restoreDeps(t, s, program)
			if err := os.WriteFile(filepath.Join(deps.ConfigDir, "env"), []byte("GH_TOKEN=ghp_here\n"), 0o600); err != nil {
				t.Fatal(err)
			}
			file := backupFile(t, t.TempDir(), "2026.09.24.1", "ANTHROPIC_API_KEY", "GH_TOKEN")
			code, out, errOut := run(t, deps, "restore", file)
			if code != 0 {
				t.Fatalf("exit %d: %s %s", code, out, errOut)
			}
			for _, want := range []string{"ANTHROPIC_API_KEY  Anthropic (Claude)", "GH_TOKEN  GitHub  (already set on this computer)", "yawble secret set"} {
				if !strings.Contains(out, want) {
					t.Errorf("out lacks %q:\n%s", want, out)
				}
			}
			if strings.Contains(out, "ghp_here") {
				t.Errorf("a value was printed:\n%s", out)
			}
		})
	}
}

func TestRestoreRefusesAFileThatIsNotABackup(t *testing.T) {
	path := filepath.Join(t.TempDir(), "x.tar.gz")
	_ = os.WriteFile(path, []byte("hello"), 0o600)
	s := engine.NewScripted()
	code, _, errOut := run(t, restoreDeps(t, s, "podman"), "restore", path)
	if code == 0 || !strings.Contains(errOut, "not a yawble backup") || len(s.Calls) != 0 {
		t.Errorf("exit %d err %q calls %v", code, errOut, s.Calls)
	}
}

// --- doctor ---

func TestDoctorNamesTheNewestBackupAndItsAgeWithoutFailing(t *testing.T) {
	s := stoppedScript()
	deps := stubbed(s)
	deps.ConfigDir = t.TempDir()
	_, out, _ := run(t, deps, "doctor", "--json")
	if v, d, _ := verdict(t, parseDoctor(t, out), "backup"); v != "info" || !strings.Contains(d, "no backup") {
		t.Errorf("before any backup: %s %s", v, d)
	}

	s2 := backupScript(t, "podman", "exited", "")
	path := filepath.Join(t.TempDir(), "b.tar.gz")
	bdeps := backupDeps(t, s2, "podman")
	bdeps.ConfigDir = deps.ConfigDir
	if code, out, errOut := run(t, bdeps, "backup", "--output", path); code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	_, out, _ = run(t, deps, "doctor", "--json")
	if v, d, _ := verdict(t, parseDoctor(t, out), "backup"); v != "info" || !strings.Contains(d, path) || !strings.Contains(d, "just now") {
		t.Errorf("after a backup: %s %s", v, d)
	}
	_ = os.Remove(path)
	_, out, _ = run(t, deps, "doctor", "--json")
	if v, d, _ := verdict(t, parseDoctor(t, out), "backup"); v != "info" || !strings.Contains(d, "no longer") {
		t.Errorf("after the file moved: %s %s", v, d)
	}
}

func TestHelpDescribesBackupAndRestore(t *testing.T) {
	_, out, _ := run(t, cli.Deps{}, "--help")
	if !strings.Contains(out, "backup") || !strings.Contains(out, "restore") {
		t.Errorf("--help:\n%s", out)
	}
	_, out, _ = run(t, cli.Deps{}, "help", "restore")
	for _, want := range []string{"--replace", "yawble update", "secret set"} {
		if !strings.Contains(out, want) {
			t.Errorf("help restore lacks %q", want)
		}
	}
	_, out, _ = run(t, cli.Deps{}, "help", "backup")
	for _, want := range []string{"--full", "--output", "readable by you only", "Podman", "Docker"} {
		if !strings.Contains(out, want) {
			t.Errorf("help backup lacks %q", want)
		}
	}
}

// The move itself: after `uninstall --data` and a switch of engine there is no volume and no
// container. Restore creates the volume, fills it, and `up` makes the rest.
func TestRestoreOntoAFreshEngineCreatesTheVolumeAndTheContainer(t *testing.T) {
	s := engine.NewScripted()
	s.OnSequence("docker container inspect --format {{.State.Status}}|{{.Config.Image}}|{{index .Config.Labels \"yawble.settings\"}} yawble",
		engine.Result{Stderr: "Error: No such container: yawble", ExitCode: 1})
	s.On(helper("docker", "find", true), engine.Result{})
	s.On("docker volume inspect yawble-data", engine.Result{Stderr: "Error: No such volume: yawble-data", ExitCode: 1})
	deps := restoreDeps(t, s, "docker")
	code, out, errOut := run(t, deps, "restore", backupFile(t, t.TempDir(), "2026.09.24.1"))
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	create, extract, runAt := indexOf(s.Calls, "docker volume create yawble-data"), indexOf(s.Calls, stdinHelper("docker", "tar")), indexOf(s.Calls, "docker run -d --name yawble")
	if create < 0 || extract < create || runAt < extract {
		t.Errorf("want the volume created, filled, then the container run:\n%s", calls(s))
	}
}
