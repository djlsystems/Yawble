package instance

import "time"

// SetNoteAtForTests moves the point where the wait is said, and progress starts being shown.
func SetNoteAtForTests(at time.Duration) func() {
	old := healthNoteAt
	healthNoteAt = at
	return func() { healthNoteAt = old }
}

// SetDrainPollingForTests shortens the drain wait so a test watches runs end without sleeping:
// interval paces every read of control's record, and stands for one record period; timeout
// bounds both the wait for the drain to show and the wait for runs to end.
func SetDrainPollingForTests(interval, timeout time.Duration) func() {
	old := []time.Duration{drainInterval, drainTimeout, drainShownInterval, drainShownWithin, recordPeriod}
	drainInterval, drainTimeout, drainShownInterval, drainShownWithin, recordPeriod = interval, timeout, interval, timeout, interval
	return func() {
		drainInterval, drainTimeout, drainShownInterval, drainShownWithin, recordPeriod = old[0], old[1], old[2], old[3], old[4]
	}
}
