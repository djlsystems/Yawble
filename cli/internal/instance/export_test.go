package instance

import "time"

// SetNoteAtForTests moves the point where the wait is said, and progress starts being shown.
func SetNoteAtForTests(at time.Duration) func() {
	old := healthNoteAt
	healthNoteAt = at
	return func() { healthNoteAt = old }
}

// SetPollingForTests shortens the health wait so a test can watch it give up.
func SetPollingForTests(interval, timeout time.Duration) func() {
	oldInterval, oldTimeout := healthInterval, healthTimeout
	healthInterval, healthTimeout = interval, timeout
	return func() { healthInterval, healthTimeout = oldInterval, oldTimeout }
}
