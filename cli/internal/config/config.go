// Package config is the CLI's own settings: one TOML file under the OS config directory, with
// YAWBLE_* environment overrides above it. It holds only what cannot be asked of the engine;
// the container, volume and image are state the engine holds and the CLI queries.
package config

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"

	"github.com/BurntSushi/toml"
)

// Config is the file's shape. A zero value means "not chosen": Defaults in internal/instance
// derives the run-time figure from the machine, or the build's pin for the image.
type Config struct {
	Engine     string `toml:"engine" json:"engine"`
	Port       int    `toml:"port" json:"port"`
	Memory     string `toml:"memory" json:"memory"`
	CPUs       int    `toml:"cpus" json:"cpus"`
	MaxRunning int    `toml:"maxRunning" json:"maxRunning"`
	Image      string `toml:"image" json:"image"`
	// GitHubAsked records that the first `up` asked whether teams will use GitHub, so it asks
	// once. Not a `config set` key: `yawble github` asks again whenever a person wants.
	GitHubAsked bool `toml:"githubAsked,omitempty" json:"-"`
}

// Keys are the names `config get` and `config set` accept, in the order they are listed.
var Keys = []string{"engine", "port", "memory", "cpus", "maxRunning", "image"}

const FileName = "config.toml"

// DefaultPort is the one default this package knows, because `config get port` must answer a
// number and 8080 is what the Host listens on inside the container.
const DefaultPort = 8080

func Path(dir string) string { return filepath.Join(dir, FileName) }

// EnvFile is the file of NAME=value lines handed to the container at `up`, when it exists.
func EnvFile(dir string) string { return filepath.Join(dir, "env") }

var envNames = map[string]string{
	"engine":     "YAWBLE_ENGINE",
	"port":       "YAWBLE_PORT",
	"memory":     "YAWBLE_MEMORY",
	"cpus":       "YAWBLE_CPUS",
	"maxRunning": "YAWBLE_MAX_RUNNING",
	"image":      "YAWBLE_IMAGE",
}

// Load reads the file if it exists, then applies YAWBLE_* overrides. A missing file is the zero
// config; a corrupt one is an error naming the path, and the file is not touched.
func Load(dir string, env func(string) string) (Config, string, error) {
	var c Config
	path := Path(dir)
	data, err := os.ReadFile(path)
	switch {
	case errors.Is(err, os.ErrNotExist):
	case err != nil:
		return c, path, fmt.Errorf("reading %s: %w", path, err)
	default:
		meta, err := toml.Decode(string(data), &c)
		if err != nil {
			return c, path, fmt.Errorf("%s is not valid TOML: %w", path, err)
		}
		// A key the file does not have is a typo, and a typo silently ignored is a setting the
		// person believes is in force. Named, with the real keys beside it.
		if undecoded := meta.Undecoded(); len(undecoded) > 0 {
			names := make([]string, len(undecoded))
			for i, k := range undecoded {
				names[i] = k.String()
			}
			return c, path, fmt.Errorf("%s: unknown key %s (keys: %s)", path, strings.Join(names, ", "), strings.Join(Keys, ", "))
		}
		// A hand-edited value goes through the same rules as `set`, or `memory = "12"` reaches
		// podman as a twelve-byte limit.
		if err := c.validate(); err != nil {
			return c, path, fmt.Errorf("%s: %w", path, err)
		}
	}
	for _, key := range Keys {
		if v := env(envNames[key]); v != "" {
			if err := c.Set(key, v); err != nil {
				return c, path, fmt.Errorf("%s: %w", envNames[key], err)
			}
		}
	}
	return c, path, nil
}

// Save writes the whole file. 0644: nothing in it is a secret; the env file beside it is 0600.
func Save(dir string, c Config) (string, error) {
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return "", err
	}
	path := Path(dir)
	var b strings.Builder
	b.WriteString("# yawble settings. Change with `yawble config set <key> <value>`.\n")
	if err := toml.NewEncoder(&b).Encode(c); err != nil {
		return path, err
	}
	return path, os.WriteFile(path, []byte(b.String()), 0o644)
}

// Get answers one key as a person would want it printed: the port default is applied, because
// "what port" deserves a number and not 0.
func (c Config) Get(key string) (string, error) {
	switch key {
	case "engine":
		return c.Engine, nil
	case "port":
		if c.Port == 0 {
			return strconv.Itoa(DefaultPort), nil
		}
		return strconv.Itoa(c.Port), nil
	case "memory":
		return c.Memory, nil
	case "cpus":
		return strconv.Itoa(c.CPUs), nil
	case "maxRunning":
		return strconv.Itoa(c.MaxRunning), nil
	case "image":
		return c.Image, nil
	}
	return "", unknownKey(key)
}

// The unit is required. Podman reads a bare number as BYTES, so "12" would start a container
// with a 12-byte memory limit; nobody means that.
var memoryShape = regexp.MustCompile(`^[0-9]+[kmgKMG]$`)

// Set validates and assigns. The error names the key and the shape it accepts, because it is
// printed to a person who typed the value.
func (c *Config) Set(key, value string) error {
	value = strings.TrimSpace(value)
	switch key {
	case "engine":
		if value != "" && value != "podman" && value != "docker" {
			return fmt.Errorf("engine must be podman or docker (or empty to detect), not %q", value)
		}
		c.Engine = value
	case "port":
		n, err := strconv.Atoi(value)
		if err != nil || n < 1 || n > 65535 {
			return fmt.Errorf("port must be a number between 1 and 65535, not %q", value)
		}
		c.Port = n
	case "memory":
		if value != "" && !memoryShape.MatchString(value) {
			return fmt.Errorf("memory must be a size like 8g or 12g (podman's --memory syntax), not %q", value)
		}
		c.Memory = value
	case "cpus":
		n, err := strconv.Atoi(value)
		if err != nil || n < 0 {
			return fmt.Errorf("cpus must be a whole number of CPUs (0 = derive from the machine), not %q", value)
		}
		c.CPUs = n
	case "maxRunning":
		n, err := strconv.Atoi(value)
		if err != nil || n < 0 {
			return fmt.Errorf("maxRunning must be a whole number (0 = same as cpus), not %q", value)
		}
		c.MaxRunning = n
	case "image":
		if strings.ContainsAny(value, " \t") {
			return fmt.Errorf("image must be one reference like ghcr.io/djlsystems/yawble:2026.09.24.1, not %q", value)
		}
		c.Image = value
	default:
		return unknownKey(key)
	}
	return nil
}

// validate re-applies every field through Set's rules. A zero value is "not chosen" and passes;
// Set's own zero checks (port 0) are skipped for that reason.
func (c *Config) validate() error {
	probe := *c
	checks := []struct{ key, value string }{
		{"engine", c.Engine},
		{"memory", c.Memory},
		{"cpus", strconv.Itoa(c.CPUs)},
		{"maxRunning", strconv.Itoa(c.MaxRunning)},
		{"image", c.Image},
	}
	if c.Port != 0 {
		checks = append(checks, struct{ key, value string }{"port", strconv.Itoa(c.Port)})
	}
	for _, check := range checks {
		if err := probe.Set(check.key, check.value); err != nil {
			return err
		}
	}
	return nil
}

func unknownKey(key string) error {
	return fmt.Errorf("unknown key %q (keys: %s)", key, strings.Join(Keys, ", "))
}
