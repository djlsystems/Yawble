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
