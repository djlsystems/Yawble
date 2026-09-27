package cli

import "strings"

// LogoFirstLine lets the tests count how many times the mark was printed.
func LogoFirstLine() string { return strings.SplitN(logoMark, "\n", 2)[0] }
