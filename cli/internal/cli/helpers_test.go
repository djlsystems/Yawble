package cli_test

import (
	"bytes"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/cli"
)

func run(t *testing.T, deps cli.Deps, args ...string) (int, string, string) {
	t.Helper()
	var out, errb bytes.Buffer
	deps.Stdout, deps.Stderr = &out, &errb
	if deps.Env == nil {
		deps.Env = func(string) string { return "" }
	}
	if deps.ConfigDir == "" {
		deps.ConfigDir = t.TempDir()
		seedWorkerKey(t, deps.ConfigDir)
	}
	code := cli.Execute(cli.NewRoot(deps), args)
	return code, out.String(), errb.String()
}
