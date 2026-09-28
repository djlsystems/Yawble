package cli

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"sort"
	"strings"
	"text/tabwriter"
	"time"

	"github.com/spf13/cobra"

	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/instance"
	"github.com/djlsystems/yawble/cli/internal/plugin"
)

// dataRoot is the instance's data root in the image; pluginsRoot is where the Host reads plugins,
// <data root>/plugins.
const (
	dataRoot    = "/data"
	pluginsRoot = dataRoot + "/plugins"
)

// The Host answers a rescan request within a second (PluginRescanRequests polls once a second);
// the wait allows for a busy Host. Tests shorten both and fix the nonce.
var (
	rescanWait = 20 * time.Second
	rescanPoll = 500 * time.Millisecond
	newNonce   = func() string {
		b := make([]byte, 16)
		_, _ = rand.Read(b)
		return hex.EncodeToString(b)
	}
)

func newPluginCommand(deps Deps) *cobra.Command {
	cmd := &cobra.Command{
		Use:   "plugin",
		Short: "Install, list and remove the plugins members can be hired on",
		Long: "A plugin is a program a team member runs instead of an agent CLI (docs/plugins.md). These commands " +
			"put a built plugin into the running instance, show what the Host made of each one, and take one out. " +
			"The Host picks up every change at once: no restart, no API key.",
	}
	cmd.AddCommand(newPluginInstallCommand(deps), newPluginListCommand(deps), newPluginRemoveCommand(deps))
	return cmd
}

func newPluginInstallCommand(deps Deps) *cobra.Command {
	var force, fromInstance bool
	cmd := &cobra.Command{
		Use:   "install <folder>",
		Short: "Install a built plugin version into the instance and make it the active one",
		Long: "<folder> is one built version of a plugin: it holds plugin.json and everything the manifest names. " +
			"The manifest is checked with the Host's rules before anything is copied. The folder goes to " +
			"/data/plugins/<id>/<version>/, owned harness:agent with directories 0750, files 0640 and the " +
			"manifest's executable 0750, whatever the modes were here (a folder from Windows has no execute bit). " +
			"That version becomes active; earlier versions are kept. Then the Host rescans, and its verdict is printed.\n\n" +
			"With --from-instance, <folder> is a path inside the instance (a plugin a team built in its worktree, or " +
			"the Concierge in its workspace) and nothing is copied from this computer. It must be under the data root " +
			"(" + dataRoot + "), reached without a symlink that leaves it, and hold no symlink that leaves the folder; " +
			"the checks, modes, active switch, --force and verdict are the same.",
		Example: "  yawble plugin install ~/plugins-build/sample-echo/0.1.0\n  yawble plugin install .\\sample-echo\\0.2.0 --force\n" +
			"  yawble plugin install --from-instance /data/teams/acme/repos/Tools/main/build/sample-echo-go/0.1.0",
		Args: cobra.ExactArgs(1),
		RunE: func(cmd *cobra.Command, args []string) error {
			if fromInstance {
				return installFromInstance(cmd, deps, args[0], force)
			}
			m, err := plugin.Read(args[0])
			if err != nil {
				return fmt.Errorf("%s was not installed: %w", args[0], err)
			}
			ctx := cmd.Context()
			e, err := runningEngine(ctx, deps, "plugin install")
			if err != nil {
				return err
			}
			return install(ctx, cmd.OutOrStdout(), e, m, force, func(stage string) error {
				return e.CopyTo(ctx, instance.ContainerName, args[0], stage)
			})
		},
	}
	cmd.Flags().BoolVar(&force, "force", false, "replace this version if it is already installed")
	cmd.Flags().BoolVar(&fromInstance, "from-instance", false, "<folder> is a path inside the instance, under "+dataRoot+"; nothing is copied from this computer")
	return cmd
}

// installFromInstance checks a folder that is already inside the instance, where it is, then
// installs it with a copy made inside the container. Every refusal comes before anything is written.
func installFromInstance(cmd *cobra.Command, deps Deps, folder string, force bool) error {
	if !strings.HasPrefix(folder, "/") {
		return UsageError{fmt.Sprintf("--from-instance takes the folder's absolute path inside the instance, under %s; %q is not one", dataRoot, folder)}
	}
	ctx := cmd.Context()
	e, err := runningEngine(ctx, deps, "plugin install --from-instance")
	if err != nil {
		return err
	}
	res, err := e.Exec(ctx, instance.ContainerName, "sh", "-c", inspectScript, "sh", dataRoot, folder)
	if err != nil {
		return err
	}
	status, rest, _ := strings.Cut(res.Stdout, "\n")
	verdict, detail, _ := strings.Cut(status, "\t")
	if verdict != "ok" {
		if verdict != "refuse" {
			detail = "the instance did not answer the folder check: " + strings.TrimSpace(res.Stdout+" "+res.Stderr)
		}
		return fmt.Errorf("%s was not installed: %s", folder, detail)
	}
	source := detail
	m, skills, err := plugin.Parse([]byte(rest))
	if err != nil {
		return fmt.Errorf("%s was not installed: %w", folder, err)
	}
	check := []string{"sh", "-c", filesScript, "sh", source}
	for _, x := range m.Executables {
		check = append(check, "x:"+x)
	}
	for _, sk := range skills {
		check = append(check, "s:"+sk)
	}
	res, err = e.Exec(ctx, instance.ContainerName, check...)
	if err != nil {
		return err
	}
	if fields := strings.Split(strings.TrimSpace(res.Stdout), "\t"); fields[0] != "ok" {
		reason := "the instance did not answer the file check: " + strings.TrimSpace(res.Stdout+" "+res.Stderr)
		if len(fields) == 3 {
			switch {
			case fields[0] == "outside":
				reason = fmt.Sprintf("`%s` resolves outside %s/.", fields[2], m.Version)
			case fields[1] == "x":
				reason = fmt.Sprintf("its executable %s does not exist.", fields[2])
			default:
				reason = fmt.Sprintf("its skill %s does not exist inside %s/.", fields[2], m.Version)
			}
		}
		return fmt.Errorf("%s was not installed: %s", folder, reason)
	}
	return install(ctx, cmd.OutOrStdout(), e, m, force, func(stage string) error {
		_, err := e.Exec(ctx, instance.ContainerName, "cp", "-R", "-P", "--", source, stage)
		return err
	})
}

// install is what both install paths share once the folder has passed its checks: refuse an
// installed version without force, put the folder in a staging folder (copy), give it the Host's
// modes and make it active, then print the Host's verdict.
func install(ctx context.Context, out io.Writer, e engine.Engine, m plugin.Manifest, force bool, copy func(stage string) error) error {
	dir := pluginsRoot + "/" + m.ID
	target := dir + "/" + m.Version
	stage := dir + "/.incoming-" + m.Version

	res, err := e.Exec(ctx, instance.ContainerName, "sh", "-c", prepareScript, "sh", pluginsRoot, m.ID, m.Version, flag01(force))
	if err != nil {
		return err
	}
	if strings.TrimSpace(res.Stdout) == "exists" {
		return fmt.Errorf("%s %s is already installed in the instance (%s); run again with --force to replace it", m.ID, m.Version, target)
	}
	if err := copy(stage); err != nil {
		_, _ = e.Exec(ctx, instance.ContainerName, "rm", "-rf", stage)
		return err
	}
	place := append([]string{"sh", "-c", placeScript, "sh", pluginsRoot, m.ID, m.Version}, m.Executables...)
	if _, err := e.Exec(ctx, instance.ContainerName, place...); err != nil {
		_, _ = e.Exec(ctx, instance.ContainerName, "rm", "-rf", stage)
		return err
	}
	fmt.Fprintf(out, "copied %s %s to %s and made it the active version\n", m.ID, m.Version, target)

	report, err := rescan(ctx, e)
	if err != nil {
		return err
	}
	if reason, refused := report.refusal(m.ID); refused {
		return fmt.Errorf("the Host refused %s: %s", m.ID, reason)
	}
	if v, ok := report.installed(m.ID); !ok || v != m.Version {
		return fmt.Errorf("the Host does not list %s %s after the rescan (it lists %q)", m.ID, m.Version, v)
	}
	fmt.Fprintf(out, "the Host reports %s %s installed; hire it as plugin:%s\n", m.ID, m.Version, m.ID)
	return nil
}

func newPluginListCommand(deps Deps) *cobra.Command {
	var asJSON bool
	cmd := &cobra.Command{
		Use:     "list",
		Short:   "Each plugin version in the instance: active or not, and what the Host made of it",
		Long:    "Asks the Host to rescan, then lists every version on the volume. The active version of each plugin is installed, or refused with the Host's reason; the others are kept for going back.",
		Example: "  yawble plugin list\n  yawble plugin list --json",
		Args:    cobra.NoArgs,
		RunE: func(cmd *cobra.Command, _ []string) error {
			ctx := cmd.Context()
			e, err := runningEngine(ctx, deps, "plugin list")
			if err != nil {
				return err
			}
			report, err := rescan(ctx, e)
			if err != nil {
				return err
			}
			layout, err := readLayout(ctx, e)
			if err != nil {
				return err
			}
			rows := pluginRows(layout, report)
			if asJSON {
				enc := json.NewEncoder(cmd.OutOrStdout())
				enc.SetIndent("", "  ")
				return enc.Encode(rows)
			}
			if len(rows) == 0 {
				fmt.Fprintln(cmd.OutOrStdout(), "no plugins are installed; install one with yawble plugin install <folder>")
				return nil
			}
			w := tabwriter.NewWriter(cmd.OutOrStdout(), 0, 4, 2, ' ', 0)
			fmt.Fprintln(w, "PLUGIN\tVERSION\tACTIVE\tSTATE")
			for _, r := range rows {
				active := "no"
				if r.Active {
					active = "yes"
				}
				state := r.State
				if r.Reason != "" {
					state += ": " + r.Reason
				}
				fmt.Fprintf(w, "%s\t%s\t%s\t%s\n", r.ID, r.Version, active, state)
			}
			return w.Flush()
		},
	}
	cmd.Flags().BoolVar(&asJSON, "json", false, "print JSON")
	return cmd
}

func newPluginRemoveCommand(deps Deps) *cobra.Command {
	var version string
	var yes bool
	cmd := &cobra.Command{
		Use:   "remove <id>",
		Short: "Remove a plugin, or one version of it, from the instance",
		Long: "Removes /data/plugins/<id>, or with --version only that version. Refused while a member is hired " +
			"on the plugin (remove those members first), and refused for the active version while others are " +
			"kept. Asks first; --yes answers.",
		Example: "  yawble plugin remove sample-echo\n  yawble plugin remove sample-echo --version 0.1.0 --yes",
		Args:    cobra.ExactArgs(1),
		RunE: func(cmd *cobra.Command, args []string) error {
			id := args[0]
			if !plugin.ValidID(id) {
				return UsageError{fmt.Sprintf("%q is not a plugin id (lowercase letters, digits and hyphens)", id)}
			}
			if version != "" && !plugin.ValidVersion(version) {
				return UsageError{fmt.Sprintf("%q is not a plugin version", version)}
			}
			ctx, out := cmd.Context(), cmd.OutOrStdout()
			e, err := runningEngine(ctx, deps, "plugin remove")
			if err != nil {
				return err
			}
			layout, err := readLayout(ctx, e)
			if err != nil {
				return err
			}
			entry, ok := layout[id]
			if !ok {
				return fmt.Errorf("plugin %s is not installed in the instance", id)
			}
			if version != "" && !contains(entry.Versions, version) {
				return fmt.Errorf("plugin %s has no version %s (it has: %s)", id, version, strings.Join(entry.Versions, ", "))
			}
			whole := version == "" || len(entry.Versions) == 1
			if !whole && version == entry.inUse() {
				return fmt.Errorf("%s %s is the active version. Install the version you want to use (yawble plugin install <folder> --force), then remove this one; or remove the whole plugin", id, version)
			}
			target, what := pluginsRoot+"/"+id+"/"+version, fmt.Sprintf("version %s of plugin %s", version, id)
			if whole {
				report, err := rescan(ctx, e)
				if err != nil {
					return err
				}
				if hired := report.hiredOn(id); len(hired) > 0 {
					return fmt.Errorf("plugin %s is in use by %s; remove those members first", id, strings.Join(hired, ", "))
				}
				target, what = pluginsRoot+"/"+id, fmt.Sprintf("plugin %s (%s)", id, strings.Join(entry.Versions, ", "))
			}
			ok, err = confirm(deps, yes, out, "Remove "+what+" from the instance?")
			if err != nil {
				return err
			}
			if !ok {
				fmt.Fprintln(out, "nothing was removed")
				return nil
			}
			if _, err := e.Exec(ctx, instance.ContainerName, "rm", "-rf", "--", target); err != nil {
				return err
			}
			fmt.Fprintf(out, "removed %s\n", target)
			report, err := rescan(ctx, e)
			if err != nil {
				return err
			}
			if whole {
				_, installed := report.installed(id)
				_, refused := report.refusal(id)
				if installed || refused {
					return fmt.Errorf("the Host still lists %s after the rescan", id)
				}
				fmt.Fprintf(out, "the Host no longer lists %s\n", id)
				return nil
			}
			if v, ok := report.installed(id); ok {
				fmt.Fprintf(out, "the Host reports %s %s installed\n", id, v)
			} else if reason, refused := report.refusal(id); refused {
				fmt.Fprintf(out, "the Host refuses %s: %s\n", id, reason)
			}
			return nil
		},
	}
	cmd.Flags().StringVar(&version, "version", "", "remove only this version")
	cmd.Flags().BoolVar(&yes, "yes", false, "answer yes to the question")
	return cmd
}

// runningEngine is the instance's engine, refusing when the container is not running: every
// plugin command runs a program inside it.
func runningEngine(ctx context.Context, deps Deps, verb string) (engine.Engine, error) {
	e, _, _, err := prepare(deps)
	if err != nil {
		return nil, err
	}
	state, err := e.ContainerState(ctx, instance.ContainerName)
	if err != nil {
		return nil, err
	}
	if state != engine.StateRunning {
		return nil, fmt.Errorf("the instance is not running; run `yawble up` first, then `yawble %s`", verb)
	}
	return e, nil
}

// The scripts run as root in the container (exec's user; the entrypoint needs root, so the image
// sets no USER), and take every value as a positional argument, never spliced into the text.
const (
	// $1 root, $2 id, $3 version, $4 force: "exists" when the version is there and force is not
	// given, else clears the staging folder and answers "ready".
	prepareScript = `set -e
d="$1/$2"; t="$d/$3"; s="$d/.incoming-$3"
if [ -e "$t" ] && [ "$4" != 1 ]; then echo exists; exit 0; fi
mkdir -p "$d"
rm -rf "$s"
echo ready`

	// $1 root, $2 id, $3 version, then the executables. The staged copy gets the Host's modes
	// whatever the source had: harness:agent, directories 0750, files 0640, executables 0750. -h and
	// ! -type l leave a symlink's target alone. Directories are set symbolically: GNU chmod keeps a
	// directory's setgid bit through an octal mode. Then it replaces the version, and `active` names it.
	placeScript = `set -e
r="$1"; d="$1/$2"; v="$3"; t="$d/$3"; s="$d/.incoming-$3"; shift 3
chown -R -h harness:agent "$s"
find "$s" -type d -exec chmod u=rwx,g=rx,o=,ug-s {} +
find "$s" ! -type d ! -type l -exec chmod 0640 {} +
for x in "$@"; do chmod 0750 "$s/$x"; done
rm -rf "$t"
mv "$s" "$t"
printf '%s\n' "$v" > "$d/.active.tmp"
mv -f "$d/.active.tmp" "$d/active"
chown harness:agent "$r" "$d" "$d/active"
chmod u=rwx,g=rx,o=,ug-s "$r" "$d"
chmod 0640 "$d/active"`

	// $1 root, $2 nonce: the request the Host's PluginRescanRequests answers. Only harness and root
	// can write in root, so an agent cannot ask for one.
	requestScript = `set -e
mkdir -p "$1"
chown harness:agent "$1"
chmod u=rwx,g=rx,o=,ug-s "$1"
printf '%s\n' "$2" > "$1/.rescan"
chown harness:agent "$1/.rescan"
chmod 0640 "$1/.rescan"`

	// $1 root: one line per plugin directory: id, the active file's line, then each version folder.
	layoutScript = `cd "$1" 2>/dev/null || exit 0
for d in */; do
  d=${d%/}; [ -d "$d" ] || continue
  a=; [ -f "$d/active" ] && a=$(head -n 1 "$d/active" | tr -d '\r[:space:]')
  printf '%s\t%s' "$d" "$a"
  for v in "$d"/*/; do [ -d "$v" ] || continue; v=${v%/}; printf '\t%s' "${v##*/}"; done
  printf '\n'
done`

	reportScript = `cat "$1/.rescan-report.json" 2>/dev/null || true`

	// $1 data root, $2 the folder named to --from-instance. One line, "refuse<TAB>reason" or
	// "ok<TAB>the folder with every symlink resolved", then on "ok" the manifest. It refuses a folder
	// outside the data root as named, one a symlink leads out of it, and one holding a symlink that
	// leaves the folder: once installed, that would point the plugin at files an agent can write.
	inspectScript = `d=$(realpath -e -- "$1" 2>/dev/null) || { printf 'refuse\tthe data root %s does not exist in the instance\n' "$1"; exit 0; }
under() { case "$1" in "$2"|"$2"/*) return 0;; esac; return 1; }
named=$(realpath -m -s -- "$2")
under "$named" "$d" || { printf 'refuse\tit is outside the data root %s; build or copy the plugin under it first\n' "$1"; exit 0; }
r=$(realpath -e -- "$2" 2>/dev/null) || { printf 'refuse\tit does not exist in the instance\n'; exit 0; }
under "$r" "$d" || { printf 'refuse\tit leads through a symlink to %s, outside the data root %s\n' "$r" "$1"; exit 0; }
[ "$r" != "$d" ] || { printf 'refuse\tit is the data root itself; name the folder that holds plugin.json\n'; exit 0; }
[ -d "$r" ] || { printf 'refuse\tit is not a folder; name the folder that holds plugin.json\n'; exit 0; }
link=$(find "$r" -type l -exec sh -c 'for l; do t=$(realpath -m -- "$l"); case "$t" in "$0"/*) ;; *) printf "%s -> %s" "$l" "$t"; exit 0;; esac; done' "$r" {} +)
[ -z "$link" ] || { printf 'refuse\tit holds a symlink that leaves the folder (%s); a plugin must be self-contained\n' "$link"; exit 0; }
[ -f "$r/plugin.json" ] || { printf 'refuse\tit has no plugin.json; name a built plugin version, the folder that holds it\n'; exit 0; }
printf 'ok\t%s\n' "$r"
cat "$r/plugin.json"`

	// $1 the resolved folder, then "x:<executable>" and "s:<skill>" as the manifest names them:
	// "ok", or "outside|missing<TAB>x|s<TAB>path" for the first one that is not a file inside it.
	filesScript = `r="$1"; shift
for a; do
  k=${a%%:*}; p=${a#*:}; t=$(realpath -m -- "$r/$p")
  case "$t" in "$r"/*) ;; *) printf 'outside\t%s\t%s\n' "$k" "$p"; exit 0;; esac
  [ -f "$t" ] || { printf 'missing\t%s\t%s\n' "$k" "$p"; exit 0; }
done
echo ok`
)

// hostReport is .rescan-report.json, which PluginRescanRequests writes.
type hostReport struct {
	Request string `json:"request"`
	Plugins []struct {
		ID      string `json:"id"`
		Version string `json:"version"`
	} `json:"plugins"`
	Refused []struct {
		ID     string `json:"id"`
		Reason string `json:"reason"`
	} `json:"refused"`
	Members []struct {
		Plugin string `json:"plugin"`
		Team   string `json:"team"`
		Member string `json:"member"`
	} `json:"members"`
}

func (r hostReport) installed(id string) (string, bool) {
	for _, p := range r.Plugins {
		if p.ID == id {
			return p.Version, true
		}
	}
	return "", false
}

func (r hostReport) refusal(id string) (string, bool) {
	for _, p := range r.Refused {
		if p.ID == id {
			return p.Reason, true
		}
	}
	return "", false
}

func (r hostReport) hiredOn(id string) []string {
	var names []string
	for _, m := range r.Members {
		if m.Plugin == id {
			names = append(names, m.Team+"/"+m.Member)
		}
	}
	return names
}

// rescan asks the Host to read the plugins again and waits for the report that answers this
// request, so what is printed is the Host's verdict now and never an older one.
func rescan(ctx context.Context, e engine.Engine) (hostReport, error) {
	nonce := newNonce()
	if _, err := e.Exec(ctx, instance.ContainerName, "sh", "-c", requestScript, "sh", pluginsRoot, nonce); err != nil {
		return hostReport{}, err
	}
	deadline := time.Now().Add(rescanWait)
	for {
		res, err := e.Exec(ctx, instance.ContainerName, "sh", "-c", reportScript, "sh", pluginsRoot)
		if err != nil {
			return hostReport{}, err
		}
		var r hostReport
		if json.Unmarshal([]byte(strings.TrimSpace(res.Stdout)), &r) == nil && r.Request == nonce {
			return r, nil
		}
		if time.Now().After(deadline) {
			return hostReport{}, fmt.Errorf("the Host did not answer the plugin rescan within %s. An image from before `yawble plugin` does not; "+
				"the files are in place, and `yawble down` then `yawble up` loads them", rescanWait)
		}
		select {
		case <-ctx.Done():
			return hostReport{}, ctx.Err()
		case <-time.After(rescanPoll):
		}
	}
}

// layoutEntry is one plugin directory on the volume.
type layoutEntry struct {
	Active   string
	Versions []string
}

// inUse is the version the Host loads: `active`, or the only version when there is no `active`.
func (l layoutEntry) inUse() string {
	if l.Active == "" && len(l.Versions) == 1 {
		return l.Versions[0]
	}
	return l.Active
}

func readLayout(ctx context.Context, e engine.Engine) (map[string]layoutEntry, error) {
	res, err := e.Exec(ctx, instance.ContainerName, "sh", "-c", layoutScript, "sh", pluginsRoot)
	if err != nil {
		return nil, err
	}
	layout := map[string]layoutEntry{}
	for _, line := range strings.Split(strings.ReplaceAll(res.Stdout, "\r\n", "\n"), "\n") {
		fields := strings.Split(line, "\t")
		if len(fields) < 2 || fields[0] == "" {
			continue
		}
		versions := append([]string{}, fields[2:]...)
		sort.Strings(versions)
		layout[fields[0]] = layoutEntry{Active: fields[1], Versions: versions}
	}
	return layout, nil
}

// pluginRow is one line of `plugin list`.
type pluginRow struct {
	ID      string `json:"id"`
	Version string `json:"version"`
	Active  bool   `json:"active"`
	// State is installed or refused for the version in use, kept for the others, and refused for a
	// plugin directory the Host could not read a version from at all.
	State  string `json:"state"`
	Reason string `json:"reason,omitempty"`
}

func pluginRows(layout map[string]layoutEntry, report hostReport) []pluginRow {
	ids := make([]string, 0, len(layout))
	for id := range layout {
		ids = append(ids, id)
	}
	for _, r := range report.Refused {
		if _, ok := layout[r.ID]; !ok {
			ids = append(ids, r.ID)
		}
	}
	sort.Strings(ids)
	var rows []pluginRow
	for _, id := range ids {
		entry := layout[id]
		reason, refused := report.refusal(id)
		inUse := entry.inUse()
		if len(entry.Versions) == 0 || (refused && !contains(entry.Versions, inUse)) {
			rows = append(rows, pluginRow{ID: id, Version: "-", State: "refused", Reason: reason})
		}
		for _, v := range entry.Versions {
			row := pluginRow{ID: id, Version: v, Active: v == inUse, State: "kept"}
			if row.Active {
				row.State = "installed"
				if refused {
					row.State, row.Reason = "refused", reason
				} else if _, ok := report.installed(id); !ok {
					row.State = "not listed by the Host"
				}
			}
			rows = append(rows, row)
		}
	}
	if rows == nil {
		rows = []pluginRow{}
	}
	return rows
}

func contains(list []string, s string) bool {
	for _, x := range list {
		if x == s {
			return true
		}
	}
	return false
}

func flag01(b bool) string {
	if b {
		return "1"
	}
	return "0"
}
