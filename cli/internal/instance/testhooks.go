package instance

import "time"

// SetPollingForTests shortens the health wait so a test can watch it give up, or pass in
// milliseconds rather than the real two-second interval. Not a _test file because tests in other
// packages (the cli package's restore and doctor tests) wait on the same poll. Never called by
// yawble itself.
func SetPollingForTests(interval, timeout time.Duration) func() {
	oldInterval, oldTimeout := healthInterval, healthTimeout
	healthInterval, healthTimeout = interval, timeout
	return func() { healthInterval, healthTimeout = oldInterval, oldTimeout }
}
