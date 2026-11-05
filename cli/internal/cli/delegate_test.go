package cli_test

import (
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/machine"
)

// The answers a Podman machine gives, as `podman info` and the read inside it over
// `podman machine ssh` would.
const (
	infoNoControllers = `{"host":{"cgroupVersion":"v2","cgroupManager":"cgroupfs","cgroupControllers":[]}}`
	infoControllers   = `{"host":{"cgroupVersion":"v2","cgroupManager":"systemd","cgroupControllers":["cpuset","cpu","io","memory","pids"]}}`
	sshPrefix         = "podman machine ssh podman-machine-default "
	readPrefix        = sshPrefix + "u=$(id -u)"
	writePrefix       = sshPrefix + "sudo -n sh -c"
	notDelegated      = "controllers: \nuser.slice: \ndelegate.conf: absent\n"
	delegated         = "controllers: cpuset cpu io memory pids\nuser.slice: cpuset cpu io memory pids\ndelegate.conf: present\n" + machine.DelegateContent
)

// delegationScript is a running rootless Windows machine whose podman info answers info, in turn,
// and whose inside answers read.
func delegationScript(read string, info ...string) *engine.Scripted {
	s := upScript()
	s.On("wsl --status", engine.Result{})
	s.On("podman machine inspect", engine.Result{Stdout: "podman-machine-default|running|false|2048|10\n"})
	s.On("wsl -d podman-machine-default -e free -m", engine.Result{Stdout: "Mem: 15688 1 1\n"})
	var answers []engine.Result
	for _, i := range info {
		answers = append(answers, engine.Result{Stdout: i})
	}
	s.OnSequence("podman info --format json", answers...)
	s.On(readPrefix, engine.Result{Stdout: read})
	return s
}

func runUp(t *testing.T, s *engine.Scripted, interactive bool, stdin string, args ...string) (int, string, string) {
	t.Helper()
	deps := stubbed(s)
	deps.GOOS, deps.LookPath, deps.Interactive = "windows", lookPath("podman"), interactive
	if stdin != "" {
		deps.Stdin = strings.NewReader(stdin)
	}
	return run(t, deps, append([]string{"up"}, args...)...)
}

func has(s *engine.Scripted, prefix string) bool {
	for _, c := range s.Calls {
		if strings.HasPrefix(c, prefix) {
			return true
		}
	}
	return false
}

// A machine flipped from rootful: made rootless, then delegated (written as root, reloaded,
// restarted), and podman info read again lists memory, cpu and pids; control and the worker are
// made with their limits. The machine is never recreated and no volume is touched.
func TestUpOnAMachineFlippedFromRootfulDelegatesTheControllersAndAppliesTheLimits(t *testing.T) {
	s := engine.NewScripted()
	s.On("wsl --status", engine.Result{})
	s.OnSequence("podman machine inspect", engine.Result{Stdout: "podman-machine-default|running|true|2048|10\n"}, engine.Result{Stdout: "podman-machine-default|running|false|2048|10\n"})
	s.On("wsl -d podman-machine-default -e free -m", engine.Result{Stdout: "Mem: 15688 1 1\n"})
	s.OnSequence("podman info --format json", engine.Result{Stdout: infoNoControllers}, engine.Result{Stdout: infoControllers})
	s.On(readPrefix, engine.Result{Stdout: notDelegated})
	s.On("podman container inspect", engine.Result{Stderr: "no such container", ExitCode: 125})
	s.On("podman volume exists", engine.Result{ExitCode: 1})
	s.On("podman pod exists", engine.Result{ExitCode: 1})
	s.On(healthLine("podman", "yawble-worker-1"), engine.Result{Stdout: "healthy\n"})
	deps := stubbed(s)
	deps.GOOS, deps.LookPath = "windows", lookPath("podman")
	deps.ConfigDir = t.TempDir()
	seedWorkerKey(t, deps.ConfigDir)
	code, out, errOut := run(t, deps, "up", "--yes")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	c := calls(s)
	set := strings.Index(c, "podman machine set --rootful=false")
	write := strings.Index(c, writePrefix)
	if set < 0 || write < set {
		t.Fatalf("not delegated after the flip:\n%s", c)
	}
	after := c[write:]
	stop, start, info := strings.Index(after, "podman machine stop"), strings.Index(after, "podman machine start"), strings.Index(after, "podman info --format json")
	if stop < 0 || start < stop || info < start {
		t.Errorf("not written, restarted, re-read in that order:\n%s", after)
	}
	for _, line := range s.Calls {
		if strings.HasPrefix(line, writePrefix) {
			for _, want := range []string{machine.DelegateFile, "[Service]", machine.DelegateLine, "yawble-delegate:", "systemctl daemon-reload"} {
				if !strings.Contains(line, want) {
					t.Errorf("write lacks %q: %s", want, line)
				}
			}
		}
	}
	for _, never := range []string{"podman machine init", "podman machine rm", "podman volume rm", "podman machine reset"} {
		if strings.Contains(c, never) {
			t.Errorf("ran %q:\n%s", never, c)
		}
	}
	if !strings.Contains(out, "RESTARTS the Podman machine") && !strings.Contains(out, "restarting the podman machine") {
		t.Errorf("out %q", out)
	}
	runs := 0
	for _, line := range s.Calls {
		if strings.HasPrefix(line, "podman run -d --name yawble") {
			runs++
			if !strings.Contains(line, "--memory") || !strings.Contains(line, "--cpus") {
				t.Errorf("made without its limits: %s", line)
			}
		}
	}
	if runs != 2 || strings.Contains(out+errOut, "WITHOUT") {
		t.Errorf("runs %d, out %s %s", runs, out, errOut)
	}
}

// A second up on the delegated machine: podman info lists them, nothing inside is read or written,
// the machine is not restarted.
func TestASecondUpOnTheDelegatedMachineChangesNothing(t *testing.T) {
	s := delegationScript(delegated, infoControllers)
	code, out, errOut := runUp(t, s, false, "")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if has(s, sshPrefix+"u=") || has(s, writePrefix) || has(s, "podman machine stop") || strings.Contains(out+errOut, "WITHOUT") {
		t.Errorf("touched the machine:\n%s\n%s %s", calls(s), out, errOut)
	}
}

// Controllers already there: the machine is untouched.
func TestAMachineWithTheControllersIsNotTouched(t *testing.T) {
	s := delegationScript(notDelegated, infoControllers)
	if code, out, errOut := runUp(t, s, true, "y\n"); code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if has(s, sshPrefix) || has(s, "podman machine stop") || has(s, "podman machine set") {
		t.Errorf("touched:\n%s", calls(s))
	}
}

// Someone else's delegate.conf is never overwritten: it is said and left, and the start is the
// one without limits.
func TestADelegateConfYawbleDidNotWriteIsNotOverwritten(t *testing.T) {
	foreign := "controllers: \nuser.slice: \ndelegate.conf: present\n[Service]\nDelegate=memory pids cpu io\n"
	s := delegationScript(foreign, infoNoControllers)
	code, out, errOut := runUp(t, s, false, "", "--yes")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if has(s, writePrefix) || has(s, "podman machine stop") {
		t.Errorf("wrote or restarted:\n%s", calls(s))
	}
	if !strings.Contains(errOut, "was not written by yawble, so it is left as it is") || !strings.Contains(errOut, "Delegate=memory pids cpu io") || !strings.Contains(out+errOut, "WITHOUT the containers' limits") {
		t.Errorf("out %s\nerr %s", out, errOut)
	}
}

// The machine refuses the write itself when a foreign file appeared after the read (exit 3).
func TestAForeignDelegateConfFoundAtTheWriteIsLeftAndNoRestart(t *testing.T) {
	s := delegationScript(notDelegated, infoNoControllers)
	s.On(writePrefix, engine.Result{ExitCode: 3})
	code, out, errOut := runUp(t, s, false, "", "--yes")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if has(s, "podman machine stop") || !strings.Contains(errOut, "was not written by yawble; it is left as it is") {
		t.Errorf("calls:\n%s\nerr %s", calls(s), errOut)
	}
}

// Delegated, restarted, and podman info still lacks them: B0085's start without limits, with its
// warning, and the reason: the delegation did not take.
func TestDelegationThatDoesNotTakeFallsBackToTheStartWithoutLimitsSayingWhy(t *testing.T) {
	s := delegationScript(notDelegated, infoNoControllers)
	code, out, errOut := runUp(t, s, false, "", "--yes")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if !has(s, writePrefix) || !has(s, "podman machine stop") {
		t.Errorf("not delegated:\n%s", calls(s))
	}
	all := out + errOut
	if !strings.Contains(all, "the delegation did not take") || !strings.Contains(all, "podman info reports no memory, cpu, pids cgroup controller") || !strings.Contains(all, "WITHOUT the containers' limits") {
		t.Errorf("out %s\nerr %s", out, errOut)
	}
	if strings.Count(calls(s), "podman machine stop") != 1 {
		t.Errorf("restarted more than once:\n%s", calls(s))
	}
}

// yawble's own file is in place and the controllers are still missing: not written again, not
// restarted again, and said.
func TestYawblesOwnDelegateConfThatDidNotTakeIsNotRestartedAgain(t *testing.T) {
	ours := "controllers: \nuser.slice: \ndelegate.conf: present\n" + machine.DelegateContent
	s := delegationScript(ours, infoNoControllers)
	code, out, errOut := runUp(t, s, false, "", "--yes")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if has(s, writePrefix) || has(s, "podman machine stop") || !strings.Contains(errOut, "the delegation did not take") {
		t.Errorf("calls:\n%s\nerr %s", calls(s), errOut)
	}
}

// user@.service already has the controllers: the delegation is not the cause, and nothing is
// written.
func TestWhenUserServiceHasTheControllersTheDelegationIsNotTheCause(t *testing.T) {
	s := delegationScript("controllers: cpuset cpu io memory pids\nuser.slice: memory pids\ndelegate.conf: present\n[Service]\nDelegate=memory pids cpu io\n", infoNoControllers)
	code, _, errOut := runUp(t, s, false, "", "--yes")
	if code != 0 || has(s, writePrefix) || has(s, "podman machine stop") || !strings.Contains(errOut, "the delegation is not the cause") {
		t.Errorf("exit %d calls:\n%s\nerr %s", code, calls(s), errOut)
	}
}

// The inside of the machine cannot be read: not measured, nothing changed.
func TestWhenTheMachineCannotBeReadNothingIsWritten(t *testing.T) {
	s := delegationScript("", infoNoControllers)
	s.On(readPrefix, engine.Result{Stderr: "ssh: connect refused", ExitCode: 255})
	code, _, errOut := runUp(t, s, false, "", "--yes")
	if code != 0 || has(s, writePrefix) || !strings.Contains(errOut, "could not be read") {
		t.Errorf("exit %d calls:\n%s\nerr %s", code, calls(s), errOut)
	}
}

// The prompt says what changes and that the machine restarts; a no leaves the machine as it is
// and the start is the one without limits.
func TestADeclinedDelegationLeavesTheMachineAndStartsWithoutLimits(t *testing.T) {
	s := delegationScript(notDelegated, infoNoControllers)
	code, out, errOut := runUp(t, s, true, "n\n")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	for _, want := range []string{"Delegate them now?", machine.DelegateFile, machine.DelegateLine, "RESTARTS the Podman machine", "[y/N]"} {
		if !strings.Contains(out, want) {
			t.Errorf("prompt lacks %q: %s", want, out)
		}
	}
	if has(s, writePrefix) || has(s, "podman machine stop") || !strings.Contains(errOut, "left as it is") || !strings.Contains(out+errOut, "WITHOUT the containers' limits") {
		t.Errorf("calls:\n%s\nout %s\nerr %s", calls(s), out, errOut)
	}
}

// No terminal and no --yes: the delegation is taken, and it says so.
func TestWithNoTerminalTheDelegationIsTaken(t *testing.T) {
	s := delegationScript(notDelegated, infoNoControllers, infoControllers)
	code, out, errOut := runUp(t, s, false, "")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if !has(s, writePrefix) || !strings.Contains(out, "no terminal to ask: taking it") || strings.Contains(out+errOut, "WITHOUT") {
		t.Errorf("calls:\n%s\nout %s\nerr %s", calls(s), out, errOut)
	}
}

// Linux with no Podman machine: no system file is written, nothing is read over ssh, and the fix
// is the same as before.
func TestOnLinuxWithoutAMachineUpWritesNoSystemFileAndPrintsTheFix(t *testing.T) {
	s := upScript()
	s.On("podman info --format json", engine.Result{Stdout: infoNoControllers})
	deps := stubbed(s)
	deps.LookPath = lookPath("podman")
	code, out, errOut := run(t, deps, "up", "--yes")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if has(s, "podman machine") || has(s, "sudo") || has(s, "systemctl") {
		t.Errorf("touched the system:\n%s", calls(s))
	}
	if !strings.Contains(out+errOut, "enable them in each cgroup.subtree_control") {
		t.Errorf("out %s\nerr %s", out, errOut)
	}
}
