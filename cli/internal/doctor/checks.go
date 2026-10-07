package doctor

import (
	"errors"
	"fmt"
	"strings"
	"time"

	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/github"
	"github.com/djlsystems/yawble/cli/internal/instance"
	"github.com/djlsystems/yawble/cli/internal/machine"
)

type Verdict string

const (
	OK   Verdict = "ok"
	Warn Verdict = "warn"
	Fail Verdict = "FAIL"
	Skip Verdict = "skip"
)

// Check is one line of the doctor's output: a verdict, what was seen, and what to do about it.
type Check struct {
	Name    string  `json:"name"`
	Verdict Verdict `json:"verdict"`
	Detail  string  `json:"detail"`
	Fix     string  `json:"fix,omitempty"`
}

// Observed is what the command layer measured on the host side. A nil pointer, or ContainerKnown
// false, means not measured, and the check for it is Skip.
type Observed struct {
	EngineName, EngineVersion string
	EngineErr                 error
	Image                     string
	ImagePresent              *bool
	VolumePresent             *bool
	PodPresent                *bool
	Container                 engine.State
	ContainerKnown            bool
	Pending                   []string
	URL                       string
	Healthy                   *bool
	Port                      int
	PortFree                  *bool
	ExeDir                    string
	OnPath                    *bool
	GOOS                      string
	// Machine is the Podman machine on macOS and Windows (Applies false elsewhere); MachineErr is
	// an inspect that failed for a reason other than "no machine". ContainerMemoryMB is the limit
	// a run would ask for, to compare with the machine's.
	Machine           machine.Info
	MachineErr        error
	ContainerMemoryMB int
	// HostMemoryMB is the computer's own RAM (not the machine's), 0 when not measured; the
	// memory fix never asks the machine for more than the computer has.
	HostMemoryMB int
	// Remote is the tunnel when one is configured: nil means remote access is not enabled.
	Remote *RemoteObserved
	// GitHub is what GitHub says about the GH_TOKEN set with `yawble secret`; nil when not asked.
	GitHub *GitHubObserved
	// Stats is the engine's own reading of the running container (`podman stats`, `docker
	// stats`): the view from outside, which answers even when the Host inside cannot. Nil with
	// StatsErr nil is not asked (the container is not running).
	Stats    *engine.Stats
	StatsErr error
}

// memoryWarnPercent is the container's memory use, of its limit, from which the stats row warns:
// a run started now may be killed for memory.
const memoryWarnPercent = 90

// statsRow is the engine's stats of the container, informational: warn only near the memory
// limit, and a stats command that failed is not measured, never a failure.
func statsRow(o Observed) Check {
	switch {
	case !o.ContainerKnown:
		return Check{"stats", Skip, "the engine could not be asked", ""}
	case o.Container != engine.StateRunning:
		return Check{"stats", Skip, "the container is not running", ""}
	case o.StatsErr != nil:
		return Check{"stats", Skip, "not measured: " + o.StatsErr.Error(), ""}
	case o.Stats == nil:
		return Check{"stats", Skip, "not measured", ""}
	case o.Stats.NotRunning:
		return Check{"stats", Skip, fmt.Sprintf("%s says the container is not running", o.Stats.Command), ""}
	}
	detail := fmt.Sprintf("%s  (%s)", o.Stats.Summary(), o.Stats.Command)
	if p := o.Stats.MemoryPercent; p != nil && *p >= memoryWarnPercent {
		return Check{"stats", Warn, detail, fmt.Sprintf("the container uses %.0f%% of its memory limit; finish or stop some runs, or yawble config set memory <more>, then yawble up", *p)}
	}
	return Check{"stats", OK, detail, ""}
}

// RemoteObserved is the configured provider, its sidecar's state and the URL it printed.
type RemoteObserved struct {
	Provider string
	State    engine.State
	URL      string
}

// ContainerMemoryMB reads a podman --memory size (12288m, 6g, 512M) as megabytes; 0 when unset
// or unreadable.
func ContainerMemoryMB(size string) int { return instance.MemoryMB(size) }

// The machine needs the container's limit plus room for its own system, and the computer keeps
// some for itself: a machine sized to the whole of it would starve Windows or macOS.
const (
	machineOverheadMB = 1024
	hostReserveMB     = 2048
)

// MachineMemoryFix is how to give the machine enough memory for a container limit: .wslconfig
// on Windows (WSL ignores podman's own figure), the machine setting on macOS. hostMB is the
// computer's RAM, 0 when not measured. The machine gets the limit plus its overhead, never more
// than the computer less its reserve; when that is not enough, the limit is lowered to fit.
// Written once here and used by both the doctor row and `up`'s note, so the two cannot drift.
func MachineMemoryFix(goos string, limitMB, hostMB int) string {
	want := limitMB + machineOverheadMB
	lower := ""
	if hostMB > 0 && want > hostMB-hostReserveMB {
		want = max((hostMB-hostReserveMB)/1024*1024, 2048)
		lower = fmt.Sprintf("; this computer has %d MB, too little for a %d MB limit, so lower the limit to fit: yawble config set memory %dm", hostMB, limitMB, want-machineOverheadMB)
	}
	if goos == "windows" {
		wantGB := (want + 1023) / 1024
		return fmt.Sprintf(`add "memory=%dGB" under [wsl2] in %%USERPROFILE%%\.wslconfig, then: wsl --shutdown; podman machine start`, wantGB) + lower
	}
	return fmt.Sprintf("podman machine stop; podman machine set --memory %d; podman machine start", want) + lower
}

// MachineMemoryNote is the whole sentence `up` prints when the machine is smaller than the limit.
func MachineMemoryNote(goos string, machineMB, limitMB, hostMB int) string {
	return fmt.Sprintf("the Podman machine has %d MB and the container limit is %d MB; runs will be memory-starved until it gets more: %s", machineMB, limitMB, MachineMemoryFix(goos, limitMB, hostMB))
}

// machineRows are the four rows that exist only where a Podman machine does.
func machineRows(o Observed) []Check {
	m := o.Machine
	if !m.Applies {
		return nil
	}
	var rows []Check
	var notRunnable *engine.NotRunnable
	switch {
	case errors.As(o.MachineErr, &notRunnable):
		// No podman at all: there is no machine to talk about, and the fix is the install.
		return []Check{{"machine", Fail, "Podman is not installed, so there is no Podman machine yet", "install Podman (recommended) or Docker, then: yawble up"}}
	case o.MachineErr != nil:
		rows = append(rows, Check{"machine", Fail, o.MachineErr.Error(), "podman machine list, and podman's own message above"})
	case !m.Exists:
		rows = append(rows, Check{"machine", Fail, "no podman machine has been created", "yawble up (it creates one, rootless)"})
	case !m.Running:
		rows = append(rows, Check{"machine", Fail, m.Name + " is stopped", "yawble up (or yawble doctor --fix)"})
	default:
		detail := fmt.Sprintf("%s, %d CPUs", m.Name, m.CPUs)
		if m.MemoryMB > 0 {
			detail += fmt.Sprintf(", %d MB", m.MemoryMB)
		}
		rows = append(rows, Check{"machine", OK, detail, ""})
	}

	if m.Exists && o.MachineErr == nil {
		if m.Rootful {
			verdict := Warn
			if o.GOOS == "windows" {
				verdict = Fail
			}
			rows = append(rows, Check{"rootless", verdict, "the machine is rootful; on Windows localhost never reaches the instance, and on macOS the machine flag is reset to rootful on every start unless it is made rootless", "podman machine stop; podman machine set --rootful=false; podman machine start   (or yawble up, which offers it)"})
		} else {
			rows = append(rows, Check{"rootless", OK, "the machine is rootless", ""})
		}

		switch {
		case !m.Running || m.MemoryMB == 0:
			rows = append(rows, Check{"machine memory", Skip, "not measured while the machine is stopped", ""})
		case o.ContainerMemoryMB > 0 && m.MemoryMB < o.ContainerMemoryMB:
			rows = append(rows, Check{"machine memory", Warn, fmt.Sprintf("the machine has %d MB and the container limit is %d MB; runs will be memory-starved", m.MemoryMB, o.ContainerMemoryMB), MachineMemoryFix(o.GOOS, o.ContainerMemoryMB, o.HostMemoryMB)})
		default:
			rows = append(rows, Check{"machine memory", OK, fmt.Sprintf("%d MB, above the container limit of %d MB", m.MemoryMB, o.ContainerMemoryMB), ""})
		}
	}

	if o.GOOS == "windows" {
		switch {
		case m.WSL == nil:
			rows = append(rows, Check{"wsl", Skip, "not measured", ""})
		case !*m.WSL:
			rows = append(rows, Check{"wsl", Fail, "WSL is not installed; the Podman machine runs in it", "wsl --install, reboot, then yawble up"})
		default:
			rows = append(rows, Check{"wsl", OK, "WSL answers", ""})
		}
	}
	return rows
}

func AnyFailed(checks []Check) bool {
	for _, c := range checks {
		if c.Verdict == Fail {
			return true
		}
	}
	return false
}

// OnPath says whether dir is one of the PATH entries, compared the way the operating system
// compares paths: case-insensitively on Windows, and ignoring a trailing separator.
func OnPath(dir, pathEnv, goos string) bool {
	separator, trim := ":", "/"
	if goos == "windows" {
		separator, trim = ";", `\/`
	}
	norm := func(p string) string {
		p = strings.TrimRight(p, trim)
		if goos == "windows" {
			p = strings.ToLower(strings.ReplaceAll(p, "/", `\`))
		}
		return p
	}
	want := norm(dir)
	for _, entry := range strings.Split(pathEnv, separator) {
		if entry == "" {
			continue
		}
		if norm(entry) == want {
			return true
		}
	}
	return false
}

// PathFix is the line that puts dir on PATH, for the shell the operating system gives people.
func PathFix(dir, goos string) string {
	if goos == "windows" {
		return fmt.Sprintf(`[Environment]::SetEnvironmentVariable("Path", "$env:Path;%s", "User")   (PowerShell; then open a new terminal)`, dir)
	}
	return fmt.Sprintf(`export PATH="%s:$PATH"   (add it to ~/.profile or your shell's rc file)`, dir)
}

// HostChecks is the table for everything measured outside the container.
func HostChecks(o Observed) []Check {
	var checks []Check
	skipEngine := "the engine could not be asked"

	// Where a machine exists, its rows come first: an engine behind a stopped machine cannot be
	// asked, and that is the machine's failure, said once.
	checks = append(checks, machineRows(o)...)
	var notRunnable *engine.NotRunnable
	podmanMissing := errors.As(o.MachineErr, &notRunnable) || errors.As(o.EngineErr, &notRunnable)
	machineDown := o.Machine.Applies && !podmanMissing && (o.MachineErr != nil || !o.Machine.Exists || !o.Machine.Running)
	if podmanMissing && o.EngineErr == nil {
		o.EngineErr = o.MachineErr
	}

	switch {
	case machineDown:
		checks = append(checks, Check{"engine", Skip, "the podman machine is not running", ""})
	case o.EngineErr == nil:
		checks = append(checks, Check{"engine", OK, o.EngineName + " " + o.EngineVersion, ""})
	case errors.As(o.EngineErr, &notRunnable):
		checks = append(checks, Check{"engine", Fail, o.EngineErr.Error(), "install Podman (recommended) or Docker and make sure it is on PATH, then: yawble up"})
	default:
		checks = append(checks, Check{"engine", Fail, "podman is installed but could not reach its engine: " + o.EngineErr.Error(), "podman machine start (macOS, Windows), or check the podman service on Linux"})
	}

	switch {
	case o.Image == "":
		checks = append(checks, Check{"image", Fail, "no image is configured and this build pins none", "yawble config set image ghcr.io/djlsystems/yawble:<version>"})
	case o.ImagePresent == nil:
		checks = append(checks, Check{"image", Skip, o.Image + " (" + skipEngine + ")", ""})
	case !*o.ImagePresent:
		checks = append(checks, Check{"image", Warn, o.Image + " is not pulled yet", "yawble up"})
	default:
		checks = append(checks, Check{"image", OK, o.Image + " present", ""})
	}

	switch {
	case o.VolumePresent == nil:
		checks = append(checks, Check{"volume", Skip, skipEngine, ""})
	case !*o.VolumePresent:
		checks = append(checks, Check{"volume", Fail, "yawble-data does not exist", "yawble up (or yawble doctor --fix)"})
	default:
		checks = append(checks, Check{"volume", OK, "yawble-data", ""})
	}

	switch {
	case !o.ContainerKnown:
		checks = append(checks, Check{"container", Skip, skipEngine, ""})
	case o.Container == engine.StateAbsent:
		checks = append(checks, Check{"container", Fail, "not created", "yawble up"})
	case o.Container == engine.StateStopped:
		checks = append(checks, Check{"container", Fail, "stopped", "yawble up (or yawble doctor --fix)"})
	case len(o.Pending) > 0:
		checks = append(checks, Check{"container", Warn, "running, but settings changed since it started: " + strings.Join(o.Pending, ", "), "yawble up"})
	default:
		checks = append(checks, Check{"container", OK, "running", ""})
	}

	switch {
	case o.Healthy == nil:
		checks = append(checks, Check{"health", Skip, "the container is not running", ""})
	case !*o.Healthy:
		checks = append(checks, Check{"health", Fail, o.URL + "/healthz does not answer 200", "yawble logs"})
	default:
		checks = append(checks, Check{"health", OK, o.URL + "/healthz answers", ""})
	}

	checks = append(checks, statsRow(o))

	if o.GitHub != nil {
		checks = append(checks, gitHubRow(*o.GitHub))
		if row, ok := contributorRow(*o.GitHub); ok {
			checks = append(checks, row)
		}
	}

	switch {
	case o.Remote == nil:
		checks = append(checks, Check{"remote", Skip, "remote access is not enabled (yawble remote enable <cloudflare|tailscale|ngrok>)", ""})
	case o.ContainerKnown && o.Container != engine.StateRunning:
		checks = append(checks, Check{"remote", Warn, o.Remote.Provider + " is configured but the instance is not running, so the tunnel points at nothing", "yawble up"})
	case o.Remote.State != engine.StateRunning:
		checks = append(checks, Check{"remote", Warn, fmt.Sprintf("%s is configured but its sidecar is %s", o.Remote.Provider, o.Remote.State), "yawble remote enable " + o.Remote.Provider})
	case o.Remote.URL == "":
		checks = append(checks, Check{"remote", Warn, o.Remote.Provider + " is running but printed no URL yet (a named Cloudflare tunnel never does; its hostname is in the dashboard)", "yawble remote status"})
	default:
		checks = append(checks, Check{"remote", OK, o.Remote.Provider + " at " + o.Remote.URL, ""})
	}

	switch {
	case o.ContainerKnown && o.Container == engine.StateRunning:
		checks = append(checks, Check{"port", Skip, "measured only while the instance is stopped", ""})
	case o.PodPresent != nil && *o.PodPresent:
		checks = append(checks, Check{"port", Skip, fmt.Sprintf("port %d is held by the yawble pod itself, which `yawble up` reuses", o.Port), ""})
	case o.PortFree == nil:
		checks = append(checks, Check{"port", Skip, "not measured", ""})
	case !*o.PortFree:
		checks = append(checks, Check{"port", Warn, fmt.Sprintf("port %d is held by something else", o.Port), "stop that, or yawble config set port <n>"})
	default:
		checks = append(checks, Check{"port", OK, fmt.Sprintf("port %d is free", o.Port), ""})
	}

	switch {
	case o.OnPath == nil:
		checks = append(checks, Check{"path", Skip, "could not tell where this binary is", ""})
	case !*o.OnPath:
		checks = append(checks, Check{"path", Warn, o.ExeDir + " is not on PATH", PathFix(o.ExeDir, o.GOOS)})
	default:
		checks = append(checks, Check{"path", OK, o.ExeDir + " is on PATH", ""})
	}

	checks = append(checks, Check{"release", Skip, "newer yawble releases are not checked in this build", ""})
	return checks
}

// InstanceChecks is the table over the Host's own report. With no report, the reason decides:
// an instance that is not running, or an image without --doctor, cannot be measured and every
// check is Skip; anything else is the Host's doctor FAILING, said once, then the skips.
func InstanceChecks(r *HostReport, err error, now time.Time) []Check {
	names := []string{"data root", "database", "backups", "agents"}
	// The Host's figures, never estimated here: with no report they are not known.
	figures := []string{"running limit", "run memory"}
	if err != nil || r == nil {
		reason := "no report"
		if err != nil {
			reason = err.Error()
		}
		var checks []Check
		if err != nil && !errors.Is(err, ErrNoReport) && !errors.Is(err, ErrNotRunning) && !errors.Is(err, ErrEngineUnavailable) {
			checks = append(checks, Check{"instance", Fail, reason, "yawble logs"})
			reason = "the Host's doctor did not answer"
		}
		for _, n := range names {
			checks = append(checks, Check{n, Skip, reason, ""})
		}
		for _, n := range figures {
			checks = append(checks, Check{n, Skip, "not known: " + reason, ""})
		}
		return checks
	}
	var checks []Check

	detail := r.DataRoot.Path
	if r.DataRoot.FreeBytes != nil {
		detail += fmt.Sprintf(", %d GB free", *r.DataRoot.FreeBytes>>30)
	}
	if r.DataRoot.Writable {
		checks = append(checks, Check{"data root", OK, detail + ", writable", ""})
	} else {
		checks = append(checks, Check{"data root", Fail, detail + ", NOT writable", "check the volume's mount and permissions; yawble logs"})
	}

	switch {
	case !r.Database.Exists:
		checks = append(checks, Check{"database", Warn, "no database yet; the Host creates it on its first start", "yawble logs"})
	case r.Database.Error != nil:
		checks = append(checks, Check{"database", Fail, r.Database.Path + ": " + *r.Database.Error, "restore a backup from " + r.Backups.Directory + " with the Host's --restore switch"})
	case r.Database.Schema == nil:
		checks = append(checks, Check{"database", Skip, "the report carried no schema", ""})
	case !r.Database.Schema.Accepted:
		checks = append(checks, Check{"database", Fail, "the volume was written by a newer build; unknown schema steps: " + strings.Join(r.Database.Schema.Unknown, ", "), "update to a newer Yawble image, or recreate the volume"})
	case len(r.Database.Schema.Pending) > 0:
		checks = append(checks, Check{"database", Warn, fmt.Sprintf("%d schema step(s) will migrate on the next start", len(r.Database.Schema.Pending)), ""})
	default:
		checks = append(checks, Check{"database", OK, fmt.Sprintf("schema accepted (%d steps)", len(r.Database.Schema.Applied)), ""})
	}

	switch {
	case r.Backups.DailyCount == 0 || r.Backups.NewestDailyAt == nil:
		checks = append(checks, Check{"backups", Warn, "no daily copy in the data volume yet (the Host writes one a day into " + r.Backups.Directory + ")", ""})
	default:
		newest, parseErr := time.Parse(time.RFC3339, *r.Backups.NewestDailyAt)
		age := now.Sub(newest)
		switch {
		case parseErr != nil:
			checks = append(checks, Check{"backups", Skip, "the newest daily copy's time could not be read: " + *r.Backups.NewestDailyAt, ""})
		case age > 48*time.Hour:
			checks = append(checks, Check{"backups", Warn, fmt.Sprintf("the newest daily copy in the data volume is %d days old (%d kept), and copies there are lost if the volume is lost", int(age.Hours()/24), r.Backups.DailyCount), "yawble logs, and check the Host is running daily"})
		default:
			checks = append(checks, Check{"backups", OK, fmt.Sprintf("%s in the data volume (newest %s), lost if the volume is lost", dailyCopies(r.Backups.DailyCount), newest.UTC().Format("2006-01-02 15:04 UTC")), ""})
		}
	}

	var parts []string
	verdict := OK
	// With one agent able to run, an agent nobody signed in is not in use, not a fault: a
	// Claude-only install is not warned about the others, and one that is signed in but did not
	// start is named, pointing at `yawble agents`, without a warning. With none able to run every
	// gap is a warning, as it was.
	anyRuns := AnyCanRun(r.Agents)
	for _, a := range r.Agents {
		var part string
		switch {
		case a.Updating != nil:
			// The platform's update holds it: not asked, never "not installed", never a failure.
			parts = append(parts, a.Agent+" updating")
			continue
		case a.Installed == nil:
			// Not measured: no worker answered the Host's probe. Never "not installed", never a failure.
			parts = append(parts, a.Agent+" not measured")
			continue
		case anyRuns && a.NotInUse():
			parts = append(parts, a.Agent+" not in use ("+a.whyNotInUse()+")")
			continue
		case !*a.Installed:
			parts = append(parts, a.Agent+" not installed")
			continue
		case a.Authenticated == nil:
			part = a.Agent + " not measured"
		case *a.Authenticated:
			part = a.Agent + " signed in"
		default:
			part = a.Agent + " NOT signed in"
			verdict = Warn
		}
		if source := a.SourceText(); source != "" {
			part += ", source " + source
		}
		if a.Issued() && a.IssuedSet != nil && !*a.IssuedSet {
			verdict = Warn
		}
		// Beside the sign-in: whether the CLI starts the way a member run launches it.
		part += ", launch " + a.LaunchText()
		if a.LaunchFailed() {
			if anyRuns {
				part += ", see yawble agents"
			} else {
				verdict = Warn
			}
		}
		parts = append(parts, part)
	}
	fix := ""
	if verdict == Warn {
		fix = "yawble agents"
	}
	summary := strings.Join(parts, " · ")
	for _, a := range r.Agents {
		if measured := a.MeasuredText(); measured != "" {
			summary += " (sign-ins measured " + measured + ")"
			break
		}
	}
	checks = append(checks, Check{"agents", verdict, summary, fix})
	if row, ok := versionsRow(r.Agents); ok {
		checks = append(checks, row)
	}
	checks = append(checks, runLimitRow(r), runMemoryRow(r))
	checks = append(checks, ConciergeRows(r)...)
	return checks
}

// versionsRow says which version of each agent CLI is installed and when it last changed. The
// platform updates them only at start and when a person asks, so this is where a person sees what
// that came to. Information, never a verdict; absent when the report carried no version at all.
func versionsRow(agents []Agent) (Check, bool) {
	var parts []string
	for _, a := range agents {
		if !a.IsModelAgent() || !a.IsInstalled() {
			continue
		}
		if text := a.UpdatedText(); text != "" {
			parts = append(parts, a.Agent+" "+text)
		}
	}
	if len(parts) == 0 {
		return Check{}, false
	}
	return Check{"agent versions", Info, strings.Join(parts, " · "), ""}, true
}

// SignInHint is what a person does about one agent, or "" when nothing is needed.
func SignInHint(a Agent) string {
	switch {
	case a.Installed == nil:
		return ""
	case !*a.Installed:
		return "not installed in the instance; a worker installs it when it starts: yawble down, then yawble up"
	case a.Issued() && a.IssuedSet != nil && !*a.IssuedSet:
		return "its source is issued and no credential is set: yawble agents credential set " + a.Agent +
			" (or yawble agents source " + a.Agent + " home)"
	case a.Authenticated != nil && *a.Authenticated && a.LaunchFailed():
		return "it is signed in but did not start: yawble agents --details shows why; yawble down, then yawble up starts it again"
	case a.Authenticated != nil && *a.Authenticated:
		return ""
	case a.Issued():
		return "its source is issued: replace its credential with yawble agents credential set " + a.Agent
	}
	concierge := "open a Concierge on " + a.Agent + " in the board and sign in there"
	if a.CredentialVariable != nil && *a.CredentialVariable != "" {
		return "yawble secret set " + *a.CredentialVariable + ", then yawble up; or " + concierge
	}
	return concierge
}

// GitHubObserved is what GitHub said about the GH_TOKEN set with `yawble secret`: not set, its
// answer to the token (200 with the account, 401 for a revoked or expired one), or unreachable.
type GitHubObserved struct {
	Set         bool
	Status      int
	Login       string
	Unreachable bool
	// Kind and Scopes are what the token is (internal/github.KindOf) and GitHub's X-OAuth-Scopes.
	Kind   string
	Scopes []string
	// ContributorReady: a classic or OAuth token with public_repo or repo.
	ContributorReady bool
}

// gitHubRow says whether agents can reach GitHub with the token they will be given, so a
// revoked GH_TOKEN is reported here rather than by a clone failing inside a run.
func gitHubRow(g GitHubObserved) Check {
	switch {
	case !g.Set:
		return Check{"github", Skip, "GH_TOKEN is not set, so agents cannot push to GitHub or open pull requests", "yawble github (or yawble secret set GH_TOKEN), then yawble up"}
	case g.Unreachable:
		return Check{"github", Skip, "GH_TOKEN is set; could not reach GitHub to check it", ""}
	case g.Status == 200:
		return Check{"github", OK, "GitHub accepts GH_TOKEN (as " + g.Login + ")", ""}
	case g.Status == 401:
		return Check{"github", Fail, "GH_TOKEN is set, but GitHub rejects it (revoked or expired)", "yawble github (or yawble secret set GH_TOKEN) with a new token, then yawble up"}
	}
	return Check{"github", Warn, fmt.Sprintf("GH_TOKEN is set; GitHub answered %d when asked who it is", g.Status), "yawble secret set GH_TOKEN with a new token, then yawble up"}
}

// contributorRow says whether the token can do what contributor mode asks of it: fork a
// project the token's owner does not own and open a pull request on it. Shown only once GitHub has
// accepted the token; a missing or rejected token is the github row's to say.
func contributorRow(g GitHubObserved) (Check, bool) {
	if !g.Set || g.Unreachable || g.Status != 200 {
		return Check{}, false
	}
	if g.ContributorReady {
		return Check{"contributor", OK, "GH_TOKEN is a " + g.Kind + " token with " + strings.Join(g.Scopes, ", ") + ": contributor mode can fork and open pull requests", ""}, true
	}
	if g.Kind == github.KindFineGrained {
		return Check{"contributor", Warn, "GH_TOKEN is a fine-grained token; " + github.ContributorNeeds, "only teams that contribute to a project they cannot push to need it: yawble github with a classic token, then yawble up"}, true
	}
	return Check{"contributor", Warn, "GH_TOKEN (" + g.Kind + ") has no public_repo or repo scope; " + github.ContributorNeeds, "only teams that contribute to a project they cannot push to need it: yawble github with a classic token, then yawble up"}, true
}

// OtherVolumeCheck names a data volume on an engine this instance does not run on: data a
// person may believe gone, which a later `up` on that engine would take over.
func OtherVolumeCheck(selected, other, volume string) Check {
	return Check{
		Name:    "data volume on " + other,
		Verdict: Warn,
		Detail:  fmt.Sprintf("%s also holds a data volume %s, which this instance on %s does not use", other, volume, selected),
		Fix:     fmt.Sprintf("%s volume rm %s removes it and everything on it; yawble config set engine %s uses it instead", other, instance.VolumeName, other),
	}
}
