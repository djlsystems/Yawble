package cli

import "time"

// FastConnect makes the connect commands wait briefly for the Host and the browser. The returned
// func restores the defaults.
func FastConnect(browser time.Duration) func() {
	wait, poll, br := connectWait, connectPoll, browserWait
	connectWait, connectPoll, browserWait = 300*time.Millisecond, time.Millisecond, browser
	return func() { connectWait, connectPoll, browserWait = wait, poll, br }
}

// The scripts the connect commands run in the container, so a scripted Host can answer each.
const (
	ConnectRequestScript  = connectRequestScript
	ConnectReportScript   = connectReportScript
	ConnectWithdrawScript = connectWithdrawScript
)
