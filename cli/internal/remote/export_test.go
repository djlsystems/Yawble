package remote

import "time"

// SetPollingForTests shortens the URL wait so a test can watch it give up.
func SetPollingForTests(interval, timeout time.Duration) func() {
	oldInterval, oldTimeout := urlInterval, urlTimeout
	urlInterval, urlTimeout = interval, timeout
	return func() { urlInterval, urlTimeout = oldInterval, oldTimeout }
}
