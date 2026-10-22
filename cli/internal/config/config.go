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
	"sort"
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
	// Workers is how many worker containers run beside control; 0 is not chosen, which is 1.
	Workers int `toml:"workers,omitempty" json:"workers"`
	// WorkerImage overrides the worker image, which is otherwise derived from Image.
	WorkerImage string `toml:"workerImage,omitempty" json:"workerImage"`
	// GitHubAsked records that the first `up` asked whether teams will use GitHub, so it asks
	// once. Not a `config set` key: `yawble github` asks again whenever a person wants.
	GitHubAsked bool `toml:"githubAsked,omitempty" json:"-"`
	// MarketplaceCatalog is the address of the package catalog the Host reads for Marketplace > Browse.
	// nil is not chosen, which is DefaultMarketplaceCatalog; an empty value turns the catalog off.
	MarketplaceCatalog *string `toml:"marketplaceCatalog,omitempty" json:"marketplaceCatalog"`
	// MicrosoftClientID is the Application (client) ID of the public Microsoft app the Host signs in
	// through while no Microsoft client is saved. nil is not chosen, which is DefaultMicrosoftClientID;
	// an empty value turns it off.
	MicrosoftClientID *string `toml:"microsoftClientId,omitempty" json:"microsoftClientId"`
	// PreviewPorts is the range of ports `up` publishes beside the Host's for previews of a team's
	// app ("28081-28090", or a list like "28081,28085-28087"). nil is not chosen, which is
	// DefaultPreviewPorts; an empty value turns previews off.
	PreviewPorts *string `toml:"previewPorts,omitempty" json:"previewPorts"`
}

// Keys are the names `config get` and `config set` accept, in the order they are listed.
var Keys = []string{"engine", "port", "memory", "cpus", "maxRunning", "image", "workers", "workerImage", "marketplaceCatalog", "microsoftClientId", "previewPorts"}

const FileName = "config.toml"

// DefaultPort is the one default this package knows, because `config get port` must answer a
// number and 8080 is what the Host listens on inside the container.
const DefaultPort = 8080

// DefaultMarketplaceCatalog is where the published package catalog is - yawble.ai's - unless the config names
// another address or none.
const DefaultMarketplaceCatalog = "https://yawble.ai/api/marketplace/catalog.json"

// DefaultMicrosoftClientID is the published Microsoft app's Application (client) ID: a multi-tenant
// public client that signs in with a code, registered in the DJL Systems tenant as "Yawble".
const DefaultMicrosoftClientID = "40149369-2b22-45de-a449-cbd7f4756377"

// DefaultPreviewPorts is the preview range an unset previewPorts reads: ten ports above the
// 8000-8999 band where dev servers and local tools default (8081, 8088, 8888 ...), below every
// operating system's ephemeral range (Linux from 32768, Windows and macOS from 49152), so the OS
// never hands one out as the source port of an outgoing connection, and the default of no common
// software.
const DefaultPreviewPorts = "28081-28090"

// maxPreviewPorts bounds how many ports a range may publish: each is a published port on the
// engine, and a typo like 2000-20000 must not ask for eighteen thousand.
const maxPreviewPorts = 100

// MicrosoftClientDefault is what an unset microsoftClientId reads: DefaultMicrosoftClientID. A variable
// only so tests drive `up` with a default that is not empty.
var MicrosoftClientDefault = DefaultMicrosoftClientID

func Path(dir string) string { return filepath.Join(dir, FileName) }

// EnvFile is the file of NAME=value lines handed to the container at `up`, when it exists.
func EnvFile(dir string) string { return filepath.Join(dir, "env") }

var envNames = map[string]string{
	"engine":      "YAWBLE_ENGINE",
	"port":        "YAWBLE_PORT",
	"memory":      "YAWBLE_MEMORY",
	"cpus":        "YAWBLE_CPUS",
	"maxRunning":  "YAWBLE_MAX_RUNNING",
	"image":       "YAWBLE_IMAGE",
	"workers":     "YAWBLE_WORKERS",
	"workerImage": "YAWBLE_WORKER_IMAGE",
	// Only an address: an empty variable is not set, so the file is where the catalog is turned off.
	"marketplaceCatalog": "YAWBLE_MARKETPLACE_CATALOG",
	// The same: the file is where the default client is turned off.
	"microsoftClientId": "YAWBLE_MICROSOFT_CLIENT_ID",
	// The same: the file is where previews are turned off.
	"previewPorts": "YAWBLE_PREVIEW_PORTS",
}

// WorkerCount is the number of workers the config asks for: 1 when none was chosen.
func (c Config) WorkerCount() int {
	if c.Workers < 1 {
		return 1
	}
	return c.Workers
}

// Catalog is the package catalog's address `up` passes to the Host: the default when none was
// chosen, and "" when the config turned it off.
func (c Config) Catalog() string {
	if c.MarketplaceCatalog == nil {
		return DefaultMarketplaceCatalog
	}
	return *c.MarketplaceCatalog
}

// MicrosoftClient is the Microsoft client ID `up` passes to the Host: the default when none was chosen,
// and "" when the config turned it off.
func (c Config) MicrosoftClient() string {
	if c.MicrosoftClientID == nil {
		return MicrosoftClientDefault
	}
	return *c.MicrosoftClientID
}

// Preview is the preview ports `up` publishes, in order: the default range when none was chosen,
// none when the config turned previews off. The value was validated when it was read or set.
func (c Config) Preview() []int {
	spec := DefaultPreviewPorts
	if c.PreviewPorts != nil {
		spec = *c.PreviewPorts
	}
	ports, _ := ParsePorts(spec)
	return ports
}

// ParsePorts reads a port range as a person writes one: comma-separated ports and low-high ranges,
// each between 1 and 65535. The answer is sorted with no repeats; an empty spec is none.
func ParsePorts(spec string) ([]int, error) {
	seen := map[int]bool{}
	var ports []int
	for _, item := range strings.Split(spec, ",") {
		item = strings.TrimSpace(item)
		if item == "" {
			continue
		}
		low, high, isRange := strings.Cut(item, "-")
		from, err := strconv.Atoi(strings.TrimSpace(low))
		to := from
		if err == nil && isRange {
			to, err = strconv.Atoi(strings.TrimSpace(high))
		}
		if err != nil || from < 1 || to > 65535 || to < from {
			return nil, fmt.Errorf("%q is not a port or a low-high range of ports between 1 and 65535", item)
		}
		for p := from; p <= to; p++ {
			if !seen[p] {
				seen[p] = true
				ports = append(ports, p)
			}
			if len(ports) > maxPreviewPorts {
				return nil, fmt.Errorf("%q is more than %d ports", spec, maxPreviewPorts)
			}
		}
	}
	sort.Ints(ports)
	return ports, nil
}

// FormatPorts writes ports back as ParsePorts reads them, each run of consecutive ports as one
// low-high range: "28081-28090", "28081-28082,28084-28090". None is "".
func FormatPorts(ports []int) string {
	var parts []string
	for i := 0; i < len(ports); {
		j := i
		for j+1 < len(ports) && ports[j+1] == ports[j]+1 {
			j++
		}
		if j == i {
			parts = append(parts, strconv.Itoa(ports[i]))
		} else {
			parts = append(parts, fmt.Sprintf("%d-%d", ports[i], ports[j]))
		}
		i = j + 1
	}
	return strings.Join(parts, ",")
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
	return path, WriteAtomic(path, []byte(b.String()), 0o644)
}

// WriteAtomic writes data to path whole or not at all: into a temporary file beside it, then a
// rename over it, so a crash or an interrupt mid-write never leaves a truncated file the next
// start refuses to read.
func WriteAtomic(path string, data []byte, mode os.FileMode) error {
	dir := filepath.Dir(path)
	tmp, err := os.CreateTemp(dir, filepath.Base(path)+".*.tmp")
	if err != nil {
		return err
	}
	name := tmp.Name()
	cleanup := func() { _ = os.Remove(name) }
	if _, err := tmp.Write(data); err != nil {
		tmp.Close()
		cleanup()
		return err
	}
	if err := tmp.Close(); err != nil {
		cleanup()
		return err
	}
	if err := os.Chmod(name, mode); err != nil {
		cleanup()
		return err
	}
	if err := os.Rename(name, path); err != nil {
		cleanup()
		return err
	}
	return nil
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
	case "workers":
		return strconv.Itoa(c.WorkerCount()), nil
	case "workerImage":
		return c.WorkerImage, nil
	case "marketplaceCatalog":
		return c.Catalog(), nil
	case "microsoftClientId":
		return c.MicrosoftClient(), nil
	case "previewPorts":
		if c.PreviewPorts == nil {
			return DefaultPreviewPorts, nil
		}
		return *c.PreviewPorts, nil
	}
	return "", unknownKey(key)
}

// The unit is required. Podman reads a bare number as BYTES, so "12" would start a container
// with a 12-byte memory limit; nobody means that.
var memoryShape = regexp.MustCompile(`^[0-9]+[kmgKMG]$`)

// guidShape is an Application (client) ID as Microsoft Entra writes one.
var guidShape = regexp.MustCompile(`^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$`)

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
			return fmt.Errorf("memory must be a size like 8g or 12g (podman's --memory syntax), each worker's, not %q", value)
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
			return fmt.Errorf("maxRunning must be a whole number (0 = the Host's default: CPUs - 1, bounded by memory), not %q", value)
		}
		c.MaxRunning = n
	case "image":
		if strings.ContainsAny(value, " \t") {
			return fmt.Errorf("image must be one reference like ghcr.io/djlsystems/yawble:2026.09.24.1, not %q", value)
		}
		c.Image = value
	case "workers":
		n, err := strconv.Atoi(value)
		if err != nil || n < 1 {
			return fmt.Errorf("workers must be a whole number, 1 or more, not %q", value)
		}
		c.Workers = n
	case "workerImage":
		if strings.ContainsAny(value, " \t") {
			return fmt.Errorf("workerImage must be one reference like ghcr.io/djlsystems/yawble:2026.09.24.1-worker, not %q", value)
		}
		c.WorkerImage = value
	case "marketplaceCatalog":
		if value != "" && (strings.ContainsAny(value, " \t") || !(strings.HasPrefix(value, "https://") || strings.HasPrefix(value, "http://"))) {
			return fmt.Errorf("marketplaceCatalog must be the address of a catalog.json like %s (or empty to turn the catalog off), not %q", DefaultMarketplaceCatalog, value)
		}
		c.MarketplaceCatalog = &value
	case "microsoftClientId":
		if value != "" && !guidShape.MatchString(value) {
			return fmt.Errorf("microsoftClientId must be an Application (client) ID like 00000000-0000-0000-0000-000000000000 (or empty to turn the default Microsoft app off), not %q", value)
		}
		c.MicrosoftClientID = &value
	case "previewPorts":
		if _, err := ParsePorts(value); err != nil {
			return fmt.Errorf("previewPorts must be a range of ports like %s or a list like 28081,28085-28087 (or empty to turn previews off): %w", DefaultPreviewPorts, err)
		}
		c.PreviewPorts = &value
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
		{"workerImage", c.WorkerImage},
	}
	if c.Workers != 0 {
		checks = append(checks, struct{ key, value string }{"workers", strconv.Itoa(c.Workers)})
	}
	if c.Port != 0 {
		checks = append(checks, struct{ key, value string }{"port", strconv.Itoa(c.Port)})
	}
	if c.MarketplaceCatalog != nil {
		checks = append(checks, struct{ key, value string }{"marketplaceCatalog", *c.MarketplaceCatalog})
	}
	if c.MicrosoftClientID != nil {
		checks = append(checks, struct{ key, value string }{"microsoftClientId", *c.MicrosoftClientID})
	}
	if c.PreviewPorts != nil {
		checks = append(checks, struct{ key, value string }{"previewPorts", *c.PreviewPorts})
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
