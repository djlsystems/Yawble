// Package plugin reads a built plugin folder on this computer before anything is copied into the
// instance. The rules are the Host's (src/Harness.Host/PluginManifest.cs and PluginCatalog.cs), in
// the same order and with the same sentences, so a folder `yawble plugin install` accepts is one
// the Host loads, and a refusal here names the field the Host would have named.
package plugin

import (
	"bytes"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
)

const (
	FileName      = "plugin.json"
	SchemaVersion = 1
	ProtocolV1    = "harness.member/1"
)

// Manifest is what the install needs from plugin.json: where it goes and which files must be
// executable. Everything else is the Host's to read.
type Manifest struct {
	ID, Name, Version string
	// Executables are executable.path and every platforms entry, relative to the folder. The Host
	// runs one of them, chosen by the container's processor, so the install marks them all.
	Executables []string
}

var (
	pluginID    = regexp.MustCompile(`^[a-z0-9][a-z0-9-]{0,47}$`)
	safeVersion = regexp.MustCompile(`^[A-Za-z0-9.+_-]{1,64}$`)
	configName  = regexp.MustCompile(`^[A-Za-z][A-Za-z0-9_-]{0,63}$`)
	eventSuffix = regexp.MustCompile(`^[a-z][a-z0-9.-]{0,63}$`)
)

// ValidID is MemberRef.IsValidPluginId.
func ValidID(id string) bool { return pluginID.MatchString(id) }

// ValidVersion is PluginManifest's plain version: the name of a directory, nothing more.
func ValidVersion(v string) bool { return safeVersion.MatchString(v) && v != "." && v != ".." }

// Refusal is a folder the Host would not load, in one sentence naming the field.
type Refusal struct{ Reason string }

func (r Refusal) Error() string { return r.Reason }

func refuse(format string, args ...any) (Manifest, error) {
	return Manifest{}, Refusal{fmt.Sprintf(format, args...)}
}

// Read checks folder as the Host would check it once installed: the manifest's fields, then that
// the executable and every skill are files inside the folder after symlinks. The one thing it does
// not require is an execute bit, because the install sets it (a folder copied from Windows has
// none).
func Read(folder string) (Manifest, error) {
	info, err := os.Stat(folder)
	if err != nil {
		return refuse("%s cannot be read: %v", folder, err)
	}
	if !info.IsDir() {
		return refuse("%s is not a folder; name the folder that holds %s", folder, FileName)
	}
	raw, err := os.ReadFile(filepath.Join(folder, FileName))
	if err != nil {
		if os.IsNotExist(err) {
			return refuse("%s has no %s; name a built plugin version, the folder that holds it", folder, FileName)
		}
		return refuse("%s could not be read: %v", FileName, err)
	}
	m, skills, err := Parse(raw)
	if err != nil {
		return Manifest{}, err
	}
	for _, exe := range m.Executables {
		path, ok := inside(folder, exe)
		if !ok {
			return refuse("`%s` resolves outside %s/.", exe, m.Version)
		}
		if st, err := os.Stat(path); err != nil || st.IsDir() {
			return refuse("its executable %s does not exist.", exe)
		}
	}
	for _, skill := range skills {
		path, ok := inside(folder, skill)
		if st, err := os.Stat(path); !ok || err != nil || st.IsDir() {
			return refuse("its skill %s does not exist inside %s/.", skill, m.Version)
		}
	}
	return m, nil
}

// Parse is PluginManifest.Parse: the manifest, and the skills it names; or the refusal.
func Parse(raw []byte) (Manifest, []string, error) {
	dec := json.NewDecoder(bytes.NewReader(relax(raw)))
	dec.UseNumber()
	var doc any
	if err := dec.Decode(&doc); err != nil {
		m, err := refuse("%s is not JSON: %v", FileName, err)
		return m, nil, err
	}
	root, ok := doc.(map[string]any)
	if !ok {
		m, err := refuse("%s must be a JSON object.", FileName)
		return m, nil, err
	}
	m, skills, reason := parse(root)
	if reason != "" {
		return Manifest{}, nil, Refusal{reason}
	}
	return m, skills, nil
}

func parse(root map[string]any) (Manifest, []string, string) {
	var m Manifest
	schema, ok := root["schemaVersion"].(json.Number)
	n, err := schema.Int64()
	if !ok || err != nil || n != int64(int32(n)) {
		return m, nil, "`schemaVersion` is required and must be a number."
	}
	if n != SchemaVersion {
		return m, nil, fmt.Sprintf("`schemaVersion` %d is not one this Host reads (it reads %d).", n, SchemaVersion)
	}
	id, ok := text(root, "id")
	if !ok {
		return m, nil, "`id` is required."
	}
	if !ValidID(id) {
		return m, nil, fmt.Sprintf("`id` '%s' must be lowercase letters, digits and hyphens, starting with a letter or digit, at most 48 characters.", id)
	}
	name, ok := text(root, "name")
	if !ok {
		return m, nil, "`name` is required."
	}
	if _, ok := text(root, "description"); !ok {
		return m, nil, "`description` is required."
	}
	version, ok := text(root, "version")
	if !ok {
		return m, nil, "`version` is required."
	}
	if !ValidVersion(version) {
		return m, nil, fmt.Sprintf("`version` '%s' must be a plain version such as 0.1.0.", version)
	}
	protocol, ok := text(root, "protocol")
	if !ok {
		return m, nil, "`protocol` is required."
	}
	if protocol != ProtocolV1 {
		return m, nil, fmt.Sprintf("`protocol` '%s' is not one this Host speaks (it speaks %s).", protocol, ProtocolV1)
	}
	executable, _ := root["executable"].(map[string]any)
	path, ok := text(executable, "path")
	if executable == nil || !ok {
		return m, nil, "`executable.path` is required."
	}
	if r := relativeRefusal("executable.path", path); r != "" {
		return m, nil, r
	}
	if args, present := executable["args"]; present && !stringArray(args) {
		return m, nil, "`executable.args` must be an array of strings."
	}
	if t, present := root["timeoutSeconds"]; present {
		num, ok := t.(json.Number)
		v, err := num.Int64()
		if !ok || err != nil || v <= 0 || v != int64(int32(v)) {
			return m, nil, "`timeoutSeconds` must be a positive whole number."
		}
	}
	if c, present := root["config"]; present {
		fields, ok := c.(map[string]any)
		if !ok {
			return m, nil, "`config` must be an object."
		}
		for _, key := range orderedKeys(fields) {
			if r := configField(key, fields[key]); r != "" {
				return m, nil, r
			}
		}
	}
	if s, present := root["secrets"]; present {
		secrets, ok := s.(map[string]any)
		if !ok {
			return m, nil, "`secrets` must be an object."
		}
		for _, key := range orderedKeys(secrets) {
			if !configName.MatchString(key) {
				return m, nil, fmt.Sprintf("`secrets.%s` is not a usable name.", key)
			}
			secret, ok := secrets[key].(map[string]any)
			if !ok {
				return m, nil, fmt.Sprintf("`secrets.%s` must be an object.", key)
			}
			if _, has := secret["value"]; has {
				return m, nil, fmt.Sprintf("`secrets.%s` carries a value. A manifest names a secret; the value is set with `secret set` and bound by a logical key.", key)
			}
		}
	}
	if e, present := root["events"]; present {
		events, ok := e.(map[string]any)
		if !ok {
			return m, nil, "`events` must be an object."
		}
		if p, present := events["publishes"]; present {
			list, ok := p.([]any)
			if !ok {
				return m, nil, "`events.publishes` must be an array."
			}
			for _, item := range list {
				obj, _ := item.(map[string]any)
				t, ok := text(obj, "type")
				if obj == nil || !ok || !eventSuffix.MatchString(t) {
					return m, nil, "Each of `events.publishes` needs a `type`: lowercase letters, digits, hyphens and dots."
				}
			}
		}
	}
	var skills []string
	if s, present := root["skills"]; present {
		if !stringArray(s) {
			return m, nil, "`skills` must be an array of relative paths."
		}
		for _, item := range s.([]any) {
			skill := item.(string)
			if r := relativeRefusal("skills", skill); r != "" {
				return m, nil, r
			}
			skills = append(skills, skill)
		}
	}
	executables := []string{path}
	if p, present := root["platforms"]; present && p != nil {
		platforms, ok := p.(map[string]any)
		if !ok {
			return m, nil, "`platforms` must be an object of platform to path."
		}
		for _, key := range orderedKeys(platforms) {
			value, ok := platforms[key].(string)
			if !ok {
				return m, nil, fmt.Sprintf("`platforms.%s` must be a path.", key)
			}
			if r := relativeRefusal("platforms."+key, value); r != "" {
				return m, nil, r
			}
			executables = append(executables, value)
		}
	}
	return Manifest{ID: id, Name: name, Version: version, Executables: executables}, skills, ""
}

func configField(name string, value any) string {
	if !configName.MatchString(name) {
		return fmt.Sprintf("`config.%s` is not a usable name.", name)
	}
	field, ok := value.(map[string]any)
	if !ok {
		return fmt.Sprintf("`config.%s` must be an object.", name)
	}
	typ := "string"
	if t, ok := field["type"].(string); ok {
		typ = t
	}
	if typ != "string" && typ != "number" && typ != "bool" {
		return fmt.Sprintf("`config.%s.type` must be string, number or bool - v1 has no nested configuration.", name)
	}
	var choices []string
	if e, present := field["enum"]; present {
		if !stringArray(e) {
			return fmt.Sprintf("`config.%s.enum` must be an array of strings.", name)
		}
		for _, c := range e.([]any) {
			choices = append(choices, c.(string))
		}
	}
	d, present := field["default"]
	if !present {
		return ""
	}
	fits := false
	switch typ {
	case "string":
		_, fits = d.(string)
	case "number":
		_, fits = d.(json.Number)
	case "bool":
		_, fits = d.(bool)
	}
	if !fits {
		return fmt.Sprintf("`%s` must be a %s.", name, typ)
	}
	if s, ok := d.(string); ok && choices != nil {
		for _, c := range choices {
			if c == s {
				return ""
			}
		}
		return fmt.Sprintf("`%s` must be one of: %s.", name, strings.Join(choices, ", "))
	}
	return ""
}

// text is a string property that is not blank, trimmed.
func text(obj map[string]any, name string) (string, bool) {
	s, ok := obj[name].(string)
	if !ok || strings.TrimSpace(s) == "" {
		return "", false
	}
	return strings.TrimSpace(s), true
}

func stringArray(v any) bool {
	list, ok := v.([]any)
	if !ok {
		return false
	}
	for _, item := range list {
		if _, ok := item.(string); !ok {
			return false
		}
	}
	return true
}

// relativeRefusal: relative, no `..`, no empty segment, no rooted form. A drive letter is refused
// too: it means nothing inside a Linux container.
func relativeRefusal(field, path string) string {
	bad := strings.HasPrefix(path, "/") || strings.HasPrefix(path, "\\") || (len(path) >= 2 && path[1] == ':')
	for _, part := range strings.Split(strings.ReplaceAll(path, "\\", "/"), "/") {
		if part == "" || part == ".." {
			bad = true
		}
	}
	if bad {
		return fmt.Sprintf("`%s` '%s' must be a relative path inside the plugin's directory.", field, path)
	}
	return ""
}

// inside is the full path of rel under folder with every symlink resolved, and whether it stays
// inside folder.
func inside(folder, rel string) (string, bool) {
	base, err := filepath.EvalSymlinks(folder)
	if err != nil {
		return "", false
	}
	base, _ = filepath.Abs(base)
	target := filepath.Join(folder, filepath.FromSlash(strings.ReplaceAll(rel, "\\", "/")))
	resolved, err := filepath.EvalSymlinks(target)
	if err != nil {
		// Missing: the caller says so. It is inside as far as its path goes.
		return target, true
	}
	resolved, _ = filepath.Abs(resolved)
	return resolved, strings.HasPrefix(resolved, base+string(filepath.Separator))
}

// orderedKeys is a JSON object's keys sorted, so a refusal names the same field on every run.
func orderedKeys(m map[string]any) []string {
	keys := make([]string, 0, len(m))
	for k := range m {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	return keys
}
