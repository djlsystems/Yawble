package cli

// RecoverHintForTest lets the tests read the recovery hint for a given OS.
func RecoverHintForTest(safety, goos string) string { return recoverHint(safety, goos) }
