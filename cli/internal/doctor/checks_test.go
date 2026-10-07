package doctor_test

import (
	"bytes"
	"errors"
	"strings"
	"testing"
	"time"

	"github.com/djlsystems/yawble/cli/internal/doctor"
	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/machine"
)

func boolp(b bool) *bool    { return &b }
func strp(s string) *string { return &s }
func int64p(n int64) *int64 { return &n }
func find(t *testing.T, checks []doctor.Check, name string) doctor.Check {
	t.Helper()
	for _, c := range checks {
		if c.Name == name {
			return c
		}
	}
	t.Fatalf("no check named %q in %+v", name, checks)
	return doctor.Check{}
}

func healthyObserved() doctor.Observed {
	return doctor.Observed{
		EngineName: "podman", EngineVersion: "6.0.2",
		Image: "ghcr.io/djlsystems/yawble:2026.09.24.1", ImagePresent: boolp(true),
		VolumePresent: boolp(true),
		Container:     engine.StateRunning, ContainerKnown: true,
		URL: "http://127.0.0.1:8080", Healthy: boolp(true),
		Port:   8080,
		ExeDir: "/home/d/.local/bin", OnPath: boolp(true),
		ContainerMemoryMB: 12288,
		Stats:             &engine.Stats{Command: "podman stats", CPUPercent: float64p(12.3), MemoryUsage: "1.2GB", MemoryLimit: "12.88GB", MemoryPercent: float64p(9.4), PIDs: intp(412)},
	}
}

func float64p(f float64) *float64 { return &f }
func intp(n int) *int             { return &n }

// The engine's own stats are a row of their own: informational, warn near the memory limit,
// and never a failure, whatever went wrong asking.
func TestTheStatsRowSaysWhatTheEngineMeasuredAndNeverFails(t *testing.T) {
	o := healthyObserved()
	row := find(t, doctor.HostChecks(o), "stats")
	if row.Verdict != doctor.OK || row.Detail != "cpu 12.3%  memory 1.2GB / 12.88GB (9%)  pids 412  (podman stats)" {
		t.Errorf("healthy: %+v", row)
	}

	o.Stats = &engine.Stats{Command: "docker stats", CPUPercent: float64p(250), MemoryUsage: "7.5GiB", MemoryLimit: "8GiB", MemoryPercent: float64p(93.75)}
	row = find(t, doctor.HostChecks(o), "stats")
	if row.Verdict != doctor.Warn || !strings.Contains(row.Detail, "pids not measured  (docker stats)") || !strings.Contains(row.Fix, "94% of its memory limit") {
		t.Errorf("near the limit: %+v", row)
	}

	o.Stats, o.StatsErr = nil, errors.New("podman stats yawble: cgroups v1 is not supported (exit 125)")
	row = find(t, doctor.HostChecks(o), "stats")
	if row.Verdict != doctor.Skip || row.Detail != "not measured: podman stats yawble: cgroups v1 is not supported (exit 125)" {
		t.Errorf("error: %+v", row)
	}

	o.StatsErr = nil
	o.Container = engine.StateStopped
	if row := find(t, doctor.HostChecks(o), "stats"); row.Verdict != doctor.Skip || row.Detail != "the container is not running" {
		t.Errorf("stopped: %+v", row)
	}
}

func TestAHealthyInstancePassesEveryHostCheckExceptTheSkippedRelease(t *testing.T) {
	checks := doctor.HostChecks(healthyObserved())
	for _, c := range checks {
		switch c.Name {
		case "release":
			if c.Verdict != doctor.Skip {
				t.Errorf("%s: %+v", c.Name, c)
			}
		case "port", "remote":
			if c.Verdict != doctor.Skip { // port: not measured while the container runs; remote: not enabled
				t.Errorf("%s: %+v", c.Name, c)
			}
		default:
			if c.Verdict != doctor.OK {
				t.Errorf("%s: %+v", c.Name, c)
			}
		}
	}
	if doctor.AnyFailed(checks) {
		t.Error("nothing should have failed")
	}
}

// No podman at all. The engine fails with the install hint; everything that
// needs the engine is skip, not fail.
func TestNoEngineFailsOnceAndSkipsTheRest(t *testing.T) {
	o := doctor.Observed{EngineErr: &engine.NotRunnable{Err: errors.New(`exec: "podman": executable file not found in $PATH`)}, Image: "x:1", Port: 8080}
	checks := doctor.HostChecks(o)
	e := find(t, checks, "engine")
	if e.Verdict != doctor.Fail || !strings.Contains(e.Fix, "install Podman") {
		t.Errorf("engine: %+v", e)
	}
	for _, name := range []string{"image", "volume", "container", "health"} {
		if c := find(t, checks, name); c.Verdict != doctor.Skip {
			t.Errorf("%s should be skip, got %+v", name, c)
		}
	}
	if !doctor.AnyFailed(checks) {
		t.Error("a missing engine is a failure")
	}
}

// Podman installed but its engine unreachable is the common macOS and Windows state (the machine
// is not running). Telling that person to install Podman is wrong.
func TestAnUnreachableEngineIsNotAnInstallProblem(t *testing.T) {
	o := healthyObserved()
	o.EngineErr = errors.New("podman version --format: Cannot connect to Podman (exit 125)")
	c := find(t, doctor.HostChecks(o), "engine")
	if c.Verdict != doctor.Fail || strings.Contains(c.Fix, "install") || !strings.Contains(c.Fix, "podman machine start") {
		t.Errorf("%+v", c)
	}
	o.EngineErr = &engine.NotRunnable{Err: errors.New(`exec: "podman": executable file not found`)}
	c = find(t, doctor.HostChecks(o), "engine")
	if c.Verdict != doctor.Fail || !strings.Contains(c.Fix, "install Podman") || strings.Count(c.Detail, "could not be run") != 1 {
		t.Errorf("%+v", c)
	}
}

func TestOnPathComparesTheWayTheOperatingSystemDoes(t *testing.T) {
	if !doctor.OnPath("/home/d/.local/bin", "/usr/bin:/home/d/.local/bin", "linux") {
		t.Error("linux: exact entry should match")
	}
	if doctor.OnPath("/home/d/.local/bin", "/usr/bin:/home/d/.LOCAL/bin", "linux") {
		t.Error("linux is case-sensitive")
	}
	if !doctor.OnPath(`C:\Users\d\bin`, `C:\Windows;c:\users\D\BIN\`, "windows") {
		t.Error("windows: case and a trailing separator must not matter")
	}
}

func TestThePathFixLineIsForTheShellThePersonHas(t *testing.T) {
	if fix := doctor.PathFix("/home/d/.local/bin", "linux"); !strings.Contains(fix, `export PATH="/home/d/.local/bin:$PATH"`) {
		t.Errorf("linux: %q", fix)
	}
	if fix := doctor.PathFix(`C:\Users\d\bin`, "windows"); strings.Contains(fix, "export") || !strings.Contains(fix, `C:\Users\d\bin`) || !strings.Contains(fix, "Path") {
		t.Errorf("windows: %q", fix)
	}
}

// A Host that has --doctor and fails anyway is the instance failing, not a skip.
func TestAFailedHostDoctorIsAFailureNotASkip(t *testing.T) {
	checks := doctor.InstanceChecks(nil, errors.New("the Host's doctor could not be run: podman exec yawble: Unhandled exception (exit 134)"), now)
	first := checks[0]
	if first.Name != "instance" || first.Verdict != doctor.Fail || !strings.Contains(first.Detail, "Unhandled exception") || !strings.Contains(first.Fix, "yawble logs") {
		t.Errorf("first: %+v", first)
	}
	for _, c := range checks[1:] {
		if c.Verdict != doctor.Skip {
			t.Errorf("%+v", c)
		}
	}
	if !doctor.AnyFailed(checks) {
		t.Error("must count as failed")
	}
	// The two known non-failures stay skips with no FAIL line.
	for _, known := range []error{doctor.ErrNoReport, doctor.ErrNotRunning} {
		if doctor.AnyFailed(doctor.InstanceChecks(nil, known, now)) {
			t.Errorf("%v must not fail", known)
		}
	}
}

func winMachine() machine.Info {
	wsl := true
	return machine.Info{Applies: true, Exists: true, Running: true, Name: "podman-machine-default", CPUs: 10, MemoryMB: 15688, MemorySource: "wsl", WSL: &wsl}
}

func TestLinuxHasNoMachineRows(t *testing.T) {
	o := healthyObserved()
	for _, c := range doctor.HostChecks(o) {
		if c.Name == "machine" || c.Name == "rootless" || c.Name == "machine memory" || c.Name == "wsl" {
			t.Errorf("linux must not show %+v", c)
		}
	}
}

func TestAHealthyWindowsMachinePassesItsFourRows(t *testing.T) {
	o := healthyObserved()
	o.GOOS, o.Machine, o.ContainerMemoryMB = "windows", winMachine(), 8192
	checks := doctor.HostChecks(o)
	for _, name := range []string{"machine", "rootless", "machine memory", "wsl"} {
		if c := find(t, checks, name); c.Verdict != doctor.OK {
			t.Errorf("%s: %+v", name, c)
		}
	}
	if c := find(t, checks, "machine"); !strings.Contains(c.Detail, "15688 MB") || !strings.Contains(c.Detail, "10 CPUs") {
		t.Errorf("machine detail %q", c.Detail)
	}
}

// A stopped machine.
func TestAStoppedMachineFailsWithUpAndTheEngineRowsAreSkipped(t *testing.T) {
	o := healthyObserved()
	o.GOOS, o.Machine = "windows", winMachine()
	o.Machine.Running, o.Machine.MemoryMB, o.Machine.MemorySource = false, 0, ""
	o.EngineVersion, o.ImagePresent, o.VolumePresent, o.ContainerKnown, o.Healthy = "", nil, nil, false, nil
	checks := doctor.HostChecks(o)
	if c := find(t, checks, "machine"); c.Verdict != doctor.Fail || !strings.Contains(c.Detail, "stopped") || !strings.Contains(c.Fix, "doctor --fix") {
		t.Errorf("machine: %+v", c)
	}
	if c := find(t, checks, "machine memory"); c.Verdict != doctor.Skip {
		t.Errorf("memory of a stopped machine cannot be measured: %+v", c)
	}
	for _, name := range []string{"engine", "image", "volume", "container", "health"} {
		if c := find(t, checks, name); c.Verdict != doctor.Skip {
			t.Errorf("%s should be skip while the machine is stopped, got %+v", name, c)
		}
	}
}

// Podman not installed on a machine OS: the machine row says so and offers the install; the
// engine row does not blame a machine that cannot exist yet.
func TestPodmanMissingOnAMachineOSIsSaidOnceAndNotBlamedOnTheMachine(t *testing.T) {
	o := healthyObserved()
	o.GOOS, o.Machine = "darwin", machine.Info{Applies: true}
	o.MachineErr = &engine.NotRunnable{Err: errors.New(`exec: "podman": executable file not found`)}
	o.EngineErr = o.MachineErr
	checks := doctor.HostChecks(o)
	m := find(t, checks, "machine")
	if m.Verdict != doctor.Fail || !strings.Contains(m.Detail, "not installed") || !strings.Contains(m.Fix, "yawble up") || strings.Contains(m.Fix, "machine list") {
		t.Errorf("machine: %+v", m)
	}
	e := find(t, checks, "engine")
	if e.Verdict != doctor.Fail || strings.Contains(e.Detail, "machine is not running") || !strings.Contains(e.Fix, "install Podman") {
		t.Errorf("engine: %+v", e)
	}
	for _, name := range []string{"rootless", "machine memory"} {
		for _, c := range checks {
			if c.Name == name {
				t.Errorf("%s should not be shown when podman is missing: %+v", name, c)
			}
		}
	}
}

func TestNoMachineFailsWithUpWhichCreatesIt(t *testing.T) {
	o := healthyObserved()
	o.GOOS, o.Machine = "darwin", machine.Info{Applies: true}
	c := find(t, doctor.HostChecks(o), "machine")
	if c.Verdict != doctor.Fail || !strings.Contains(c.Detail, "no podman machine") || !strings.Contains(c.Fix, "yawble up") {
		t.Errorf("%+v", c)
	}
}

// At the table: a broken inspect is podman's sentence, not "create the machine".
func TestABrokenMachineInspectFailsWithPodmansSentence(t *testing.T) {
	o := healthyObserved()
	o.GOOS, o.Machine, o.MachineErr = "windows", machine.Info{Applies: true}, errors.New("podman machine inspect: cannot read machine config: permission denied (exit 125)")
	c := find(t, doctor.HostChecks(o), "machine")
	if c.Verdict != doctor.Fail || !strings.Contains(c.Detail, "permission denied") || strings.Contains(c.Fix, "yawble up") {
		t.Errorf("%+v", c)
	}
}

// Rootful is FAIL on Windows (localhost never answers) and warn on macOS.
func TestARootfulMachineFailsOnWindowsAndWarnsOnMac(t *testing.T) {
	o := healthyObserved()
	o.GOOS, o.Machine = "windows", winMachine()
	o.Machine.Rootful = true
	c := find(t, doctor.HostChecks(o), "rootless")
	if c.Verdict != doctor.Fail || !strings.Contains(c.Fix, "podman machine set --rootful=false") {
		t.Errorf("windows: %+v", c)
	}
	o.GOOS = "darwin"
	if c := find(t, doctor.HostChecks(o), "rootless"); c.Verdict != doctor.Warn {
		t.Errorf("darwin: %+v", c)
	}
}

// The VM has less memory than the container limit.
func TestASmallWSLMachineWarnsWithTheWslconfigLine(t *testing.T) {
	o := healthyObserved()
	o.GOOS, o.Machine, o.ContainerMemoryMB = "windows", winMachine(), 8192
	o.Machine.MemoryMB = 4096
	c := find(t, doctor.HostChecks(o), "machine memory")
	if c.Verdict != doctor.Warn || !strings.Contains(c.Detail, "4096 MB") || !strings.Contains(c.Detail, "8192 MB") || !strings.Contains(c.Fix, ".wslconfig") || !strings.Contains(c.Fix, "memory=9GB") || !strings.Contains(c.Fix, "wsl --shutdown") {
		t.Errorf("%+v", c)
	}
	o.GOOS = "darwin"
	o.Machine.MemorySource = "machine"
	if c := find(t, doctor.HostChecks(o), "machine memory"); c.Verdict != doctor.Warn || !strings.Contains(c.Fix, "podman machine set --memory 9216") {
		t.Errorf("darwin: %+v", c)
	}
}

// The fix must never ask for more memory than the computer has (e.g. "memory=16GB" on a 12 GB
// computer). What the machine needs is the limit and 1 GB for its own system, and it can
// never be given more than the computer has less 2 GB for the computer itself.
func TestTheMemoryFixNeverAsksForMoreThanTheComputerHas(t *testing.T) {
	cases := []struct {
		goos             string
		limitMB, hostMB  int
		want, mustNotSay string
	}{
		{"windows", 8192, 12288, "memory=9GB", "config set memory"}, // fits: limit + 1 GB
		{"windows", 8192, 0, "memory=9GB", "config set memory"},     // host not measured: no cap
		{"windows", 8192, 8192, "memory=6GB", "memory=9GB"},         // too big: 8 GB less 2, and lower the limit
		{"darwin", 8192, 8192, "podman machine set --memory 6144", "9216"},
	}
	for _, c := range cases {
		fix := doctor.MachineMemoryFix(c.goos, c.limitMB, c.hostMB)
		if !strings.Contains(fix, c.want) || strings.Contains(fix, c.mustNotSay) {
			t.Errorf("%s limit %d host %d: %q", c.goos, c.limitMB, c.hostMB, fix)
		}
	}
	if fix := doctor.MachineMemoryFix("windows", 8192, 8192); !strings.Contains(fix, "yawble config set memory 5120m") {
		t.Errorf("a host too small for the limit must lower the limit to fit: %q", fix)
	}
}

func TestMissingWSLFailsWithTheInstallLine(t *testing.T) {
	o := healthyObserved()
	o.GOOS, o.Machine = "windows", machine.Info{Applies: true}
	no := false
	o.Machine.WSL = &no
	c := find(t, doctor.HostChecks(o), "wsl")
	if c.Verdict != doctor.Fail || !strings.Contains(c.Fix, "wsl --install") {
		t.Errorf("%+v", c)
	}
}

func TestContainerMemoryMBParsesPodmanSizes(t *testing.T) {
	for in, want := range map[string]int{"12288m": 12288, "6g": 6144, "512m": 512, "1G": 1024, "": 0, "nonsense": 0} {
		if got := doctor.ContainerMemoryMB(in); got != want {
			t.Errorf("%q: got %d want %d", in, got, want)
		}
	}
}

func TestTheRemoteRowFollowsTheSidecar(t *testing.T) {
	o := healthyObserved()
	if c := find(t, doctor.HostChecks(o), "remote"); c.Verdict != doctor.Skip || !strings.Contains(c.Detail, "not enabled") {
		t.Errorf("nothing enabled: %+v", c)
	}
	o.Remote = &doctor.RemoteObserved{Provider: "cloudflare", State: engine.StateRunning, URL: "https://quiet-owl.trycloudflare.com"}
	if c := find(t, doctor.HostChecks(o), "remote"); c.Verdict != doctor.OK || !strings.Contains(c.Detail, "https://quiet-owl.trycloudflare.com") {
		t.Errorf("running: %+v", c)
	}
	o.Remote = &doctor.RemoteObserved{Provider: "ngrok", State: engine.StateAbsent}
	if c := find(t, doctor.HostChecks(o), "remote"); c.Verdict != doctor.Warn || !strings.Contains(c.Fix, "yawble remote enable ngrok") {
		t.Errorf("configured but gone: %+v", c)
	}
	o.Remote = &doctor.RemoteObserved{Provider: "cloudflare", State: engine.StateRunning}
	if c := find(t, doctor.HostChecks(o), "remote"); c.Verdict != doctor.Warn || !strings.Contains(c.Detail, "no URL") {
		t.Errorf("running without a URL yet: %+v", c)
	}
	// A tunnel in front of a stopped Host points at nothing: not OK, whatever the sidecar says.
	o.Container, o.Healthy = engine.StateStopped, nil
	o.Remote = &doctor.RemoteObserved{Provider: "cloudflare", State: engine.StateRunning, URL: "https://x.trycloudflare.com"}
	if c := find(t, doctor.HostChecks(o), "remote"); c.Verdict != doctor.Warn || !strings.Contains(c.Detail, "instance is not running") {
		t.Errorf("instance down: %+v", c)
	}
}

func TestNoImageConfiguredFailsWithTheSetting(t *testing.T) {
	o := healthyObserved()
	o.Image, o.ImagePresent = "", nil
	c := find(t, doctor.HostChecks(o), "image")
	if c.Verdict != doctor.Fail || !strings.Contains(c.Fix, "config set image") {
		t.Errorf("%+v", c)
	}
}

func TestAnUnpulledImageIsAWarningNotAFailure(t *testing.T) {
	o := healthyObserved()
	o.ImagePresent = boolp(false)
	c := find(t, doctor.HostChecks(o), "image")
	if c.Verdict != doctor.Warn || !strings.Contains(c.Fix, "yawble up") {
		t.Errorf("%+v", c)
	}
}

// A stopped container fails with `yawble up`, and health cannot be measured.
func TestAStoppedContainerFailsAndHealthIsSkipped(t *testing.T) {
	o := healthyObserved()
	o.Container, o.Healthy, o.PortFree = engine.StateStopped, nil, boolp(true)
	checks := doctor.HostChecks(o)
	if c := find(t, checks, "container"); c.Verdict != doctor.Fail || !strings.Contains(c.Fix, "yawble up") {
		t.Errorf("container: %+v", c)
	}
	if c := find(t, checks, "health"); c.Verdict != doctor.Skip {
		t.Errorf("health: %+v", c)
	}
	if c := find(t, checks, "port"); c.Verdict != doctor.OK {
		t.Errorf("port: %+v", c)
	}
}

func TestAHeldPortWhileStoppedIsAWarningNamingTheSetting(t *testing.T) {
	o := healthyObserved()
	o.Container, o.Healthy, o.PortFree = engine.StateAbsent, nil, boolp(false)
	c := find(t, doctor.HostChecks(o), "port")
	if c.Verdict != doctor.Warn || !strings.Contains(c.Detail, "8080") || !strings.Contains(c.Fix, "config set port") {
		t.Errorf("%+v", c)
	}
}

func TestPendingSettingsOnARunningContainerAreAWarning(t *testing.T) {
	o := healthyObserved()
	o.Pending = []string{"port 8080 -> 9090"}
	c := find(t, doctor.HostChecks(o), "container")
	if c.Verdict != doctor.Warn || !strings.Contains(c.Detail, "port 8080 -> 9090") || !strings.Contains(c.Fix, "yawble up") {
		t.Errorf("%+v", c)
	}
}

func TestABinaryOffPathIsAWarningWithTheExportLine(t *testing.T) {
	o := healthyObserved()
	o.OnPath = boolp(false)
	c := find(t, doctor.HostChecks(o), "path")
	if c.Verdict != doctor.Warn || !strings.Contains(c.Fix, `export PATH="/home/d/.local/bin:$PATH"`) {
		t.Errorf("%+v", c)
	}
}

func TestUnhealthyIsAFailurePointingAtLogs(t *testing.T) {
	o := healthyObserved()
	o.Healthy = boolp(false)
	c := find(t, doctor.HostChecks(o), "health")
	if c.Verdict != doctor.Fail || !strings.Contains(c.Fix, "yawble logs") {
		t.Errorf("%+v", c)
	}
}

func sampleReport() *doctor.HostReport {
	r := &doctor.HostReport{}
	r.DataRoot.Path, r.DataRoot.Writable, r.DataRoot.FreeBytes = "/data", true, int64p(900<<30)
	r.Database.Path, r.Database.Exists = "/data/messages.db", true
	r.Database.Schema = &doctor.Schema{Applied: []string{"auth-001", "messages-001"}, Accepted: true}
	r.Backups.DailyCount, r.Backups.NewestDailyAt = 1, strp("2026-09-23T18:47:24+00:00")
	r.Agents = []doctor.Agent{
		{Agent: "claude", Installed: boolp(true), Version: strp("2.1.280"), Authenticated: boolp(true), Detail: "saved login", CredentialVariable: strp("ANTHROPIC_API_KEY")},
		{Agent: "codex", Installed: boolp(true), Authenticated: boolp(false), Detail: "The status command exited 1.", CredentialVariable: strp("OPENAI_API_KEY")},
		{Agent: "copilot", Installed: boolp(true), Detail: "no probe", CredentialVariable: strp("GH_TOKEN")},
		{Agent: "agy", Installed: boolp(false), Detail: "'agy' is not on PATH."},
	}
	return r
}

var now = time.Date(2026, 9, 23, 20, 0, 0, 0, time.UTC)

func TestAGoodReportPassesWithTheAgentsSummarised(t *testing.T) {
	checks := doctor.InstanceChecks(sampleReport(), nil, now)
	for _, name := range []string{"data root", "database", "backups"} {
		if c := find(t, checks, name); c.Verdict != doctor.OK {
			t.Errorf("%s: %+v", name, c)
		}
	}
	a := find(t, checks, "agents")
	if a.Verdict != doctor.Warn || !strings.Contains(a.Detail, "codex NOT signed in") || !strings.Contains(a.Detail, "claude signed in") || !strings.Contains(a.Detail, "agy not installed") || !strings.Contains(a.Detail, "copilot not measured") {
		t.Errorf("agents: %+v", a)
	}
	if !strings.Contains(find(t, checks, "database").Detail, "2 steps") {
		t.Errorf("database detail should count steps: %+v", find(t, checks, "database"))
	}
}

// A schema the Host does not accept names the steps and the cause.
func TestAnUnacceptedSchemaFailsNamingTheUnknownSteps(t *testing.T) {
	r := sampleReport()
	r.Database.Schema = &doctor.Schema{Applied: []string{"auth-001", "zzz-999"}, Unknown: []string{"zzz-999"}, Accepted: false}
	c := find(t, doctor.InstanceChecks(r, nil, now), "database")
	if c.Verdict != doctor.Fail || !strings.Contains(c.Detail, "zzz-999") || !strings.Contains(c.Detail, "newer build") {
		t.Errorf("%+v", c)
	}
}

func TestADatabaseSqliteRefusesFails(t *testing.T) {
	r := sampleReport()
	r.Database.Schema, r.Database.Error = nil, strp("file is not a database")
	c := find(t, doctor.InstanceChecks(r, nil, now), "database")
	if c.Verdict != doctor.Fail || !strings.Contains(c.Detail, "file is not a database") {
		t.Errorf("%+v", c)
	}
}

func TestNoBackupsAndOldBackupsWarn(t *testing.T) {
	r := sampleReport()
	r.Backups.DailyCount, r.Backups.NewestDailyAt = 0, nil
	if c := find(t, doctor.InstanceChecks(r, nil, now), "backups"); c.Verdict != doctor.Warn {
		t.Errorf("none: %+v", c)
	}
	r.Backups.DailyCount, r.Backups.NewestDailyAt = 3, strp("2026-09-20T03:00:00+00:00")
	if c := find(t, doctor.InstanceChecks(r, nil, now), "backups"); c.Verdict != doctor.Warn || !strings.Contains(c.Detail, "3 days") {
		t.Errorf("old: %+v", c)
	}
}

// No report at all (an image without --doctor) is skip everywhere, with the reason.
func TestNoReportSkipsEveryInstanceCheckWithTheReason(t *testing.T) {
	checks := doctor.InstanceChecks(nil, doctor.ErrNoReport, now)
	if len(checks) < 4 {
		t.Fatalf("checks %+v", checks)
	}
	for _, c := range checks {
		if c.Verdict != doctor.Skip || !strings.Contains(c.Detail, "predates") {
			t.Errorf("%+v", c)
		}
	}
	if doctor.AnyFailed(checks) {
		t.Error("skip is not failure")
	}
}

func TestRenderAlignsVerdictsAndPutsTheFixUnderneath(t *testing.T) {
	var out bytes.Buffer
	doctor.Render(&out, []doctor.Check{
		{Name: "engine", Verdict: doctor.OK, Detail: "podman 6.0.2"},
		{Name: "container", Verdict: doctor.Fail, Detail: "absent", Fix: "yawble up"},
	})
	text := out.String()
	if !strings.Contains(text, "ok    engine         podman 6.0.2") || !strings.Contains(text, "FAIL  container      absent") || !strings.Contains(text, "fix: yawble up") {
		t.Errorf("rendered:\n%s", text)
	}
}

func TestSignInHintsSayWhatToDo(t *testing.T) {
	r := sampleReport()
	if h := doctor.SignInHint(r.Agents[0]); h != "" {
		t.Errorf("signed in needs no hint, got %q", h)
	}
	h := doctor.SignInHint(r.Agents[1])
	if !strings.Contains(h, "Concierge on codex") || !strings.Contains(h, "OPENAI_API_KEY") {
		t.Errorf("codex: %q", h)
	}
	if h := doctor.SignInHint(r.Agents[3]); !strings.Contains(h, "not installed") {
		t.Errorf("agy: %q", h)
	}
}

// A revoked GH_TOKEN would otherwise surface only when a clone fails. doctor asks GitHub whether the token set with `yawble secret` still works.
func TestTheGitHubRowSaysWhetherGitHubAcceptsTheToken(t *testing.T) {
	cases := []struct {
		name    string
		github  *doctor.GitHubObserved
		verdict doctor.Verdict
		detail  string
		fix     string
	}{
		{"not set", &doctor.GitHubObserved{}, doctor.Skip, "GH_TOKEN is not set", "yawble secret set GH_TOKEN"},
		{"accepted", &doctor.GitHubObserved{Set: true, Status: 200, Login: "djlsystems"}, doctor.OK, "djlsystems", ""},
		{"revoked", &doctor.GitHubObserved{Set: true, Status: 401}, doctor.Fail, "rejects", "yawble secret set GH_TOKEN"},
		{"unreachable", &doctor.GitHubObserved{Set: true, Unreachable: true}, doctor.Skip, "could not reach GitHub", ""},
	}
	for _, c := range cases {
		o := healthyObserved()
		o.GitHub = c.github
		row := find(t, doctor.HostChecks(o), "github")
		if row.Verdict != c.verdict || !strings.Contains(row.Detail, c.detail) || !strings.Contains(row.Fix, c.fix) {
			t.Errorf("%s: %+v", c.name, row)
		}
	}
	if rows := doctor.HostChecks(healthyObserved()); len(rows) == 0 {
		t.Fatal("no rows")
	}
}

// Contributor mode forks a project the token's owner does not own and opens pull
// requests on it; doctor says which kind of token that needs and whether this one is it.
func TestTheContributorRowSaysWhichTokenContributorModeNeeds(t *testing.T) {
	cases := []struct {
		name    string
		github  *doctor.GitHubObserved
		verdict doctor.Verdict
		detail  string
	}{
		{"classic with public_repo", &doctor.GitHubObserved{Set: true, Status: 200, Kind: "classic", Scopes: []string{"public_repo"}, ContributorReady: true}, doctor.OK, "can fork and open pull requests"},
		{"oauth with repo", &doctor.GitHubObserved{Set: true, Status: 200, Kind: "oauth", Scopes: []string{"repo", "workflow"}, ContributorReady: true}, doctor.OK, "oauth token with repo, workflow"},
		{"fine-grained", &doctor.GitHubObserved{Set: true, Status: 200, Kind: "fine-grained"}, doctor.Warn, "classic token with the public_repo scope"},
		{"classic without the scope", &doctor.GitHubObserved{Set: true, Status: 200, Kind: "classic", Scopes: []string{"gist"}}, doctor.Warn, "no public_repo or repo scope"},
	}
	for _, c := range cases {
		o := healthyObserved()
		o.GitHub = c.github
		row := find(t, doctor.HostChecks(o), "contributor")
		if row.Verdict != c.verdict || !strings.Contains(row.Detail, c.detail) {
			t.Errorf("%s: %+v", c.name, row)
		}
	}

	o := healthyObserved()
	o.GitHub = &doctor.GitHubObserved{Set: true, Status: 401}
	for _, row := range doctor.HostChecks(o) {
		if row.Name == "contributor" {
			t.Errorf("a rejected token has no contributor row: %+v", row)
		}
	}
}

// Which version of each agent CLI is installed and when it last changed, as its own row: the
// platform updates them only at start and on a person's request, and this is where that shows.
func TestTheAgentVersionsRowSaysWhichVersionAndWhenItWasUpdated(t *testing.T) {
	r := sampleReport()
	r.Agents[0].UpdatedAt = strp("2026-09-29T20:46:21+00:00")
	r.Agents[2].Version = strp("GitHub Copilot CLI 1.0.88.")
	r.Agents[2].VersionsSince = strp("2026-09-25T15:22:00+00:00")

	row := find(t, doctor.InstanceChecks(r, nil, now), "agent versions")

	if row.Verdict != doctor.Info {
		t.Errorf("an information row, never a verdict: %+v", row)
	}
	for _, want := range []string{
		"claude 2.1.280, updated 2026-09-29 20:46 UTC",
		// The CLI's own full stop after its version is not carried into the sentence.
		"copilot GitHub Copilot CLI 1.0.88, unchanged since 2026-09-25 15:22 UTC",
	} {
		if !strings.Contains(row.Detail, want) {
			t.Errorf("agent versions should say %q: %+v", want, row)
		}
	}
	if strings.Contains(row.Detail, "agy") || strings.Contains(row.Detail, "codex") {
		t.Errorf("a CLI with no version or not installed is left out: %+v", row)
	}

	var out bytes.Buffer
	doctor.RenderAgents(&out, r.Agents)
	if !strings.Contains(out.String(), "installed   yes, 2.1.280, updated 2026-09-29 20:46 UTC") {
		t.Errorf("yawble agents should say when it was updated:\n%s", out.String())
	}
}

// No version anywhere: no row, rather than an empty one.
func TestNoAgentVersionsRowWithoutVersions(t *testing.T) {
	r := sampleReport()
	for i := range r.Agents {
		r.Agents[i].Version = nil
	}
	for _, c := range doctor.InstanceChecks(r, nil, now) {
		if c.Name == "agent versions" {
			t.Errorf("no versions, no row: %+v", c)
		}
	}
}

// The running limit is the Host's; a limit a person set above the Host's own bounds warns,
// and the comparison uses only the Host's figures.
func TestTheRunningLimitIsTheHostsAnswerAndWarnsAboveItsOwnBounds(t *testing.T) {
	r := sampleReport()
	r.Wip = &doctor.HostWip{Limit: &doctor.WipLimit{Limit: 4, Bound: "memory", CPUBound: 7, MemoryBound: intp(4), Reason: "the memory bound applies"}}
	if c := find(t, doctor.InstanceChecks(r, nil, now), "running limit"); c.Verdict != doctor.OK || c.Detail != "4, from the memory bound (the Host's answer: the memory bound applies)" {
		t.Errorf("default: %+v", c)
	}
	r.Wip.Limit = &doctor.WipLimit{Limit: 8, Bound: "configuration", CPUBound: 7, MemoryBound: intp(4), Reason: "Wip:MaxRunning is 8"}
	if c := find(t, doctor.InstanceChecks(r, nil, now), "running limit"); c.Verdict != doctor.Warn || !strings.Contains(c.Detail, "the Host's own bounds allow 4") || !strings.Contains(c.Fix, "maxRunning 0") {
		t.Errorf("configured above: %+v", c)
	}
	r.Wip.Limit = &doctor.WipLimit{Limit: 3, Bound: "setting", CPUBound: 7, Reason: "a tenant setting"}
	if c := find(t, doctor.InstanceChecks(r, nil, now), "running limit"); c.Verdict != doctor.OK {
		t.Errorf("setting within: %+v", c)
	}
	r.Wip = nil
	if c := find(t, doctor.InstanceChecks(r, nil, now), "running limit"); c.Verdict != doctor.Skip || !strings.HasPrefix(c.Detail, "not known: ") {
		t.Errorf("older Host: %+v", c)
	}
}

// Each mechanism in the Host's words; one the CLI does not know is not known, never enforced.
func TestRunMemorySaysTheHostsMechanism(t *testing.T) {
	r := sampleReport()
	for _, c := range []struct {
		m    doctor.RunMemory
		want string
	}{
		{doctor.RunMemory{Mechanism: "cgroup", PerRunMb: intp(2048), Detail: "per-run cgroup"}, "cgroup, 2048 MB per run: per-run cgroup"},
		{doctor.RunMemory{Mechanism: "rlimit", PerRunMb: intp(1792), Detail: "set"}, "rlimit, 1792 MB per run: set"},
		{doctor.RunMemory{Mechanism: "none", Detail: "nothing set"}, "no per-run memory cap: this engine offers none, so runs share the worker's memory (a fact of the engine, not a fault)"},
		{doctor.RunMemory{Mechanism: "quota"}, `not known (the Host said "quota")`},
	} {
		m := c.m
		r.Wip = &doctor.HostWip{RunMemory: &m}
		if row := find(t, doctor.InstanceChecks(r, nil, now), "run memory"); row.Verdict != doctor.Info || row.Detail != c.want {
			t.Errorf("%s: %+v, want %q", c.m.Mechanism, row, c.want)
		}
	}
}

// The agents row puts each launch beside the sign-in; only the Host's "ok" reads ok.
func TestTheAgentsRowSaysEachLaunchAndOnlyOkIsOk(t *testing.T) {
	r := sampleReport()
	r.Agents[0].Launch = &doctor.Launch{Result: "failed", ExitCode: intp(137), StderrTail: strp("Killed")}
	r.Agents[1].Launch = &doctor.Launch{Result: "not checked"}
	a := find(t, doctor.InstanceChecks(r, nil, now), "agents")
	for _, want := range []string{"claude signed in, launch FAILED, exit 137", "codex NOT signed in, launch not checked", "copilot not measured, launch not known", "agy not installed"} {
		if !strings.Contains(a.Detail, want) {
			t.Errorf("agents row lacks %q: %+v", want, a)
		}
	}
	if a.Verdict != doctor.Warn {
		t.Errorf("a failed launch warns: %+v", a)
	}
	if got := (doctor.Agent{Launch: &doctor.Launch{Result: "maybe"}}).LaunchText(); got != `not known (the Host said "maybe")` {
		t.Errorf("unknown result read as %q", got)
	}
}

// A CLI no worker answered for is not measured: installed null decodes as nil, reads "not measured"
// in the agents row and the agents block, fails no check, needs no hint, and is never "not installed".
func TestANullInstalledIsNotMeasuredAndFailsNoCheck(t *testing.T) {
	r, err := doctor.ParseHostReport(`{"agents":[{"agent":"claude","installed":null,"version":null,"authenticated":null,` +
		`"detail":"Not measured: no worker is connected to ask this CLI.","measuredAt":null,"measuredOn":null}]}`)
	if err != nil {
		t.Fatal(err)
	}
	a := r.Agents[0]
	if a.Installed != nil || a.IsInstalled() || a.NotInstalled() {
		t.Fatalf("installed decoded as %v", a.Installed)
	}

	row := find(t, doctor.InstanceChecks(&r, nil, now), "agents")
	if row.Verdict != doctor.OK || !strings.Contains(row.Detail, "claude not measured") || strings.Contains(row.Detail, "not installed") || row.Fix != "" {
		t.Errorf("agents row: %+v", row)
	}
	if h := doctor.SignInHint(a); h != "" {
		t.Errorf("hint %q", h)
	}

	var out bytes.Buffer
	doctor.RenderAgents(&out, r.Agents)
	text := out.String()
	if !strings.Contains(text, "installed   not measured") || !strings.Contains(text, "signed in   not measured") ||
		!strings.Contains(text, "no worker is connected") || strings.Contains(text, "installed   no\n") || strings.Contains(text, "  measured    ") {
		t.Errorf("render:\n%s", text)
	}
}

// The doctor says when it measured a sign-in and on which worker: in the agents row and in each block.
func TestTheDoctorSaysWhenAndOnWhichWorkerItMeasured(t *testing.T) {
	r := sampleReport()
	for i := range r.Agents {
		r.Agents[i].MeasuredAt, r.Agents[i].MeasuredOn = strp("2026-10-02T12:00:00+00:00"), strp("w1")
	}

	row := find(t, doctor.InstanceChecks(r, nil, now), "agents")
	if !strings.Contains(row.Detail, "(sign-ins measured 2026-10-02 12:00 UTC on worker w1)") {
		t.Errorf("agents row: %+v", row)
	}

	var out bytes.Buffer
	doctor.RenderAgents(&out, r.Agents[:1])
	if !strings.Contains(out.String(), "  measured    2026-10-02 12:00 UTC on worker w1\n") {
		t.Errorf("render:\n%s", out.String())
	}
}

// A CLI the platform's update held when the Host last probed reads "updating" - in the agents row and
// its block - with the probe's time set and installed null: never "not installed", and no warning.
func TestAgentsRowSaysUpdatingNotNotInstalled(t *testing.T) {
	r, err := doctor.ParseHostReport(`{"agents":[{"agent":"claude","installed":null,"version":"2.0.1","authenticated":null,` +
		`"detail":"Updating claude on w1; it is measured again when the update ends.","measuredAt":"2026-10-03T09:00:00+00:00","measuredOn":"w1",` +
		`"updating":"Updating claude on w1; it is measured again when the update ends.",` +
		`"launch":{"result":"updating","exitCode":null,"stderrTail":null,"detail":"claude-headless: Updating claude on w1; it is measured again when the update ends."}}]}`)
	if err != nil {
		t.Fatal(err)
	}
	a := r.Agents[0]
	if a.Updating == nil || a.NotInstalled() {
		t.Fatalf("decoded as %+v", a)
	}
	if got := a.LaunchText(); got != "updating" {
		t.Errorf("launch read as %q", got)
	}

	row := find(t, doctor.InstanceChecks(&r, nil, now), "agents")
	if row.Verdict != doctor.OK || !strings.Contains(row.Detail, "claude updating") || strings.Contains(row.Detail, "not installed") || row.Fix != "" {
		t.Errorf("agents row: %+v", row)
	}

	var out bytes.Buffer
	doctor.RenderAgents(&out, r.Agents)
	if text := out.String(); !strings.Contains(text, "installed   updating") || strings.Contains(text, "installed   no\n") {
		t.Errorf("render:\n%s", text)
	}
}
