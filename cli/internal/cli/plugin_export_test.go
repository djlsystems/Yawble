package cli

import "time"

// FastRescan makes the plugin commands wait briefly and name their rescan request nonce, so a
// scripted report can answer it. The returned func restores the defaults.
func FastRescan(nonce string) func() {
	wait, install, poll, next := rescanWait, installWait, rescanPoll, newNonce
	rescanWait, installWait, rescanPoll, newNonce = 50*time.Millisecond, 50*time.Millisecond, time.Millisecond, func() string { return nonce }
	return func() { rescanWait, installWait, rescanPoll, newNonce = wait, install, poll, next }
}

// The scripts the plugin commands run in the container, so a scripted engine can answer each.
const (
	PluginPrepareScript         = prepareScript
	PluginPlaceScript           = placeScript
	PluginRequestScript         = requestScript
	PluginLayoutScript          = layoutScript
	PluginReportScript          = reportScript
	PluginInstallRequestScript  = installRequestScript
	PluginInstallReportScript   = installReportScript
	PluginInstallWithdrawScript = installWithdrawScript
)
