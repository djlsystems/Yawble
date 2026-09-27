//go:build windows

package main

import (
	"os"

	"golang.org/x/sys/windows"
)

// enableColor asks the console to interpret colour sequences. Windows Terminal already does; an
// older console shows them as raw text unless this is switched on, and when it cannot be, the
// mark is drawn without colour.
func enableColor() bool {
	h := windows.Handle(os.Stdout.Fd())
	var mode uint32
	if err := windows.GetConsoleMode(h, &mode); err != nil {
		return false
	}
	if mode&windows.ENABLE_VIRTUAL_TERMINAL_PROCESSING != 0 {
		return true
	}
	return windows.SetConsoleMode(h, mode|windows.ENABLE_VIRTUAL_TERMINAL_PROCESSING) == nil
}
