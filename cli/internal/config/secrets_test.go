package config_test

import (
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/config"
)

// The container's secrets and settings (GH_TOKEN, provider keys) are kept
// in yawble's env file, which `up` hands to the container; `yawble secret` edits it.
func TestSecretsAreSetReplacedListedAndUnsetKeepingTheRestOfTheFile(t *testing.T) {
	dir := t.TempDir()
	path := config.EnvFile(dir)
	if err := os.WriteFile(path, []byte("# kept\nANTHROPIC_API_KEY=a=b\n"), 0o600); err != nil {
		t.Fatal(err)
	}

	if err := config.SetSecret(dir, "GH_TOKEN", "one"); err != nil {
		t.Fatal(err)
	}
	if err := config.SetSecret(dir, "GH_TOKEN", "two"); err != nil {
		t.Fatal(err)
	}
	data, _ := os.ReadFile(path)
	if got := string(data); got != "# kept\nANTHROPIC_API_KEY=a=b\nGH_TOKEN=two\n" {
		t.Errorf("file %q", got)
	}

	names, err := config.SecretNames(dir)
	if err != nil || strings.Join(names, ",") != "ANTHROPIC_API_KEY,GH_TOKEN" {
		t.Errorf("names %q err %v", names, err)
	}

	removed, err := config.UnsetSecret(dir, "GH_TOKEN")
	if err != nil || !removed {
		t.Errorf("removed %v err %v", removed, err)
	}
	if again, _ := config.UnsetSecret(dir, "GH_TOKEN"); again {
		t.Error("a second unset found it again")
	}
	data, _ = os.ReadFile(path)
	if string(data) != "# kept\nANTHROPIC_API_KEY=a=b\n" {
		t.Errorf("after unset %q", data)
	}
}

func TestANewEnvFileIsReadableByItsOwnerOnly(t *testing.T) {
	dir := filepath.Join(t.TempDir(), "yawble")
	if err := config.SetSecret(dir, "GH_TOKEN", "x"); err != nil {
		t.Fatal(err)
	}
	info, err := os.Stat(config.EnvFile(dir))
	if err != nil {
		t.Fatal(err)
	}
	if runtime.GOOS != "windows" && info.Mode().Perm() != 0o600 {
		t.Errorf("mode %04o", info.Mode().Perm())
	}
}

func TestBadNamesAndValuesAreRefusedAndNothingIsWritten(t *testing.T) {
	dir := t.TempDir()
	for name, value := range map[string]string{
		"HARNESS_KEY": "x", // the platform's own credentials; the Host drops these anyway
		"harness_url": "x",
		"1ST":         "x",
		"HAS SPACE":   "x",
		"":            "x",
		"OK_NAME":     "two\nlines",
	} {
		if err := config.SetSecret(dir, name, value); err == nil {
			t.Errorf("%q=%q was accepted", name, value)
		}
	}
	if _, err := os.Stat(config.EnvFile(dir)); !os.IsNotExist(err) {
		t.Errorf("a refused secret wrote the file: %v", err)
	}
}
