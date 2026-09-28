package cli_test

import (
	"archive/tar"
	"bytes"
	"encoding/json"
	"fmt"
	"io/fs"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/cli"
	"github.com/djlsystems/yawble/cli/internal/engine"
)

const repoTar = " exec yawble tar -C /data/repos -cf - -- "

func repoListCall(program string) string {
	return program + " exec yawble sh -c " + cli.RepoListScript + " sh /data/repos"
}

// repoScript is a running instance on program whose repos root lists listing.
func repoScript(program, listing string) *engine.Scripted {
	s := engine.NewScripted()
	s.On(program+" version", engine.Result{Stdout: "6.0.2\n"})
	s.On(program+" container inspect", engine.Result{Stdout: "running|" + testImage + "|" + currentLabel() + "\n"})
	s.On(repoListCall(program), engine.Result{Stdout: listing})
	return s
}

func needGit(t *testing.T) {
	t.Helper()
	if _, err := exec.LookPath("git"); err != nil {
		t.Skip("git is not on PATH")
	}
}

func git(t *testing.T, dir string, args ...string) string {
	t.Helper()
	cmd := exec.Command("git", args...)
	cmd.Dir = dir
	cmd.Env = append(os.Environ(), "GIT_AUTHOR_NAME=t", "GIT_AUTHOR_EMAIL=t@example.com", "GIT_COMMITTER_NAME=t", "GIT_COMMITTER_EMAIL=t@example.com")
	out, err := cmd.CombinedOutput()
	if err != nil {
		t.Fatalf("git %v: %v\n%s", args, err, out)
	}
	return string(out)
}

// bareRepo makes <root>/<name>.git the way the Host does (default branch main, a first commit),
// plus a team branch carrying a file, and answers root.
func bareRepo(t *testing.T, name string) string {
	t.Helper()
	root := t.TempDir()
	bare := filepath.Join(root, name+".git")
	git(t, root, "init", "--quiet", "--bare", "--initial-branch=main", bare)
	work := filepath.Join(t.TempDir(), "w")
	git(t, root, "clone", "--quiet", bare, work)
	git(t, work, "commit", "--quiet", "--allow-empty", "-m", "Initial commit")
	git(t, work, "push", "--quiet", "origin", "HEAD:main")
	git(t, work, "switch", "--quiet", "-c", "team/alpha")
	must(t, os.WriteFile(filepath.Join(work, "plugin.json"), []byte("{}\n"), 0o644))
	git(t, work, "add", "plugin.json")
	git(t, work, "commit", "--quiet", "-m", "Add the manifest")
	git(t, work, "push", "--quiet", "origin", "team/alpha")
	return root
}

// tarOf is what `tar -C root -cf - name.git` prints.
func tarOf(t *testing.T, root, name string) string {
	t.Helper()
	var b bytes.Buffer
	tw := tar.NewWriter(&b)
	must(t, filepath.WalkDir(filepath.Join(root, name), func(p string, d fs.DirEntry, err error) error {
		if err != nil {
			return err
		}
		info, err := d.Info()
		if err != nil {
			return err
		}
		rel, _ := filepath.Rel(root, p)
		h, err := tar.FileInfoHeader(info, "")
		if err != nil {
			return err
		}
		h.Name = filepath.ToSlash(rel)
		if d.IsDir() {
			h.Name += "/"
		}
		if err := tw.WriteHeader(h); err != nil {
			return err
		}
		if !d.IsDir() {
			data, err := os.ReadFile(p)
			if err != nil {
				return err
			}
			_, err = tw.Write(data)
			return err
		}
		return nil
	}))
	must(t, tw.Close())
	return b.String()
}

func TestRepoListShowsEachLocalRepositoryOnBothEngines(t *testing.T) {
	listing := "my-plugin\t2048\tmain\tabc1234\t2026-09-28T10:00:00+00:00\tAdd the manifest\nscratch\t12\tmain\t\t\t\n"
	for _, program := range []string{"podman", "docker"} {
		t.Run(program, func(t *testing.T) {
			s := repoScript(program, listing)
			code, out, errOut := run(t, backupDeps(t, s, program), "repo", "list")
			if code != 0 {
				t.Fatalf("exit %d: %s %s", code, out, errOut)
			}
			for _, want := range []string{"NAME", "local:my-plugin", "abc1234 2026-09-28T10:00:00+00:00 Add the manifest", "2.0 MB", "local:scratch", "12 KB"} {
				if !strings.Contains(out, want) {
					t.Errorf("out lacks %q:\n%s", want, out)
				}
			}
			if indexOf(s.Calls, repoListCall(program)) < 0 {
				t.Errorf("the list script was not run in the container:\n%s", calls(s))
			}
			code, out, _ = run(t, backupDeps(t, s, program), "repo", "list", "--json")
			var rows []map[string]any
			if code != 0 || json.Unmarshal([]byte(out), &rows) != nil || len(rows) != 2 || rows[0]["reference"] != "local:my-plugin" || rows[0]["defaultBranch"] != "main" {
				t.Errorf("json exit %d: %s", code, out)
			}
		})
	}
}

func TestRepoListWithNoRepositoriesSaysSo(t *testing.T) {
	code, out, _ := run(t, backupDeps(t, repoScript("podman", ""), "podman"), "repo", "list")
	if code != 0 || !strings.Contains(out, "no local repositories") {
		t.Errorf("exit %d: %s", code, out)
	}
}

func TestRepoCommandsRefuseAStoppedInstance(t *testing.T) {
	s := stoppedScript()
	code, _, errOut := run(t, backupDeps(t, s, "podman"), "repo", "list")
	if code != 1 || !strings.Contains(errOut, "yawble up") {
		t.Errorf("exit %d: %s", code, errOut)
	}
	if len(callsContaining(s, "exec")) != 0 {
		t.Errorf("nothing may run in a stopped instance:\n%s", calls(s))
	}
}

// The list script itself, run with this computer's sh and git against a folder laid out as the
// volume is: it must find the repository, its default branch and its last commit.
func TestRepoListScriptReadsARealBareRepository(t *testing.T) {
	needGit(t)
	if _, err := exec.LookPath("sh"); err != nil {
		t.Skip("sh is not on PATH")
	}
	root := bareRepo(t, "my-plugin")
	must(t, os.MkdirAll(filepath.Join(root, "not-a-repo"), 0o755))
	out, err := exec.Command("sh", "-c", cli.RepoListScript, "sh", root).Output()
	if err != nil {
		t.Fatal(err)
	}
	fields := strings.Split(strings.TrimSpace(string(out)), "\t")
	if len(fields) != 6 || fields[0] != "my-plugin" || fields[2] != "main" || fields[3] == "" || fields[5] != "Initial commit" {
		t.Errorf("fields %q", fields)
	}
	if out, _ := exec.Command("sh", "-c", cli.RepoListScript, "sh", filepath.Join(root, "missing")).Output(); len(out) != 0 {
		t.Errorf("a missing repos folder lists %q", out)
	}
}

func TestRepoCloneCopiesTheBareRepositoryOutAndClonesItOnBothEngines(t *testing.T) {
	needGit(t)
	root := bareRepo(t, "my-plugin")
	for _, program := range []string{"podman", "docker"} {
		t.Run(program, func(t *testing.T) {
			s := repoScript(program, "my-plugin\t40\tmain\tabc1234\t2026-09-28T10:00:00+00:00\tAdd\n")
			s.On(program+repoTar+"my-plugin.git", engine.Result{Stdout: tarOf(t, root, "my-plugin.git")})
			folder := filepath.Join(t.TempDir(), "code")
			code, out, errOut := run(t, backupDeps(t, s, program), "repo", "clone", "my-plugin", folder)
			if code != 0 {
				t.Fatalf("exit %d: %s %s", code, out, errOut)
			}
			if indexOf(s.Calls, program+repoTar+"my-plugin.git") < 0 {
				t.Errorf("the bare repository was not streamed out:\n%s", calls(s))
			}
			if got := strings.TrimSpace(git(t, folder, "branch", "--show-current")); got != "main" {
				t.Errorf("checked out %q", got)
			}
			if got := git(t, folder, "branch", "--format=%(refname:short)"); !strings.Contains(got, "team/alpha") {
				t.Errorf("branches %q", got)
			}
			if got := strings.TrimSpace(git(t, folder, "remote")); got != "" {
				t.Errorf("the clone keeps remote %q, which names a folder that is gone", got)
			}
			git(t, folder, "switch", "--quiet", "team/alpha")
			if _, err := os.Stat(filepath.Join(folder, "plugin.json")); err != nil {
				t.Errorf("team/alpha's file is not there: %v", err)
			}
			if !strings.Contains(out, "cloned local:my-plugin into "+folder+" on main") || !strings.Contains(out, "pushing back to the instance is not supported") {
				t.Errorf("out %q", out)
			}
		})
	}
}

func TestRepoCloneIntoTheNameByDefault(t *testing.T) {
	needGit(t)
	root := bareRepo(t, "my-plugin")
	s := repoScript("podman", "my-plugin\t40\tmain\t\t\t\n")
	s.On("podman"+repoTar+"my-plugin.git", engine.Result{Stdout: tarOf(t, root, "my-plugin.git")})
	t.Chdir(t.TempDir())
	if code, out, errOut := run(t, backupDeps(t, s, "podman"), "repo", "clone", "my-plugin"); code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if _, err := os.Stat(filepath.Join("my-plugin", ".git")); err != nil {
		t.Errorf("no clone in ./my-plugin: %v", err)
	}
}

func TestRepoCloneRefusesAFolderThatExistsAndTouchesNothing(t *testing.T) {
	folder := t.TempDir()
	must(t, os.WriteFile(filepath.Join(folder, "mine.txt"), []byte("keep"), 0o644))
	s := repoScript("podman", "my-plugin\t40\tmain\t\t\t\n")
	code, _, errOut := run(t, backupDeps(t, s, "podman"), "repo", "clone", "my-plugin", folder)
	if code != 1 || !strings.Contains(errOut, folder+" already exists") {
		t.Errorf("exit %d: %s", code, errOut)
	}
	if len(s.Calls) != 0 {
		t.Errorf("the engine was asked:\n%s", calls(s))
	}
	if data, _ := os.ReadFile(filepath.Join(folder, "mine.txt")); string(data) != "keep" {
		t.Errorf("the folder was changed")
	}
}

func TestRepoCloneRefusesAnIllegalNameNamingIt(t *testing.T) {
	for _, name := range []string{"..", ".", "a/b", `a\b`, ".git", ".GIT", "-x", ""} {
		s := repoScript("podman", "")
		code, _, errOut := run(t, backupDeps(t, s, "podman"), "repo", "clone", "--", name, filepath.Join(t.TempDir(), "out"))
		if code != 2 || !strings.Contains(errOut, fmt.Sprintf("%q is not a local repository name", name)) {
			t.Errorf("%q: exit %d: %s", name, code, errOut)
		}
		if len(s.Calls) != 0 {
			t.Errorf("%q: the engine was asked:\n%s", name, calls(s))
		}
	}
}

func TestRepoCloneRefusesAnUnknownNameNamingItAndWhatThereIs(t *testing.T) {
	for _, program := range []string{"podman", "docker"} {
		s := repoScript(program, "my-plugin\t40\tmain\t\t\t\nscratch\t1\tmain\t\t\t\n")
		folder := filepath.Join(t.TempDir(), "out")
		code, _, errOut := run(t, backupDeps(t, s, program), "repo", "clone", "nope", folder)
		if code != 1 || !strings.Contains(errOut, `no local repository named "nope" (it has: my-plugin, scratch)`) {
			t.Errorf("%s: exit %d: %s", program, code, errOut)
		}
		if len(callsContaining(s, " tar ")) != 0 {
			t.Errorf("%s: something was copied:\n%s", program, calls(s))
		}
		if _, err := os.Stat(folder); err == nil {
			t.Errorf("%s: the folder was made", program)
		}
	}
}

func TestRepoCloneRefusesACopyThatLeavesTheRepository(t *testing.T) {
	needGit(t)
	var b bytes.Buffer
	tw := tar.NewWriter(&b)
	must(t, tw.WriteHeader(&tar.Header{Name: "my-plugin.git/../../evil", Mode: 0o644, Size: 1, Typeflag: tar.TypeReg}))
	_, _ = tw.Write([]byte("x"))
	must(t, tw.Close())
	s := repoScript("podman", "my-plugin\t40\tmain\t\t\t\n")
	s.On("podman"+repoTar+"my-plugin.git", engine.Result{Stdout: b.String()})
	folder := filepath.Join(t.TempDir(), "out")
	code, _, errOut := run(t, backupDeps(t, s, "podman"), "repo", "clone", "my-plugin", folder)
	if code != 1 || !strings.Contains(errOut, "outside my-plugin.git") {
		t.Errorf("exit %d: %s", code, errOut)
	}
	if _, err := os.Stat(folder); err == nil {
		t.Errorf("a folder was made from a refused copy")
	}
}

func TestRepoCloneWithoutGitSaysWhereToGetIt(t *testing.T) {
	t.Cleanup(cli.NoGit())
	s := repoScript("podman", "my-plugin\t40\tmain\t\t\t\n")
	code, _, errOut := run(t, backupDeps(t, s, "podman"), "repo", "clone", "my-plugin", filepath.Join(t.TempDir(), "out"))
	if code != 1 || !strings.Contains(errOut, "git is not on this computer's PATH") {
		t.Errorf("exit %d: %s", code, errOut)
	}
	if len(s.Calls) != 0 {
		t.Errorf("the engine was asked:\n%s", calls(s))
	}
}
