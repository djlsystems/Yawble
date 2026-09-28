package cli

import (
	"archive/tar"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"text/tabwriter"

	"github.com/spf13/cobra"

	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/instance"
)

// reposRoot is where the Host keeps the instance's local repositories: one bare repository per
// name, <data root>/repos/<name>.git, owned by harness. A team names one as local:<name>.
const reposRoot = dataRoot + "/repos"

// lookGit finds git on this computer; tests replace it to see the refusal.
var lookGit = exec.LookPath

func newRepoCommand(deps Deps) *cobra.Command {
	cmd := &cobra.Command{
		Use:   "repo",
		Short: "List the instance's local repositories and clone one onto this computer",
		Long: "A local repository is a git repository that lives only on the instance, in " + reposRoot + "/<name>.git " +
			"on the data volume, with no hosting service. A team works on it as local:<name>. These commands show " +
			"which ones the instance holds and copy one out, so you can open the code in an editor. Pushing back " +
			"from this computer is not supported.",
	}
	cmd.AddCommand(newRepoListCommand(deps), newRepoCloneCommand(deps))
	return cmd
}

func newRepoListCommand(deps Deps) *cobra.Command {
	var asJSON bool
	cmd := &cobra.Command{
		Use:     "list",
		Short:   "Each local repository on the instance: name, default branch, last commit and size",
		Example: "  yawble repo list\n  yawble repo list --json",
		Args:    cobra.NoArgs,
		RunE: func(cmd *cobra.Command, _ []string) error {
			ctx, out := cmd.Context(), cmd.OutOrStdout()
			e, err := runningEngine(ctx, deps, "repo list")
			if err != nil {
				return err
			}
			repos, err := readRepos(ctx, e)
			if err != nil {
				return err
			}
			if asJSON {
				enc := json.NewEncoder(out)
				enc.SetIndent("", "  ")
				return enc.Encode(repos)
			}
			if len(repos) == 0 {
				fmt.Fprintln(out, "the instance has no local repositories; a person creates one in New Team or Team settings")
				return nil
			}
			w := tabwriter.NewWriter(out, 0, 4, 2, ' ', 0)
			fmt.Fprintln(w, "NAME\tREFERENCE\tDEFAULT BRANCH\tLAST COMMIT\tSIZE")
			for _, r := range repos {
				last := "-"
				if r.LastCommit != "" {
					last = r.LastCommit + " " + r.LastCommitAt + " " + r.LastSubject
				}
				fmt.Fprintf(w, "%s\tlocal:%s\t%s\t%s\t%s\n", r.Name, r.Name, orDash(r.DefaultBranch), last, humanKB(r.SizeKB))
			}
			return w.Flush()
		},
	}
	cmd.Flags().BoolVar(&asJSON, "json", false, "print JSON")
	return cmd
}

func newRepoCloneCommand(deps Deps) *cobra.Command {
	return &cobra.Command{
		Use:   "clone <name> [folder]",
		Short: "Copy a local repository out of the instance and clone it on this computer",
		Long: "Copies " + reposRoot + "/<name>.git out of the running instance through the container engine and " +
			"clones it into [folder] (default: <name> in the current folder), which must not exist yet. Every " +
			"branch on the instance becomes a local branch, and the default branch is checked out. The clone has " +
			"no remote: pushing back to the instance is not supported. Needs git on this computer.",
		Example: "  yawble repo clone my-plugin\n  yawble repo clone my-plugin ~/code/my-plugin",
		Args:    cobra.RangeArgs(1, 2),
		RunE: func(cmd *cobra.Command, args []string) error {
			name := args[0]
			if reason := illegalRepoName(name); reason != "" {
				return UsageError{fmt.Sprintf("%q is not a local repository name: %s", name, reason)}
			}
			folder := name
			if len(args) == 2 {
				folder = args[1]
			}
			if _, err := os.Lstat(folder); err == nil {
				return fmt.Errorf("%s already exists; name a folder that does not exist yet: yawble repo clone %s <folder>", folder, name)
			} else if !errors.Is(err, os.ErrNotExist) {
				return fmt.Errorf("cannot check %s: %w", folder, err)
			}
			git, err := lookGit("git")
			if err != nil {
				return errors.New("git is not on this computer's PATH; install git (https://git-scm.com/downloads), then run this again")
			}
			ctx, out := cmd.Context(), cmd.OutOrStdout()
			e, err := runningEngine(ctx, deps, "repo clone")
			if err != nil {
				return err
			}
			repos, err := readRepos(ctx, e)
			if err != nil {
				return err
			}
			if !hasRepo(repos, name) {
				return fmt.Errorf("the instance has no local repository named %q%s", name, knownRepos(repos))
			}
			return cloneRepo(ctx, out, e, git, name, folder)
		},
	}
}

// cloneRepo streams <name>.git out as tar into a temporary folder, clones that, turns every
// branch into a local one, and drops the remote, which names a folder that is about to go.
func cloneRepo(ctx context.Context, out io.Writer, e engine.Engine, git, name, folder string) error {
	tmp, err := os.MkdirTemp("", "yawble-repo-")
	if err != nil {
		return err
	}
	defer os.RemoveAll(tmp)

	fmt.Fprintf(out, "copying local:%s out of the instance\n", name)
	pr, pw := io.Pipe()
	extracted := make(chan error, 1)
	go func() {
		err := untarRepo(pr, tmp, name+".git")
		// Drain what is left so the engine is never blocked writing into a reader that stopped.
		_, _ = io.Copy(io.Discard, pr)
		extracted <- err
	}()
	_, execErr := e.ExecTo(ctx, instance.ContainerName, pw, "tar", "-C", reposRoot, "-cf", "-", "--", name+".git")
	_ = pw.Close()
	if err := <-extracted; err != nil {
		return fmt.Errorf("local:%s could not be copied out: %w", name, err)
	}
	if execErr != nil {
		return fmt.Errorf("local:%s could not be copied out: %w", name, execErr)
	}

	bare := filepath.Join(tmp, name+".git")
	if err := runGit(ctx, git, "", "clone", "--quiet", "--no-hardlinks", "--", bare, folder); err != nil {
		_ = os.RemoveAll(folder)
		return err
	}
	branches, err := gitOutput(ctx, git, folder, "for-each-ref", "--format=%(refname:strip=3)", "refs/remotes/origin")
	if err != nil {
		return err
	}
	current, _ := gitOutput(ctx, git, folder, "symbolic-ref", "--short", "-q", "HEAD")
	current = strings.TrimSpace(current)
	var all []string
	for _, b := range strings.Fields(branches) {
		if b == "HEAD" {
			continue
		}
		all = append(all, b)
		if b == current {
			continue
		}
		if err := runGit(ctx, git, folder, "branch", "--quiet", "--no-track", "--", b, "origin/"+b); err != nil {
			return err
		}
	}
	if err := runGit(ctx, git, folder, "remote", "remove", "origin"); err != nil {
		return err
	}
	fmt.Fprintf(out, "cloned local:%s into %s", name, folder)
	if current != "" {
		fmt.Fprintf(out, " on %s", current)
	}
	fmt.Fprintf(out, " (branches: %s)\n", strings.Join(all, ", "))
	fmt.Fprintln(out, "this clone has no remote: pushing back to the instance is not supported")
	return nil
}

// untarRepo writes the tar stream into dir, accepting only folders and regular files under top/:
// what a bare repository holds. Anything else, or a path leaving top/, stops the copy.
func untarRepo(r io.Reader, dir, top string) error {
	tr := tar.NewReader(r)
	seen := false
	for {
		h, err := tr.Next()
		if errors.Is(err, io.EOF) {
			break
		}
		if err != nil {
			return fmt.Errorf("reading the copy: %w", err)
		}
		clean := path.Clean(strings.TrimPrefix(h.Name, "./"))
		if clean != top && !strings.HasPrefix(clean, top+"/") {
			return fmt.Errorf("the copy holds %q, outside %s", h.Name, top)
		}
		target := filepath.Join(dir, filepath.FromSlash(clean))
		switch h.Typeflag {
		case tar.TypeDir:
			if err := os.MkdirAll(target, 0o755); err != nil {
				return err
			}
		case tar.TypeReg:
			if err := os.MkdirAll(filepath.Dir(target), 0o755); err != nil {
				return err
			}
			f, err := os.OpenFile(target, os.O_CREATE|os.O_WRONLY|os.O_TRUNC, 0o644)
			if err != nil {
				return err
			}
			_, err = io.Copy(f, tr)
			if cerr := f.Close(); err == nil {
				err = cerr
			}
			if err != nil {
				return err
			}
		default:
			return fmt.Errorf("the copy holds %q, which is not a file or folder", h.Name)
		}
		seen = true
	}
	if !seen {
		return errors.New("the instance sent nothing")
	}
	return nil
}

func runGit(ctx context.Context, git, dir string, args ...string) error {
	_, err := gitOutput(ctx, git, dir, args...)
	return err
}

func gitOutput(ctx context.Context, git, dir string, args ...string) (string, error) {
	cmd := exec.CommandContext(ctx, git, args...)
	cmd.Dir = dir
	var stderr strings.Builder
	cmd.Stderr = &stderr
	out, err := cmd.Output()
	if err != nil {
		return "", fmt.Errorf("git %s: %s (%w)", args[0], strings.TrimSpace(stderr.String()), err)
	}
	return string(out), nil
}

// illegalRepoName is the Host's repository folder-name rule (RepoUrls.IsLegalFolderName): not
// empty, no `/` or NUL, not `.`, `..` or `.git`. `\` is refused too, because on Windows it is a
// separator, and a leading `-`, which a program would read as an option.
func illegalRepoName(name string) string {
	switch {
	case name == "":
		return "it is empty"
	case strings.ContainsAny(name, "/\\\x00"):
		return "it holds a path separator"
	case name == "." || name == "..":
		return "it names a folder, not a repository"
	case strings.EqualFold(name, ".git"):
		return "git refuses .git as a name"
	case strings.HasPrefix(name, "-"):
		return "it starts with -"
	}
	return ""
}

// localRepo is one line of `repo list`.
type localRepo struct {
	Name          string `json:"name"`
	Reference     string `json:"reference"`
	DefaultBranch string `json:"defaultBranch"`
	LastCommit    string `json:"lastCommit"`
	LastCommitAt  string `json:"lastCommitAt"`
	LastSubject   string `json:"lastSubject"`
	SizeKB        int64  `json:"sizeKB"`
}

// repoListScript runs as root in the container. $1 is the repos root. One line per <name>.git:
// name, size in KB, the branch HEAD names, then the last commit's short hash, date and subject.
// safe.directory is given on the command line because the repositories belong to harness.
const repoListScript = `cd "$1" 2>/dev/null || exit 0
g() { git -c 'safe.directory=*' --git-dir="$d" "$@"; }
for d in *.git; do
  [ -d "$d" ] || continue
  b=$(g symbolic-ref --short -q HEAD 2>/dev/null)
  c=$(g log -1 --format=%h%x09%cI%x09%s 2>/dev/null)
  s=$(du -sk "$d" | cut -f1)
  printf '%s\t%s\t%s\t%s\n' "${d%.git}" "$s" "$b" "$c"
done`

func readRepos(ctx context.Context, e engine.Engine) ([]localRepo, error) {
	res, err := e.Exec(ctx, instance.ContainerName, "sh", "-c", repoListScript, "sh", reposRoot)
	if err != nil {
		return nil, err
	}
	repos := []localRepo{}
	for _, line := range strings.Split(strings.ReplaceAll(res.Stdout, "\r\n", "\n"), "\n") {
		f := strings.SplitN(line, "\t", 6)
		if len(f) < 3 || f[0] == "" {
			continue
		}
		for len(f) < 6 {
			f = append(f, "")
		}
		size, _ := strconv.ParseInt(f[1], 10, 64)
		repos = append(repos, localRepo{Name: f[0], Reference: "local:" + f[0], SizeKB: size, DefaultBranch: f[2], LastCommit: f[3], LastCommitAt: f[4], LastSubject: f[5]})
	}
	sort.Slice(repos, func(i, j int) bool { return repos[i].Name < repos[j].Name })
	return repos, nil
}

func hasRepo(repos []localRepo, name string) bool {
	for _, r := range repos {
		if r.Name == name {
			return true
		}
	}
	return false
}

func knownRepos(repos []localRepo) string {
	if len(repos) == 0 {
		return " (it has none)"
	}
	names := make([]string, len(repos))
	for i, r := range repos {
		names[i] = r.Name
	}
	return " (it has: " + strings.Join(names, ", ") + ")"
}

func orDash(s string) string {
	if s == "" {
		return "-"
	}
	return s
}

func humanKB(kb int64) string {
	switch {
	case kb >= 1024*1024:
		return fmt.Sprintf("%.1f GB", float64(kb)/(1024*1024))
	case kb >= 1024:
		return fmt.Sprintf("%.1f MB", float64(kb)/1024)
	}
	return fmt.Sprintf("%d KB", kb)
}
