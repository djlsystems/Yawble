package cli

import (
	"net"
	"sync/atomic"
	"time"
)

// FastConnect makes the connect commands wait briefly for the Host and the browser. The returned
// func restores the defaults.
func FastConnect(browser time.Duration) func() {
	wait, complete, poll, br, read := connectWait, completeWait, connectPoll, browserWait, deviceRead
	connectWait, completeWait, connectPoll, browserWait, deviceRead = 300*time.Millisecond, 300*time.Millisecond, time.Millisecond, browser, time.Millisecond
	return func() {
		connectWait, completeWait, connectPoll, browserWait, deviceRead = wait, complete, poll, br, read
	}
}

// The scripts the connect commands run in the container, so a scripted Host can answer each.
const (
	ConnectRequestScript  = connectRequestScript
	ConnectReportScript   = connectReportScript
	ConnectWithdrawScript = connectWithdrawScript
)

// CountListens counts the loopback listeners the connect commands open from now on; the returned
// func reads the count. The listener itself is still opened.
func CountListens() func() int {
	var n atomic.Int32
	listen = func(network, address string) (net.Listener, error) {
		n.Add(1)
		return net.Listen(network, address)
	}
	return func() int { return int(n.Load()) }
}
