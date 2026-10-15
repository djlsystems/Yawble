package plugin

import (
	"encoding/json"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"testing"
)

// monitorCases is tests/Fixtures/PluginMonitorHints/cases.json, which PluginMonitorHintTests reads
// too: the Host and the CLI accept and refuse the same hints, in the same sentences.
type monitorCases struct {
	Fields   json.RawMessage   `json:"fields"`
	Accepted []json.RawMessage `json:"accepted"`
	Refused  []struct {
		Monitor json.RawMessage `json:"monitor"`
		Says    string          `json:"says"`
	} `json:"refused"`
}

func readMonitorCases(t *testing.T) monitorCases {
	t.Helper()
	raw, err := os.ReadFile(filepath.Join("..", "..", "..", "tests", "Fixtures", "PluginMonitorHints", "cases.json"))
	if err != nil {
		t.Fatal(err)
	}
	var cases monitorCases
	if err := json.Unmarshal(raw, &cases); err != nil {
		t.Fatal(err)
	}
	if len(cases.Accepted) == 0 || len(cases.Refused) == 0 {
		t.Fatal("cases.json holds no cases")
	}
	return cases
}

func withEvents(publishes string) []byte {
	return []byte(`{"schemaVersion":1,"id":"fx","name":"n","description":"d","version":"0.1.0","protocol":"harness.member/1",
		"executable":{"path":"bin/run"},"events":{"publishes":` + publishes + `}}`)
}

func withMonitor(fields, monitor json.RawMessage) []byte {
	return withEvents(`[{"type":"saved","fields":` + string(fields) + `,"monitor":` + string(monitor) + `}]`)
}

func TestEachSharedValidHintIsAccepted(t *testing.T) {
	cases := readMonitorCases(t)
	for _, monitor := range cases.Accepted {
		if _, _, err := Parse(withMonitor(cases.Fields, monitor)); err != nil {
			t.Errorf("%s: %v", monitor, err)
		}
	}
}

func TestEachSharedFaultIsRefusedNamingTheEventTypeAndTheField(t *testing.T) {
	cases := readMonitorCases(t)
	for _, c := range cases.Refused {
		_, _, err := Parse(withMonitor(cases.Fields, c.Monitor))
		want := "`events.publishes` `saved`: " + c.Says
		if err == nil || err.Error() != want {
			t.Errorf("%s:\n got %v\nwant %s", c.Monitor, err, want)
		}
	}
}

func TestAnUnknownKeyInsideMonitorIsIgnoredByNameAndNeverRefused(t *testing.T) {
	m, _, err := Parse(withEvents(`[{"type":"saved","monitor":{"pulse":true,"icon":"inbox","action":"created","colour":"teal"}}]`))
	if err != nil {
		t.Fatal(err)
	}
	if strings.Join(m.Ignored, ",") != "events.publishes.saved.monitor.colour,events.publishes.saved.monitor.pulse" {
		t.Errorf("%v", m.Ignored)
	}
}

func TestAManifestWithoutMonitorReadsAsBefore(t *testing.T) {
	m, _, err := Parse(withEvents(`[{"type":"tick","highVolume":true},{"type":"done","fields":[{"name":"n","kind":"number"},{"name":"k","kind":5}]}]`))
	if err != nil || len(m.Ignored) != 0 {
		t.Fatalf("%+v %v", m, err)
	}
}

// The event declarations the hint reads are refused as PluginEventsTests refuses them.
func TestABadEventDeclarationIsRefusedAsTheHostRefusesIt(t *testing.T) {
	for publishes, want := range map[string]string{
		`[{"type":"tick","fields":[{"name":"source"}]}]`:           "`events.publishes` `tick`: `source` is the platform's own field, stamped from the member; it cannot be declared.",
		`[{"type":"tick","fields":[{"name":"n","kind":"float"}]}]`: "`events.publishes` `tick`: field `n` has kind 'float'; use string, number, boolean or list.",
		`[{"type":"tick","fields":{}}]`:                            "`events.publishes` `tick`: `fields` must be an array.",
		`[{"type":"tick","fields":[{"kind":"string"}]}]`:           "`events.publishes` `tick`: each field needs a `name`.",
		`[{"type":"tick"},{"type":"tick"}]`:                        "`events.publishes` declares `tick` twice.",
	} {
		if _, _, err := Parse(withEvents(publishes)); err == nil || err.Error() != want {
			t.Errorf("%s:\n got %v\nwant %s", publishes, err, want)
		}
	}
}

// The names the hint is checked against are the generated file's: the whole font, sorted, unique,
// lower-case ligature names. MaterialSymbolNamesTests pins the same file to the font package.
func TestTheMaterialSymbolsListIsTheGeneratedFile(t *testing.T) {
	if len(MaterialSymbols) < 3000 {
		t.Fatalf("%d names is not the whole font", len(MaterialSymbols))
	}
	if !sort.StringsAreSorted(MaterialSymbols) {
		t.Error("material-symbols.txt is not sorted")
	}
	for i, name := range MaterialSymbols {
		if i > 0 && MaterialSymbols[i-1] == name {
			t.Errorf("%s twice", name)
		}
		if strings.Trim(name, "abcdefghijklmnopqrstuvwxyz0123456789_") != "" {
			t.Errorf("%q is not a ligature name", name)
		}
	}
	for _, name := range []string{"inbox", "exposure_neg_1", "crop_16_9"} {
		if !IsMaterialSymbol(name) {
			t.Errorf("%s is missing", name)
		}
	}
	if IsMaterialSymbol("Inbox") || IsMaterialSymbol("exposure_neg1") {
		t.Error("a name the font does not form is accepted")
	}
}
