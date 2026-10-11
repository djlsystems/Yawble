package remote

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"

	"github.com/BurntSushi/toml"
)

// Config is remote.toml: which provider is enabled, its credential, and the last URL the sidecar
// was seen at (a fallback for status when the banner has scrolled out of the log). Owner-only,
// because the token is a secret; it never reaches a label, a log line or a command argument.
type Config struct {
	Provider   string     `toml:"provider"`
	Credential Credential `toml:"credential"`
	LastURL    string     `toml:"lastUrl"`
}

const (
	FileName    = "remote.toml"
	EnvFileName = "remote.env"
)

func Path(dir string) string    { return filepath.Join(dir, FileName) }
func EnvPath(dir string) string { return filepath.Join(dir, EnvFileName) }

// Load answers the config and whether a file existed.
func Load(dir string) (Config, bool, error) {
	var c Config
	data, err := os.ReadFile(Path(dir))
	if errors.Is(err, os.ErrNotExist) {
		return c, false, nil
	}
	if err != nil {
		return c, false, fmt.Errorf("reading %s: %w", Path(dir), err)
	}
	if _, err := toml.Decode(string(data), &c); err != nil {
		return c, true, fmt.Errorf("%s is not valid TOML: %w", Path(dir), err)
	}
	return c, true, nil
}

// Save writes the file with mode 0600. On Windows the profile directory is the boundary.
func Save(dir string, c Config) error {
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return err
	}
	var b strings.Builder
	b.WriteString("# yawble remote access. The credential here is a secret; keep this file owner-only.\n")
	if err := toml.NewEncoder(&b).Encode(c); err != nil {
		return err
	}
	return writeOwnerOnly(Path(dir), []byte(b.String()))
}

// WriteEnvFile writes the sidecar's environment as NAME=value lines, owner-only, so the engine
// is handed a file path and the token never appears in a process listing. Empty when the
// provider needs nothing, and then no file is written.
func WriteEnvFile(dir string, env map[string]string) (string, error) {
	if len(env) == 0 {
		_ = os.Remove(EnvPath(dir))
		return "", nil
	}
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return "", err
	}
	keys := make([]string, 0, len(env))
	for k := range env {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	var b strings.Builder
	for _, k := range keys {
		b.WriteString(k + "=" + env[k] + "\n")
	}
	if err := writeOwnerOnly(EnvPath(dir), []byte(b.String())); err != nil {
		return "", err
	}
	return EnvPath(dir), nil
}

func writeOwnerOnly(path string, data []byte) error {
	if err := os.WriteFile(path, data, 0o600); err != nil {
		return err
	}
	return os.Chmod(path, 0o600)
}

// Forget removes the config and the env file. Missing is fine.
func Forget(dir string) error {
	for _, p := range []string{Path(dir), EnvPath(dir)} {
		if err := os.Remove(p); err != nil && !errors.Is(err, os.ErrNotExist) {
			return err
		}
	}
	return nil
}
