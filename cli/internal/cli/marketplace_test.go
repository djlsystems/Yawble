package cli_test

import (
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/cli"
	"github.com/djlsystems/yawble/cli/internal/config"
)

// `up` tells the Host where the package catalog is published - yawble.ai's - so Solutions > Get
// started can list it. The config's marketplaceCatalog replaces the address, and an empty value
// passes none: the Host then says the catalog is not checked.
const publishedCatalog = "https://yawble.ai/api/marketplace/catalog.json"

func controlRun(t *testing.T, saved string) string {
	t.Helper()
	s, deps := dockerUp("darwin")
	deps.ConfigDir = t.TempDir()
	writeConfig(t, deps.ConfigDir, "memory = \"8g\"\ncpus = 4\ngithubAsked = true\n"+saved)
	code, out, errOut := run(t, deps, "up", "--no-browser")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	runs := callsContaining(s, "docker run -d --name yawble ")
	if len(runs) != 1 {
		t.Fatalf("control runs %q:\n%s", runs, calls(s))
	}
	return runs[0]
}

func TestUpPassesThePublishedCatalogByDefault(t *testing.T) {
	if got := controlRun(t, ""); !strings.Contains(got, " -e HARNESS_MARKETPLACE_CATALOG="+publishedCatalog+" ") {
		t.Errorf("control run %q lacks the published catalog", got)
	}
}

func TestThePublishedCatalogIsYawbleAis(t *testing.T) {
	if config.DefaultMarketplaceCatalog != publishedCatalog {
		t.Errorf("the published catalog is %q, not yawble.ai's %q", config.DefaultMarketplaceCatalog, publishedCatalog)
	}
}

func TestTheConfigsMarketplaceCatalogOverridesTheAddress(t *testing.T) {
	got := controlRun(t, "marketplaceCatalog = \"https://mirror.example.test/download/catalog.json\"\n")
	if !strings.Contains(got, " -e HARNESS_MARKETPLACE_CATALOG=https://mirror.example.test/download/catalog.json ") || strings.Contains(got, publishedCatalog) {
		t.Errorf("control run %q does not carry the configured catalog alone", got)
	}
}

func TestAnEmptyMarketplaceCatalogPassesNoAddress(t *testing.T) {
	if got := controlRun(t, "marketplaceCatalog = \"\"\n"); strings.Contains(got, "HARNESS_MARKETPLACE_CATALOG") {
		t.Errorf("control run %q passes a catalog though the setting is empty", got)
	}
}

func TestConfigReadsThePublishedCatalogUntilSetAndEmptyUntilSetBack(t *testing.T) {
	dir := t.TempDir()
	get := func() string {
		code, out, errOut := run(t, cli.Deps{ConfigDir: dir}, "config", "get", "marketplaceCatalog")
		if code != 0 {
			t.Fatalf("get: exit %d %s", code, errOut)
		}
		return strings.TrimSpace(out)
	}
	if got := get(); got != publishedCatalog {
		t.Errorf("unset reads %q", got)
	}
	if code, _, errOut := run(t, cli.Deps{ConfigDir: dir}, "config", "set", "marketplaceCatalog", ""); code != 0 {
		t.Fatalf("set empty: exit %d %s", code, errOut)
	}
	if got := get(); got != "" {
		t.Errorf("turned off reads %q", got)
	}
	if code, _, errOut := run(t, cli.Deps{ConfigDir: dir}, "config", "set", "marketplaceCatalog", "catalog.json"); code != 2 || !strings.Contains(errOut, "marketplaceCatalog") {
		t.Errorf("a bare name: exit %d %s", code, errOut)
	}
}
