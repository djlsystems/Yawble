package config

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"strings"
)

// The env file (EnvFile) holds the container's secrets and settings - GH_TOKEN, provider API keys
// - as NAME=value lines, which `up` hands to the container. `yawble secret` edits it: values are never printed, and the file is readable by its owner only.

var secretName = regexp.MustCompile(`^[A-Za-z_][A-Za-z0-9_]*$`)

// ValidateSecret refuses a name the container cannot take or must not be handed, and a value
// that would break the one-line-per-name file. HARNESS_ names are the platform's own
// credentials; the Host drops them from any file, so setting one would only look like it worked.
func ValidateSecret(name, value string) error {
	if !secretName.MatchString(name) {
		return fmt.Errorf("%q is not a variable name: letters, digits and _, not starting with a digit", name)
	}
	if strings.HasPrefix(strings.ToUpper(name), "HARNESS_") {
		return fmt.Errorf("%s: names starting with HARNESS_ are the platform's own and are not taken from this file", name)
	}
	if strings.ContainsAny(value, "\r\n") {
		return fmt.Errorf("the value of %s has a line break; a value is one line", name)
	}
	return nil
}

// SetSecret sets name to value in the env file, replacing an earlier value and keeping every
// other line. The file and its folder are created owner-only when they do not exist.
func SetSecret(dir, name, value string) error {
	if err := ValidateSecret(name, value); err != nil {
		return err
	}
	lines, err := envLines(dir)
	if err != nil {
		return err
	}
	entry := name + "=" + value
	replaced := false
	for i, line := range lines {
		if lineName(line) == name {
			if !replaced {
				lines[i], replaced = entry, true
			} else {
				lines[i] = "" // a duplicate further down would win in the container; drop it
			}
		}
	}
	if !replaced {
		lines = append(lines, entry)
	}
	return writeEnvLines(dir, lines)
}

// UnsetSecret removes name from the env file, answering whether it was there.
func UnsetSecret(dir, name string) (bool, error) {
	lines, err := envLines(dir)
	if err != nil {
		return false, err
	}
	found := false
	kept := lines[:0]
	for _, line := range lines {
		if lineName(line) == name {
			found = true
			continue
		}
		kept = append(kept, line)
	}
	if !found {
		return false, nil
	}
	return true, writeEnvLines(dir, kept)
}

// SecretNames lists the names the env file sets, in file order. Never the values.
func SecretNames(dir string) ([]string, error) {
	lines, err := envLines(dir)
	if err != nil {
		return nil, err
	}
	var names []string
	for _, line := range lines {
		if name := lineName(line); name != "" {
			names = append(names, name)
		}
	}
	return names, nil
}

// SecretValue answers name's value from the env file, and whether it is set; for yawble's own
// checks (doctor asks GitHub whether GH_TOKEN still works), never for printing.
func SecretValue(dir, name string) (string, bool, error) {
	lines, err := envLines(dir)
	if err != nil {
		return "", false, err
	}
	for _, line := range lines {
		if lineName(line) == name {
			return line[len(name)+1:], true, nil
		}
	}
	return "", false, nil
}

func lineName(line string) string {
	trimmed := strings.TrimSpace(line)
	if trimmed == "" || strings.HasPrefix(trimmed, "#") {
		return ""
	}
	name, _, found := strings.Cut(line, "=")
	if !found {
		return ""
	}
	return name
}

func envLines(dir string) ([]string, error) {
	data, err := os.ReadFile(EnvFile(dir))
	if errors.Is(err, os.ErrNotExist) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	text := strings.ReplaceAll(string(data), "\r\n", "\n")
	text = strings.TrimSuffix(text, "\n")
	if text == "" {
		return nil, nil
	}
	return strings.Split(text, "\n"), nil
}

// writeEnvLines replaces the file through a temporary one beside it, owner-only, so a crash
// never leaves half a file and the values are never readable by anyone else, even briefly.
func writeEnvLines(dir string, lines []string) error {
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return err
	}
	var b strings.Builder
	for _, line := range lines {
		if line == "" {
			continue
		}
		b.WriteString(line)
		b.WriteByte('\n')
	}
	tmp, err := os.CreateTemp(dir, ".env-*")
	if err != nil {
		return err
	}
	// os.CreateTemp makes the file 0600 on Unix before anything is written to it. Windows has no
	// Unix modes; the profile folder is the boundary there, as for the rest of yawble's config.
	defer os.Remove(tmp.Name())
	if _, err := tmp.WriteString(b.String()); err != nil {
		tmp.Close()
		return err
	}
	if err := tmp.Close(); err != nil {
		return err
	}
	if err := os.Chmod(tmp.Name(), 0o600); err != nil {
		return err
	}
	return os.Rename(tmp.Name(), filepath.Join(dir, "env"))
}
