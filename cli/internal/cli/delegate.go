package cli

import (
	"context"
	"errors"
	"fmt"
	"io"
	"strings"

	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/machine"
)

// consent asks before a change to the machine. --yes takes it, and so does no terminal: nobody is
// there to answer, and the change is what makes the containers' limits apply. Said either way.
func consent(deps Deps, yes bool, out io.Writer, question string) bool {
	if yes {
		return true
	}
	if !deps.Interactive || deps.Stdin == nil {
		fmt.Fprintln(out, question, "(no terminal to ask: taking it)")
		return true
	}
	fmt.Fprint(out, question, " [y/N] ")
	line, _ := readLine(deps.Stdin)
	answer := strings.ToLower(strings.TrimSpace(line))
	return answer == "y" || answer == "yes"
}

// delegateControllers is `up` on a running rootless Podman machine whose `podman info` lacks a
// controller the containers' limits need. Only when the machine itself says the delegation is the
// cause - Podman uses the systemd cgroup manager, user@.service's cgroup.controllers lack them
// and no delegate.conf is there - and the
// person agrees, it writes yawble's delegate.conf inside the machine as root, reloads systemd,
// restarts the machine and reads `podman info` again. It never recreates the machine, never
// touches a volume and never overwrites a delegate.conf it did not write. Whatever it cannot fix
// is said here, and the start goes on without the limits (instance.NoLimitsLine says that).
// Errors are only the machine failing to restart: the instance cannot start then.
func delegateControllers(ctx context.Context, deps Deps, yes bool, machineName string, out, errOut io.Writer) error {
	r := runnerOf(deps)
	podman := engine.NewFor("podman", r, goosOf(deps))
	before := podman.Cgroups(ctx)
	missing := before.Missing()
	if len(missing) == 0 {
		return nil // there, or not measured: nothing to delegate on what is not known to be missing
	}
	lacking := strings.Join(missing, ", ")
	if before.UnderCgroupfs() {
		// Under cgroupfs rootless Podman's containers get no controllers whatever systemd
		// delegates: a write and a restart cannot help. The image chose cgroupfs (WSL 2.9), so
		// changing it is a person's decision, not up's.
		fmt.Fprintln(errOut, CgroupfsNote(missing))
		return nil
	}
	d := machine.ReadDelegation(ctx, r, machineName)
	switch {
	case !d.Measured:
		fmt.Fprintf(errOut, "note: podman info reports no %s cgroup controller, and whether the machine's systemd delegation is the cause could not be read (%s); the machine is left as it is\n", lacking, d.Reason)
		return nil
	case len(d.Missing()) == 0:
		fmt.Fprintf(errOut, "note: podman info reports no %s cgroup controller, but systemd already hands user@.service %s inside the machine, so the delegation is not the cause; the machine is left as it is\n", lacking, strings.Join(d.UserControllers, " "))
		return nil
	case d.File == machine.DelegateForeign:
		fmt.Fprintf(errOut, "note: podman info reports no %s cgroup controller; %s inside the machine was not written by yawble, so it is left as it is (it reads: %s)\n", lacking, machine.DelegateFile, oneLine(d.FileText))
		return nil
	case d.File == machine.DelegateOurs:
		fmt.Fprintf(errOut, "warning: yawble's %s is in place inside the machine, but podman info still reports no %s cgroup controller: the delegation did not take\n", machine.DelegateFile, lacking)
		return nil
	}
	if !d.IsTheCause() {
		return nil
	}

	question := fmt.Sprintf("Podman in the machine has no %s cgroup controller, because systemd does not delegate them to the user it runs as, so the containers would start without their memory, CPU and process limits. "+
		"Delegate them now? This writes %s (%s) inside the Podman machine as root, reloads systemd and RESTARTS the Podman machine; no machine setting, container or volume is changed.",
		lacking, machine.DelegateFile, machine.DelegateLine)
	if !consent(deps, yes, out, question) {
		fmt.Fprintln(errOut, "note: the Podman machine is left as it is; the instance starts without the containers' limits, and yawble up offers this again")
		return nil
	}
	fmt.Fprintf(out, "delegating the cgroup controllers inside the podman machine (%s, systemctl daemon-reload, restart)\n", machine.DelegateFile)
	if err := machine.WriteDelegation(ctx, r, machineName); err != nil {
		if errors.Is(err, machine.ErrForeignDelegate) {
			fmt.Fprintln(errOut, "note:", err.Error())
		} else {
			fmt.Fprintln(errOut, "warning: the delegation was not written:", err.Error())
		}
		return nil
	}
	fmt.Fprintln(out, "restarting the podman machine")
	if err := machine.Restart(ctx, r); err != nil {
		return err
	}
	after := podman.Cgroups(ctx)
	if still := after.Missing(); len(still) > 0 {
		fmt.Fprintf(errOut, "warning: delegated the cgroup controllers inside the machine, but podman info still reports no %s cgroup controller: the delegation did not take\n", strings.Join(still, ", "))
		return nil
	}
	if !after.Measured {
		fmt.Fprintf(errOut, "note: delegated the cgroup controllers inside the machine; podman info could not be read afterwards (%s)\n", after.Reason)
		return nil
	}
	fmt.Fprintf(out, "podman info now reports the cgroup controllers: %s\n", strings.Join(after.Controllers, " "))
	return nil
}

// CgroupfsNote is what up says when podman info lacks controllers and Podman in the machine uses
// cgroup_manager = "cgroupfs": the cause, and that the machine is left as it is.
func CgroupfsNote(missing []string) string {
	return fmt.Sprintf("note: podman info reports no %s cgroup controller, and Podman in the machine uses cgroup_manager = \"cgroupfs\" (set by the machine image); rootless containers get no cgroup controllers under cgroupfs, and delegation cannot change that; the machine is left as it is", strings.Join(missing, ", "))
}

func oneLine(s string) string { return strings.Join(strings.Fields(s), " ") }
