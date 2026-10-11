//go:build !windows

package main

// enableColor: terminals on macOS and Linux interpret colour sequences as they are.
func enableColor() bool { return true }
