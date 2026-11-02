package cli

import "time"

// FastAttach makes attach, attach status and detach wait briefly for the Host. The returned func
// restores the defaults.
func FastAttach() func() {
	wait, poll := attachWait, attachPoll
	attachWait, attachPoll = 300*time.Millisecond, time.Millisecond
	return func() { attachWait, attachPoll = wait, poll }
}

// The scripts the attach commands run in the container, so a scripted Host can answer each.
const (
	AttachRequestScript = attachRequestScript
	AttachReportScript  = attachReportScript
)
