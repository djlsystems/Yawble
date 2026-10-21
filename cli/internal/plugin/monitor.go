package plugin

import (
	_ "embed"
	"fmt"
	"regexp"
	"slices"
	"sort"
	"strings"
	"unicode/utf8"
)

// materialSymbolsText is every Material Symbols Outlined ligature name, one per line: the same file
// the Host embeds (Harness.Host.csproj), written by web/scripts/material-symbol-names.mjs from the
// font package the web app serves. Nothing in it is typed by hand.
//
//go:embed material-symbols.txt
var materialSymbolsText string

// MaterialSymbols is the names in material-symbols.txt, sorted as the file holds them.
var MaterialSymbols = parseSymbols(materialSymbolsText)

var symbolSet = func() map[string]bool {
	set := make(map[string]bool, len(MaterialSymbols))
	for _, name := range MaterialSymbols {
		set[name] = true
	}
	return set
}()

// parseSymbols is MaterialSymbols.Parse: one name per line, a # line a comment.
func parseSymbols(text string) []string {
	var names []string
	for _, line := range strings.Split(text, "\n") {
		line = strings.TrimSpace(line)
		if line != "" && !strings.HasPrefix(line, "#") {
			names = append(names, line)
		}
	}
	return names
}

// IsMaterialSymbol is MaterialSymbols.IsName.
func IsMaterialSymbol(name string) bool { return symbolSet[name] }

// MaxMonitorLabel is PluginMonitorHint.MaxLabel: the longest a label may be as written, in characters.
const MaxMonitorLabel = 120

var (
	monitorActions   = []string{"created", "changed", "deleted"}
	monitorKnownKeys = []string{"icon", "iconBy", "action", "actionField", "label"}
	placeholder      = regexp.MustCompile(`\{([^{}]*)\}`)
)

// eventFieldKind is a declared event field's kind as the Host reads it: a kind that is missing,
// blank or not a string is a string; number and integer are a number.
func eventFieldKind(field map[string]any) (string, bool) {
	kind, ok := text(field, "kind")
	if !ok {
		return "string", true
	}
	switch kind {
	case "string":
		return "string", true
	case "number", "integer":
		return "number", true
	case "boolean", "bool":
		return "boolean", true
	case "list":
		return "list", true
	}
	return kind, false
}

// publishedEvent checks one events.publishes entry after its type: its fields as
// PluginManifest.Parse reads them, then its monitor hint. It answers the refusal, or "".
func publishedEvent(typ string, obj map[string]any, ignored *[]string) string {
	kinds := map[string][]string{}
	if f, present := obj["fields"]; present && f != nil {
		list, ok := f.([]any)
		if !ok {
			return fmt.Sprintf("`events.publishes` `%s`: `fields` must be an array.", typ)
		}
		for _, item := range list {
			field, _ := item.(map[string]any)
			name, ok := text(field, "name")
			if field == nil || !ok || !configName.MatchString(name) {
				return fmt.Sprintf("`events.publishes` `%s`: each field needs a `name`.", typ)
			}
			if name == "source" {
				return fmt.Sprintf("`events.publishes` `%s`: `source` is the platform's own field, stamped from the member; it cannot be declared.", typ)
			}
			kind, ok := eventFieldKind(field)
			if !ok {
				return fmt.Sprintf("`events.publishes` `%s`: field `%s` has kind '%s'; use string, number, boolean or list.", typ, name, kind)
			}
			kinds[name] = append(kinds[name], kind)
		}
	}
	if m, present := obj["monitor"]; present && m != nil {
		return monitorHint(typ, m, kinds, ignored)
	}
	return ""
}

// monitorHint is PluginMonitorHint.Parse: the same checks in the same order, in the same sentences.
func monitorHint(typ string, value any, kinds map[string][]string, ignored *[]string) string {
	at := fmt.Sprintf("`events.publishes` `%s`:", typ)
	monitor, ok := value.(map[string]any)
	if !ok {
		return at + " `monitor` must be an object."
	}
	isKind := func(name string, allowed ...string) bool {
		for _, kind := range kinds[name] {
			if slices.Contains(allowed, kind) {
				return true
			}
		}
		return false
	}
	present := func(parent map[string]any, key string) bool {
		v, has := parent[key]
		return has && v != nil
	}

	icon, ok := monitor["icon"].(string)
	if !ok || strings.TrimSpace(icon) == "" {
		return at + " `monitor.icon` is required: a Material Symbols name, such as inbox."
	}
	if !IsMaterialSymbol(icon) {
		return fmt.Sprintf("%s `monitor.icon` '%s' is not a Material Symbols name.", at, icon)
	}

	if present(monitor, "iconBy") {
		iconBy, ok := monitor["iconBy"].(map[string]any)
		field, fieldOK := iconBy["field"].(string)
		icons, mapOK := iconBy["icons"].(map[string]any)
		if !ok || !fieldOK || !mapOK {
			return at + " `monitor.iconBy` must be an object with a `field` and `icons`, a map of value to icon."
		}
		if !isKind(field, "string") {
			return fmt.Sprintf("%s `monitor.iconBy.field` names '%s', which is not a declared string field.", at, field)
		}
		for _, key := range orderedKeys(icons) {
			name, ok := icons[key].(string)
			if !ok {
				return fmt.Sprintf("%s `monitor.iconBy.icons` '%s' must be a Material Symbols name.", at, key)
			}
			if !IsMaterialSymbol(name) {
				return fmt.Sprintf("%s `monitor.iconBy.icons` '%s': '%s' is not a Material Symbols name.", at, key, name)
			}
		}
	}

	hasAction, hasActionField := present(monitor, "action"), present(monitor, "actionField")
	if hasAction && hasActionField {
		return at + " `monitor` has both `action` and `actionField`; give exactly one."
	}
	if !hasAction && !hasActionField {
		return at + " `monitor` needs `action` (created, changed or deleted) or `actionField` (a declared string field)."
	}
	if hasAction {
		action, ok := monitor["action"].(string)
		if !ok || !slices.Contains(monitorActions, action) {
			return at + " `monitor.action` must be created, changed or deleted."
		}
	} else {
		field, ok := monitor["actionField"].(string)
		if !ok {
			return at + " `monitor.actionField` must name a declared string field."
		}
		if !isKind(field, "string") {
			return fmt.Sprintf("%s `monitor.actionField` names '%s', which is not a declared string field.", at, field)
		}
	}

	if present(monitor, "label") {
		label, ok := monitor["label"].(string)
		if !ok || strings.TrimSpace(label) == "" {
			return at + " `monitor.label` must be text."
		}
		if n := utf8.RuneCountInString(label); n > MaxMonitorLabel {
			return fmt.Sprintf("%s `monitor.label` is %d characters; a label is at most %d.", at, n, MaxMonitorLabel)
		}
		for _, match := range placeholder.FindAllStringSubmatch(label, -1) {
			if !isKind(match[1], "string", "number") {
				return fmt.Sprintf("%s `monitor.label` names {%s}, which is not a declared string or number field.", at, match[1])
			}
		}
	}

	var unknown []string
	for key := range monitor {
		if !slices.Contains(monitorKnownKeys, key) {
			unknown = append(unknown, key)
		}
	}
	sort.Strings(unknown)
	for _, key := range unknown {
		*ignored = append(*ignored, fmt.Sprintf("events.publishes.%s.monitor.%s", typ, key))
	}
	return ""
}
