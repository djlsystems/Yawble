package cli_test

import (
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/cli"
	"github.com/djlsystems/yawble/cli/internal/config"
)

// `up` tells the Host which published Microsoft app to sign in through while no Microsoft client is
// saved there. The config's microsoftClientId replaces it, and an empty value passes none: the Host
// then reads Microsoft as not set up until a person saves an app of their own.
const someMicrosoftApp = "5d1f7e20-3c4b-4a8e-9f06-1b2c3d4e5f60"

// withDefaultMicrosoftClient runs the test with id as the published app's ID, the way a build with
// DefaultMicrosoftClientID filled in reads it.
func withDefaultMicrosoftClient(t *testing.T, id string) {
	t.Helper()
	was := config.MicrosoftClientDefault
	config.MicrosoftClientDefault = id
	t.Cleanup(func() { config.MicrosoftClientDefault = was })
}

func TestUpPassesTheDefaultMicrosoftClientID(t *testing.T) {
	withDefaultMicrosoftClient(t, someMicrosoftApp)
	if got := controlRun(t, ""); !strings.Contains(got, " -e HARNESS_MICROSOFT_CLIENT_ID="+someMicrosoftApp+" ") {
		t.Errorf("control run %q lacks the default Microsoft client", got)
	}
}

func TestUpPassesThePublishedMicrosoftClientIDAsBuilt(t *testing.T) {
	if config.MicrosoftClientDefault != config.DefaultMicrosoftClientID {
		t.Fatalf("an unset microsoftClientId reads %q, not the published app's %q", config.MicrosoftClientDefault, config.DefaultMicrosoftClientID)
	}
	got := controlRun(t, "")
	if config.DefaultMicrosoftClientID == "" {
		if strings.Contains(got, "HARNESS_MICROSOFT_CLIENT_ID") {
			t.Errorf("control run %q passes a Microsoft client though none is published", got)
		}
		return
	}
	if !strings.Contains(got, " -e HARNESS_MICROSOFT_CLIENT_ID="+config.DefaultMicrosoftClientID+" ") {
		t.Errorf("control run %q lacks the published Microsoft client", got)
	}
}

func TestTheConfigsMicrosoftClientIDOverridesTheDefault(t *testing.T) {
	withDefaultMicrosoftClient(t, someMicrosoftApp)
	const own = "8f3c2a71-0b6e-4d2a-9c11-5e7f4a2b9d03"
	got := controlRun(t, "microsoftClientId = \""+own+"\"\n")
	if !strings.Contains(got, " -e HARNESS_MICROSOFT_CLIENT_ID="+own+" ") || strings.Contains(got, someMicrosoftApp) {
		t.Errorf("control run %q does not carry the configured Microsoft client alone", got)
	}
}

func TestAnEmptyMicrosoftClientIDPassesNone(t *testing.T) {
	withDefaultMicrosoftClient(t, someMicrosoftApp)
	if got := controlRun(t, "microsoftClientId = \"\"\n"); strings.Contains(got, "HARNESS_MICROSOFT_CLIENT_ID") {
		t.Errorf("control run %q passes a Microsoft client though the setting is empty", got)
	}
}

func TestConfigReadsTheDefaultMicrosoftClientUntilSetAndRefusesOneNotShapedLikeAnID(t *testing.T) {
	withDefaultMicrosoftClient(t, someMicrosoftApp)
	dir := t.TempDir()
	get := func() string {
		code, out, errOut := run(t, cli.Deps{ConfigDir: dir}, "config", "get", "microsoftClientId")
		if code != 0 {
			t.Fatalf("get: exit %d %s", code, errOut)
		}
		return strings.TrimSpace(out)
	}
	if got := get(); got != someMicrosoftApp {
		t.Errorf("unset reads %q", got)
	}
	if code, _, errOut := run(t, cli.Deps{ConfigDir: dir}, "config", "set", "microsoftClientId", ""); code != 0 {
		t.Fatalf("set empty: exit %d %s", code, errOut)
	}
	if got := get(); got != "" {
		t.Errorf("turned off reads %q", got)
	}
	if code, _, errOut := run(t, cli.Deps{ConfigDir: dir}, "config", "set", "microsoftClientId", "my-app"); code != 2 || !strings.Contains(errOut, "microsoftClientId") {
		t.Errorf("a name, not an ID: exit %d %s", code, errOut)
	}
}
