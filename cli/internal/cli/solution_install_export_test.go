package cli

import (
	"fmt"
	"time"
)

// FastSolution makes `solution install` wait briefly and name its stage and requests n1, n2, ...
// in turn, so scripted reports can answer each request. The returned func restores the defaults.
func FastSolution() func() {
	wait, install, poll, next := rescanWait, installWait, rescanPoll, newNonce
	n := 0
	rescanWait, installWait, rescanPoll = 50*time.Millisecond, 50*time.Millisecond, time.Millisecond
	newNonce = func() string { n++; return fmt.Sprintf("n%d", n) }
	return func() { rescanWait, installWait, rescanPoll, newNonce = wait, install, poll, next }
}

// The scripts `solution install` runs in the container.
const (
	SolutionInstallStageScript  = solutionInstallStageScript
	SolutionInstallFolderScript = solutionInstallFolderScript
	SolutionInstallSealScript   = solutionInstallSealScript
	SolutionRequestScript       = solutionRequestScript
	SolutionReportScript        = solutionReportScript
	SolutionWithdrawScript      = solutionWithdrawScript
)

// How `solution install` types an answer by the setting's type.
var ParseSetting = parseSetting
