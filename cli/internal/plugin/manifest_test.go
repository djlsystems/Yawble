package plugin

import (
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"testing"
)

// The samples in the repository are what the docs install; each must pass as it is. The Go
// template names one static binary per processor, both marked executable; the connections sample
// binds a Google slot. An empty version or executables is not checked for that sample.
func TestTheSampleManifestsAreAccepted(t *testing.T) {
	cases := []struct{ sample, version, executables string }{
		{"sample-echo", "0.1.0", "sample-echo"},
		{"sample-echo-go", "", "bin/linux-x64/sample-echo-go,bin/linux-arm64/sample-echo-go,bin/linux-x64/sample-echo-go"},
		{"sample-whoami-go", "", ""},
	}
	for _, c := range cases {
		t.Run(c.sample, func(t *testing.T) {
			raw, err := os.ReadFile(filepath.Join("..", "..", "..", "samples", "plugins", c.sample, "plugin.json"))
			if err != nil {
				t.Fatal(err)
			}
			m, skills, err := Parse(raw)
			if err != nil {
				t.Fatal(err)
			}
			if m.ID != c.sample || len(skills) != 1 ||
				(c.version != "" && m.Version != c.version) ||
				(c.executables != "" && strings.Join(m.Executables, ",") != c.executables) {
				t.Errorf("%+v %v", m, skills)
			}
		})
	}
}

func TestPlatformExecutablesAreMarkedToo(t *testing.T) {
	m, _, err := Parse([]byte(`{"schemaVersion":1,"id":"p","name":"n","description":"d","version":"1","protocol":"harness.member/1",
		"executable":{"path":"run"},"platforms":{"linux-x64":"bin/x64/p","linux-arm64":"bin/arm64/p"}}`))
	if err != nil {
		t.Fatal(err)
	}
	if strings.Join(m.Executables, ",") != "run,bin/arm64/p,bin/x64/p" {
		t.Errorf("%v", m.Executables)
	}
}

func TestRelaxDropsCommentsAndTrailingCommasButNotStrings(t *testing.T) {
	got := string(relax([]byte("{ // c\n \"a\": \"// not, a comment }\", /* x */ \"b\": [1, 2, ],\n}")))
	if got != "{ \n \"a\": \"// not, a comment }\",   \"b\": [1, 2 ]\n}" {
		t.Errorf("%q", got)
	}
}

func TestRelativePaths(t *testing.T) {
	for path, ok := range map[string]bool{"a/b": true, "run": true, "../x": false, "/bin/sh": false, `\x`: false, "a//b": false, "C:x": false, "a/../b": false} {
		if (relativeRefusal("f", path) == "") != ok {
			t.Errorf("%q: %v", path, relativeRefusal("f", path))
		}
	}
}

func TestListSettingsAndRequires(t *testing.T) {
	manifest := func(extra string) []byte {
		return []byte(`{"schemaVersion":1,"id":"p","name":"n","description":"d","version":"1","protocol":"harness.member/1",
			"executable":{"path":"run"}` + extra + `}`)
	}
	for extra, want := range map[string]string{
		`,"config":{"allow":{"type":"list","default":[]}}`:                     "",
		`,"config":{"allow":{"type":"list","enum":["a","b"],"default":["b"]}}`: "",
		`,"config":{"allow":{"type":"list","enum":["a","b"],"default":["c"]}}`: "`allow` holds 'c'; each item must be one of: a, b.",
		`,"config":{"allow":{"type":"list","default":"a"}}`:                    "`allow` must be a list of strings.",
		`,"config":{"allow":{"type":"map"}}`:                                   "`config.allow.type` must be string, number, bool or list - v1 has no nested configuration.",
		`,"requires":["dotnet","node","python3"]`:                              "",
		`,"requires":null`:     "",
		`,"requires":["ruby"]`: "`requires` names 'ruby', which is not a runtime this Host knows (dotnet, node, python3).",
		`,"requires":"dotnet"`: "`requires` must be an array of runtime names.",
	} {
		_, _, err := Parse(manifest(extra))
		got := ""
		if err != nil {
			got = err.Error()
		}
		if (want == "" && got != "") || (want != "" && !strings.Contains(got, want)) {
			t.Errorf("%s: got %q, want %q", extra, got, want)
		}
	}
}

func TestConnectionSlots(t *testing.T) {
	manifest := func(extra string) []byte {
		return []byte(`{"schemaVersion":1,"id":"p","name":"n","description":"d","version":"1","protocol":"harness.member/1",
			"executable":{"path":"run"}` + extra + `}`)
	}
	for extra, want := range map[string]string{
		`,"connections":{"mail":{"providers":["google","microsoft","custom"],"scopes":{"google":["https://mail.google.com/"]},"required":true}}`: "",
		`,"connections":{"mail":{"providers":["custom-acme"],"scopes":["read"],"description":"x"}}`:                                              "",
		`,"connections":null`: "",
		`,"connections":[]`:   "`connections` must be an object of slot name to slot.",
		`,"connections":{"1mail":{"providers":["google"]}}`:                                "`connections.1mail` is not a usable slot name.",
		`,"connections":{"mail":{"providers":[]}}`:                                         "`connections.mail.providers` must be a non-empty list",
		`,"connections":{"mail":{"providers":["yahoo"]}}`:                                  "names 'yahoo', which is not a provider",
		`,"connections":{"mail":{"providers":["google"],"scopes":{"microsoft":[]}}}`:       "has scopes for 'microsoft', which the slot's providers do not name.",
		`,"connections":{"mail":{"providers":["google"],"scopes":"email"}}`:                "`connections.mail.scopes` must be a list of scopes",
		`,"connections":{"mail":{"providers":["google"],"required":"yes"}}`:                "`connections.mail.required` must be true or false.",
		`,"connections":{"mail":{"providers":["google"],"required":null}}`:                 "`connections.mail.required` must be true or false.",
		`,"connections":{"mail":{"providers":["google"],"scopes":[""]}}`:                   "`connections.mail.scopes` must hold scope strings",
		`,"connections":{"mail":{"providers":["google"],"scopes":["  "]}}`:                 "`connections.mail.scopes` must hold scope strings",
		`,"connections":{"mail":{"providers":["google"],"scopes":["a b"]}}`:                "`connections.mail.scopes` must hold scope strings",
		`,"connections":{"mail":{"providers":["google"],"scopes":{"google":[" "]}}}`:       "`connections.mail.scopes.google` must be a list of scope strings",
		`,"connections":{"mail":{"providers":["custom-` + strings.Repeat("a", 34) + `"]}}`: "which is not a provider",
		`,"connections":{"mail":{"providers":["custom-` + strings.Repeat("a", 33) + `"]}}`: "",
		`,"connections":{"mail":{"providers":["imap"],"required":true}}`:                   "",
		`,"connections":{"mail":{"providers":["google","imap"],"scopes":["read"]}}`:        "",
		`,"connections":{"mail":{"providers":["imap"],"scopes":{"imap":["INBOX"]}}}`:       "scopes do not apply to an imap connection",
	} {
		_, _, err := Parse(manifest(extra))
		got := ""
		if err != nil {
			got = err.Error()
		}
		if (want == "" && got != "") || (want != "" && !strings.Contains(got, want)) {
			t.Errorf("%s: got %q, want %q", extra, got, want)
		}
	}
}

func readsManifest(extra string) []byte {
	return []byte(`{"schemaVersion":1,"id":"p","name":"n","description":"d","version":"1","protocol":"harness.member/1",
		"executable":{"path":"run"}` + extra + `}`)
}

// The same cases and the same sentences as the Host's PluginSiteReadsTests.
func TestReadsRefusals(t *testing.T) {
	many := make([]string, 33)
	for i := range many {
		many[i] = `{"site":"board","collection":"c` + strconv.Itoa(i) + `"}`
	}
	for extra, want := range map[string]string{
		`,"reads":[{"site":"board","collection":"items"},{"site":"board","collection":"notes"}]`: "",
		`,"reads":{"site":"board","collection":"items"}`:                                         "`reads` must be a list of { site, collection }.",
		`,"reads":["board/items"]`:                                                               "`reads[0]` must be an object with `site` and `collection`.",
		`,"reads":[{"site":"board"}]`:                                                            "`reads[0]` must be an object with `site` and `collection`.",
		`,"reads":[{"site":"Board","collection":"items"}]`:                                       "`reads[0].site`: \"Board\" is not a valid site name. Use 1-63 lower-case letters, digits and hyphens, starting with a letter or digit.",
		`,"reads":[{"site":"board","collection":"items-"}]`:                                      "`reads[0].collection`: \"items-\" is not a valid collection name. Use 1-63 lower-case letters, digits and hyphens, starting with a letter or digit.",
		`,"reads":[{"site":"board","collection":"items","team":"beta"}]`:                         "`reads[0]` names a team; a plugin reads only its own team's sites.",
		`,"reads":[{"site":"board","collection":"items"},{"site":"board","collection":"items"}]`: "`reads[1]` repeats board/items.",
		`,"reads":[` + strings.Join(many, ",") + `]`:                                             "`reads` declares 33 collections; a plugin reads at most 32.",
		`,"reads":[{"site":"` + strings.Repeat("a", 64) + `","collection":"items"}]`:             "`reads[0].site`: \"" + strings.Repeat("a", 64) + "\" is not a valid site name. Use 1-63 lower-case letters, digits and hyphens, starting with a letter or digit.",
		`,"reads":[{"site":"` + strings.Repeat("a", 63) + `","collection":"items"}]`:             "",
	} {
		_, _, err := Parse(readsManifest(extra))
		got := ""
		if err != nil {
			got = err.Error()
		}
		if got != want {
			t.Errorf("%s: got %q, want %q", extra, got, want)
		}
	}
}

func TestReadsFirstFaultWins(t *testing.T) {
	_, _, err := Parse(readsManifest(`,"reads":[{"site":"Bad","collection":"items"},{"site":"board","collection":"items","team":"beta"}]`))
	if err == nil || !strings.HasPrefix(err.Error(), "`reads[0].site`:") {
		t.Errorf("%v", err)
	}
}

func TestReadsAbsentOrNull(t *testing.T) {
	for _, extra := range []string{"", `,"reads":null`, `,"reads":[]`} {
		if _, _, err := Parse(readsManifest(extra)); err != nil {
			t.Errorf("%s: %v", extra, err)
		}
	}
}
