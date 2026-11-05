package machine

import (
	"context"
	"fmt"
	"slices"
	"strings"

	"github.com/djlsystems/yawble/cli/internal/engine"
)

// DelegateFile is the systemd drop-in that hands cgroup controllers to every user's systemd
// manager (user@.service), and so to rootless Podman under it. Podman's machine image writes one
// of its own at build time (podman-machine-os build_common.sh: Delegate=memory pids cpu io).
const DelegateFile = "/etc/systemd/system/user@.service.d/delegate.conf"

// DelegateMark is the first line of the delegate.conf yawble writes: a later `up` recognises its
// own file by it, and never overwrites one without it.
const DelegateMark = "# yawble-delegate: written by yawble up so rootless Podman gets the cgroup controllers; delete this file to undo"

// DelegateLine is the delegation yawble writes: every controller a container's limits need, and
// cpuset and io beside them.
const DelegateLine = "Delegate=cpu cpuset io memory pids"

// DelegateContent is the whole file yawble writes.
const DelegateContent = DelegateMark + "\n[Service]\n" + DelegateLine + "\n"

// File states of DelegateFile inside the machine.
const (
	DelegateAbsent  = "absent"
	DelegateOurs    = "yawble's"
	DelegateForeign = "not yawble's"
)

// Delegation is what the machine itself says about the controllers its user's systemd manager
// gets. Measured false is not measured: Reason says why, and nothing is changed on it.
type Delegation struct {
	Measured bool
	// UserControllers is the user@<uid>.service cgroup's cgroup.controllers: what systemd handed
	// the user Podman runs as.
	UserControllers []string
	// UserSlice is /sys/fs/cgroup/user.slice/cgroup.subtree_control, for the record.
	UserSlice string
	// File is DelegateAbsent, DelegateOurs or DelegateForeign; FileText is its content.
	File     string
	FileText string
	Reason   string
}

// Missing is the limit controllers user@.service lacks; none when not measured.
func (d Delegation) Missing() []string {
	if !d.Measured {
		return nil
	}
	var missing []string
	for _, want := range engine.LimitControllers {
		if !slices.Contains(d.UserControllers, want) {
			missing = append(missing, want)
		}
	}
	return missing
}

// IsTheCause is whether the delegation is what keeps the controllers from Podman: systemd hands
// user@.service fewer than the limits need, and no delegate.conf is there to ask for them. Only
// then does `up` write one.
func (d Delegation) IsTheCause() bool {
	return d.Measured && len(d.Missing()) > 0 && d.File == DelegateAbsent
}

// readScript runs as the machine's own user. Each answer is on a line of its own, behind a
// label, so an answer that is empty or missing is never read as "no controllers".
const readScript = `u=$(id -u); c=/sys/fs/cgroup/user.slice/user-$u.slice/user@$u.service/cgroup.controllers; ` +
	`if [ -r $c ]; then echo "controllers: $(cat $c)"; else echo "controllers-unreadable: $c"; fi; ` +
	`echo "user.slice: $(cat /sys/fs/cgroup/user.slice/cgroup.subtree_control 2>/dev/null)"; ` +
	`if [ -e ` + DelegateFile + ` ]; then echo "delegate.conf: present"; cat ` + DelegateFile + `; else echo "delegate.conf: absent"; fi`

// ReadDelegation asks the machine (`podman machine ssh`) for user@.service's controllers and
// whether delegate.conf is there and whose it is. It changes nothing.
func ReadDelegation(ctx context.Context, r engine.Runner, machineName string) Delegation {
	res, err := r.Run(ctx, "podman", "machine", "ssh", machineName, readScript)
	if err != nil {
		return Delegation{Reason: "podman machine ssh could not run: " + err.Error()}
	}
	if res.ExitCode != 0 {
		return Delegation{Reason: fmt.Sprintf("podman machine ssh: %s (exit %d)", strings.TrimSpace(res.Stderr), res.ExitCode)}
	}
	return parseDelegation(res.Stdout)
}

func parseDelegation(stdout string) Delegation {
	var d Delegation
	gotControllers, gotFile := false, false
	var file []string
	for _, line := range strings.Split(strings.ReplaceAll(stdout, "\r\n", "\n"), "\n") {
		switch {
		case gotFile && d.File == DelegateForeign:
			file = append(file, line)
		case strings.HasPrefix(line, "controllers: ") || line == "controllers:":
			d.UserControllers = strings.Fields(strings.TrimPrefix(line, "controllers:"))
			gotControllers = true
		case strings.HasPrefix(line, "controllers-unreadable: "):
			return Delegation{Reason: "the machine's " + strings.TrimPrefix(line, "controllers-unreadable: ") + " cannot be read"}
		case strings.HasPrefix(line, "user.slice:"):
			d.UserSlice = strings.TrimSpace(strings.TrimPrefix(line, "user.slice:"))
		case line == "delegate.conf: absent":
			d.File, gotFile = DelegateAbsent, true
		case line == "delegate.conf: present":
			// Foreign until its first line shows it is yawble's.
			d.File, gotFile = DelegateForeign, true
		}
	}
	if !gotControllers || !gotFile {
		return Delegation{Reason: "the machine did not answer what user@.service's cgroup controllers are and whether " + DelegateFile + " is there"}
	}
	if d.File == DelegateForeign {
		d.FileText = strings.TrimRight(strings.Join(file, "\n"), "\n") + "\n"
		if strings.HasPrefix(d.FileText, DelegateMark+"\n") {
			d.File = DelegateOurs
		}
	}
	d.Measured = true
	return d
}

// ErrForeignDelegate is the machine refusing the write because a delegate.conf yawble did not
// write appeared between the read and the write.
var ErrForeignDelegate = fmt.Errorf("%s inside the machine was not written by yawble; it is left as it is", DelegateFile)

// writeScript runs as root inside the machine (sudo -n: the machine's user has it without a
// password). It refuses, exit 3, when a delegate.conf without yawble's mark is there, so a file
// someone else wrote is never overwritten, even one written after ReadDelegation.
var writeScript = `sudo -n sh -c 'f=` + DelegateFile + `; ` +
	`if [ -e $f ] && ! grep -qF "yawble-delegate:" $f; then exit 3; fi; ` +
	`mkdir -p /etc/systemd/system/user@.service.d && ` +
	`printf "%s\n" "` + DelegateMark + `" "[Service]" "` + DelegateLine + `" > $f && ` +
	`systemctl daemon-reload'`

// WriteDelegation writes yawble's delegate.conf inside the machine as root and reloads systemd.
// The new delegation reaches user@.service when the machine restarts; the caller restarts it.
func WriteDelegation(ctx context.Context, r engine.Runner, machineName string) error {
	res, err := r.Run(ctx, "podman", "machine", "ssh", machineName, writeScript)
	if err != nil {
		return &engine.NotRunnable{Err: err}
	}
	switch res.ExitCode {
	case 0:
		return nil
	case 3:
		return ErrForeignDelegate
	}
	return fmt.Errorf("writing %s inside the podman machine: %s (exit %d)", DelegateFile, strings.TrimSpace(res.Stderr), res.ExitCode)
}

// Restart stops and starts the machine: what makes systemd start user@.service with the new
// delegation. The machine itself, its settings and its volumes are kept.
func Restart(ctx context.Context, r engine.Runner) error {
	if err := Stop(ctx, r); err != nil {
		return err
	}
	return Start(ctx, r)
}
