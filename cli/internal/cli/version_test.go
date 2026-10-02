package cli_test

import (
	"bytes"
	"encoding/json"
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/buildinfo"
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

func TestVersionPrintsTheBuildAndSaysWhenNoImageIsPinned(t *testing.T) {
	buildinfo.Version, buildinfo.Commit, buildinfo.ImageTag = "v0.1.0", "abc1234", ""
	t.Cleanup(func() { buildinfo.Version, buildinfo.Commit, buildinfo.ImageTag = "dev", "", "" })

	code, out, _ := run(t, cli.Deps{}, "version")

	if code != 0 {
		t.Fatalf("exit %d", code)
	}
	for _, want := range []string{"yawble v0.1.0", "abc1234", "no image pinned"} {
		if !strings.Contains(out, want) {
			t.Errorf("output %q lacks %q", out, want)
		}
	}
}

func TestVersionJSONCarriesThePinnedImage(t *testing.T) {
	buildinfo.Version, buildinfo.ImageTag = "v0.1.0", "2026.09.24.1"
	t.Cleanup(func() { buildinfo.Version, buildinfo.ImageTag = "dev", "" })

	code, out, _ := run(t, cli.Deps{}, "version", "--json")

	if code != 0 {
		t.Fatalf("exit %d", code)
	}
	var got struct {
		Version string `json:"version"`
		Image   string `json:"image"`
	}
	if err := json.Unmarshal([]byte(out), &got); err != nil {
		t.Fatalf("not json: %v: %s", err, out)
	}
	if got.Image != "ghcr.io/djlsystems/yawble:2026.09.24.1" || got.Version != "v0.1.0" {
		t.Errorf("got %+v", got)
	}
}

func TestAnUnknownCommandExitsTwo(t *testing.T) {
	code, _, errOut := run(t, cli.Deps{}, "frobnicate")
	if code != 2 || !strings.Contains(errOut, "frobnicate") {
		t.Errorf("exit %d, stderr %q", code, errOut)
	}
}

// `completion` (Cobra's, for Tab completion in a shell) is not listed in
// `yawble --help`, where most people never need it, and still works for anyone who runs it.
func TestCompletionIsHiddenFromHelpAndStillWorks(t *testing.T) {
	code, out, errOut := run(t, cli.Deps{}, "--help")
	if code != 0 || strings.Contains(out, "completion") {
		t.Errorf("help lists completion: exit %d out %q err %q", code, out, errOut)
	}
	code, out, _ = run(t, cli.Deps{}, "completion", "powershell")
	if code != 0 || !strings.Contains(out, "Register-ArgumentCompleter") {
		t.Errorf("completion powershell: exit %d, %d bytes", code, len(out))
	}
}
