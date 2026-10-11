package cli_test

import (
	"io"
	"net/http"
	"os"
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/cli"
	"github.com/djlsystems/yawble/cli/internal/config"
	"github.com/djlsystems/yawble/cli/internal/engine"
)

// The container's secrets (GH_TOKEN, provider keys) are set with `yawble secret`, never by
// hand-editing a file. A value is piped in or typed
// at a hidden prompt, so it lands in neither the screen nor the shell's history, and is never
// printed back.
func secretDeps(t *testing.T) cli.Deps {
	t.Helper()
	deps := stubbed(engine.NewScripted())
	deps.ConfigDir = t.TempDir()
	return deps
}

func envFileText(t *testing.T, dir string) string {
	t.Helper()
	data, _ := os.ReadFile(config.EnvFile(dir))
	return string(data)
}

func TestSecretSetTakesAPipedValueAndNeverPrintsIt(t *testing.T) {
	deps := secretDeps(t)
	deps.Interactive, deps.Stdin = false, strings.NewReader("gho_piped\n")
	code, out, errOut := run(t, deps, "secret", "set", "GH_TOKEN")
	if code != 0 || envFileText(t, deps.ConfigDir) != "GH_TOKEN=gho_piped\n" {
		t.Fatalf("exit %d file %q err %q", code, envFileText(t, deps.ConfigDir), errOut)
	}
	if strings.Contains(out+errOut, "gho_piped") || !strings.Contains(out, "yawble up") {
		t.Errorf("out %q err %q", out, errOut)
	}
}

func TestSecretSetAsksAtAHiddenPromptInATerminal(t *testing.T) {
	deps := secretDeps(t)
	deps.Interactive = true
	var asked string
	deps.ReadSecret = func(prompt string) (string, error) { asked = prompt; return "typed-hidden", nil }
	code, _, errOut := run(t, deps, "secret", "set", "OPENAI_API_KEY")
	if code != 0 || envFileText(t, deps.ConfigDir) != "OPENAI_API_KEY=typed-hidden\n" || !strings.Contains(asked, "OPENAI_API_KEY") {
		t.Errorf("exit %d file %q prompt %q err %q", code, envFileText(t, deps.ConfigDir), asked, errOut)
	}
}

func TestSecretSetWithTheValueOnTheCommandLineWarnsAboutHistory(t *testing.T) {
	deps := secretDeps(t)
	code, _, errOut := run(t, deps, "secret", "set", "XAI_API_KEY=on-the-line")
	if code != 0 || envFileText(t, deps.ConfigDir) != "XAI_API_KEY=on-the-line\n" || !strings.Contains(errOut, "history") {
		t.Errorf("exit %d file %q err %q", code, envFileText(t, deps.ConfigDir), errOut)
	}
}

func TestSecretListShowsNamesAndNeverValues(t *testing.T) {
	deps := secretDeps(t)
	_ = config.SetSecret(deps.ConfigDir, "GH_TOKEN", "value-one")
	_ = config.SetSecret(deps.ConfigDir, "ANTHROPIC_API_KEY", "value-two")
	code, out, _ := run(t, deps, "secret", "list")
	if code != 0 || !strings.Contains(out, "GH_TOKEN") || !strings.Contains(out, "ANTHROPIC_API_KEY") || strings.Contains(out, "value-") {
		t.Errorf("exit %d out %q", code, out)
	}
}

func TestSecretUnsetRemovesItAndSaysWhenItWasNotSet(t *testing.T) {
	deps := secretDeps(t)
	_ = config.SetSecret(deps.ConfigDir, "GH_TOKEN", "x")
	if code, out, _ := run(t, deps, "secret", "unset", "GH_TOKEN"); code != 0 || envFileText(t, deps.ConfigDir) != "" || !strings.Contains(out, "yawble up") {
		t.Errorf("exit %d out %q file %q", code, out, envFileText(t, deps.ConfigDir))
	}
	if code, _, errOut := run(t, deps, "secret", "unset", "GH_TOKEN"); code != 1 || !strings.Contains(errOut, "not set") {
		t.Errorf("second unset: exit %d err %q", code, errOut)
	}
}

func TestSecretSetRefusesAPlatformNameAndAnEmptyValue(t *testing.T) {
	deps := secretDeps(t)
	if code, _, errOut := run(t, deps, "secret", "set", "HARNESS_KEY=x"); code != 2 || !strings.Contains(errOut, "HARNESS_") {
		t.Errorf("HARNESS_: exit %d err %q", code, errOut)
	}
	deps.Interactive, deps.Stdin = false, strings.NewReader("\n")
	if code, _, errOut := run(t, deps, "secret", "set", "GH_TOKEN"); code != 2 || !strings.Contains(errOut, "empty") {
		t.Errorf("empty: exit %d err %q", code, errOut)
	}
	if envFileText(t, deps.ConfigDir) != "" {
		t.Errorf("a refused secret was written: %q", envFileText(t, deps.ConfigDir))
	}
}

// doctor asks GitHub about the GH_TOKEN `yawble secret` set, sending it there only, and says when
// GitHub rejects it, rather than leaving it to surface when a clone breaks.
func TestDoctorSaysWhenGitHubRejectsTheToken(t *testing.T) {
	// Every request goes through this one transport: GitHub's /user answers 401 and records what
	// it was sent; everything else (the health probe) answers 200.
	var sawAuth string
	transport := roundTripFunc(func(r *http.Request) (*http.Response, error) {
		code := http.StatusOK
		if r.URL.Path == "/user" {
			sawAuth, code = r.Header.Get("Authorization"), http.StatusUnauthorized
		}
		return &http.Response{StatusCode: code, Body: io.NopCloser(strings.NewReader("{}")), Header: http.Header{}}, nil
	})

	s := engine.NewScripted()
	s.On("podman container inspect", engine.Result{Stdout: "running|" + testImage + "|" + currentLabel() + "\n"})
	deps := stubbed(s)
	deps.ConfigDir = t.TempDir()
	deps.HTTP = &http.Client{Transport: transport}
	deps.ReleaseBaseURL = "https://api.github.test"
	_ = config.SetSecret(deps.ConfigDir, "GH_TOKEN", "gho_revoked")

	_, out, errOut := run(t, deps, "doctor")
	if sawAuth != "Bearer gho_revoked" {
		t.Errorf("GitHub was asked with %q", sawAuth)
	}
	if !strings.Contains(out, "rejects") || !strings.Contains(out, "yawble secret set GH_TOKEN") || strings.Contains(out+errOut, "gho_revoked") {
		t.Errorf("out %q err %q", out, errOut)
	}
}
