package plugin

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// The sample in the repository is what the docs install; it must pass as it is.
func TestTheSampleManifestIsAccepted(t *testing.T) {
	raw, err := os.ReadFile(filepath.Join("..", "..", "..", "samples", "plugins", "sample-echo", "plugin.json"))
	if err != nil {
		t.Fatal(err)
	}
	m, skills, err := Parse(raw)
	if err != nil {
		t.Fatal(err)
	}
	if m.ID != "sample-echo" || m.Version != "0.1.0" || len(m.Executables) != 1 || m.Executables[0] != "sample-echo" || len(skills) != 1 {
		t.Errorf("%+v %v", m, skills)
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

// The Go template names one static binary per processor; both are marked executable.
func TestTheGoSampleManifestIsAccepted(t *testing.T) {
	raw, err := os.ReadFile(filepath.Join("..", "..", "..", "samples", "plugins", "sample-echo-go", "plugin.json"))
	if err != nil {
		t.Fatal(err)
	}
	m, skills, err := Parse(raw)
	if err != nil {
		t.Fatal(err)
	}
	if m.ID != "sample-echo-go" || strings.Join(m.Executables, ",") != "bin/linux-x64/sample-echo-go,bin/linux-arm64/sample-echo-go,bin/linux-x64/sample-echo-go" || len(skills) != 1 {
		t.Errorf("%+v %v", m, skills)
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
		`,"requires":["ruby"]`: "`requires` names 'ruby', which is not a runtime this Host provides (it provides dotnet, node, python3); ship anything else inside the plugin's folder.",
		`,"requires":"dotnet"`: "`requires` must be an array of runtime names: dotnet, node, python3.",
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
