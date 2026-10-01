package cli_test

import "fmt"

func fmtSscanf(prompt string, lo, hi, def *int) (int, error) {
	return fmt.Sscanf(prompt, "Memory in MB (%d to %d) [%d]:", lo, hi, def)
}
