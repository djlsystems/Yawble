package backup_test

import (
	"archive/tar"
	"bytes"
	"errors"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
	"time"

	"github.com/djlsystems/yawble/cli/internal/backup"
)

// volumeTar is what `tar -C /data -cf - .` prints for a small volume.
func volumeTar(t *testing.T) []byte {
	t.Helper()
	var b bytes.Buffer
	tw := tar.NewWriter(&b)
	add := func(h *tar.Header, body string) {
		h.Size = int64(len(body))
		if err := tw.WriteHeader(h); err != nil {
			t.Fatal(err)
		}
		if _, err := io.WriteString(tw, body); err != nil {
			t.Fatal(err)
		}
	}
	add(&tar.Header{Name: "./", Typeflag: tar.TypeDir, Mode: 0o750, Uid: 10001, Gid: 10002}, "")
	add(&tar.Header{Name: "./messages.db", Typeflag: tar.TypeReg, Mode: 0o600, Uid: 10001, Gid: 10001}, "SQLite format 3")
	add(&tar.Header{Name: "./agent-home/.claude/.credentials.json", Typeflag: tar.TypeReg, Mode: 0o600, Uid: 10002, Gid: 10002}, `{"token":"x"}`)
	add(&tar.Header{Name: "./teams/alpha/doc.md", Typeflag: tar.TypeReg, Mode: 0o660, Uid: 10002, Gid: 10002}, "# hello")
	add(&tar.Header{Name: "./teams/alpha/same.md", Typeflag: tar.TypeLink, Linkname: "./teams/alpha/doc.md"}, "")
	add(&tar.Header{Name: "./bin/tool", Typeflag: tar.TypeSymlink, Linkname: "/usr/bin/true"}, "")
	if err := tw.Close(); err != nil {
		t.Fatal(err)
	}
	// GNU tar pads to whole 10 KiB records after the end marker.
	b.Write(make([]byte, 4096))
	return b.Bytes()
}

func write(t *testing.T, dir string, m backup.Manifest) string {
	t.Helper()
	path := filepath.Join(dir, "b.tar.gz")
	data := volumeTar(t)
	if _, err := backup.Write(path, m, func(w io.Writer) error { _, err := w.Write(data); return err }); err != nil {
		t.Fatal(err)
	}
	return path
}

func TestWriteThenReadManifestAndExtractRoundTrips(t *testing.T) {
	at := time.Date(2026, 9, 28, 10, 15, 0, 0, time.UTC)
	path := write(t, t.TempDir(), backup.Manifest{Image: "ghcr.io/djlsystems/yawble:2026.09.24.1", ImageVersion: "2026.09.24.1", CreatedAt: at, SchemaSteps: []string{"a", "b"}})

	m, err := backup.ReadManifest(path)
	if err != nil {
		t.Fatal(err)
	}
	if m.Format != backup.FormatVersion || m.ImageVersion != "2026.09.24.1" || len(m.SchemaSteps) != 2 || !m.CreatedAt.Equal(at) {
		t.Errorf("manifest %+v", m)
	}
	if m.Bytes.Files != 3 || m.Bytes.Data != int64(len("SQLite format 3")+len(`{"token":"x"}`)+len("# hello")) || m.Bytes.Directories != 1 || m.Bytes.Links != 2 {
		t.Errorf("counts %+v", m.Bytes)
	}

	var plain bytes.Buffer
	if _, err := backup.Extract(path, &plain); err != nil {
		t.Fatal(err)
	}
	tr := tar.NewReader(&plain)
	var names []string
	for {
		h, err := tr.Next()
		if errors.Is(err, io.EOF) {
			break
		}
		if err != nil {
			t.Fatal(err)
		}
		names = append(names, h.Name)
		if h.Name == "./teams/alpha/same.md" && h.Linkname != "./teams/alpha/doc.md" {
			t.Errorf("hard link target %q", h.Linkname)
		}
		if h.Name == "./messages.db" && (h.Uid != 10001 || h.Mode != 0o600) {
			t.Errorf("owner or mode lost: %+v", h)
		}
	}
	want := "./ ./messages.db ./agent-home/.claude/.credentials.json ./teams/alpha/doc.md ./teams/alpha/same.md ./bin/tool"
	if strings.Join(names, " ") != want {
		t.Errorf("extracted %v", names)
	}
}

// Readable with standard tools: the system tar lists the manifest first and the data under data/.
func TestTheArchiveIsReadableWithTar(t *testing.T) {
	tarPath, err := exec.LookPath("tar")
	if err != nil {
		t.Skip("no tar on this machine")
	}
	path := write(t, t.TempDir(), backup.Manifest{})
	out, err := exec.Command(tarPath, "-tzf", path).CombinedOutput()
	if err != nil {
		t.Fatalf("%v: %s", err, out)
	}
	lines := strings.Split(strings.TrimSpace(string(out)), "\n")
	if lines[0] != "manifest.json" || lines[1] != "data/" || !strings.Contains(string(out), "data/teams/alpha/doc.md") {
		t.Errorf("tar -tzf:\n%s", out)
	}
}

func TestWriteIsOwnerOnlyAndRefusesAnExistingFile(t *testing.T) {
	dir := t.TempDir()
	path := write(t, dir, backup.Manifest{})
	if runtime.GOOS != "windows" {
		info, err := os.Stat(path)
		if err != nil {
			t.Fatal(err)
		}
		if info.Mode().Perm() != 0o600 {
			t.Errorf("mode %04o", info.Mode().Perm())
		}
	}
	if _, err := backup.Write(path, backup.Manifest{}, func(io.Writer) error { return nil }); err == nil || !strings.Contains(err.Error(), "already exists") {
		t.Errorf("err %v", err)
	}
	left, _ := os.ReadDir(dir)
	if len(left) != 1 {
		t.Errorf("temporary files left: %v", left)
	}
}

// A helper that fails leaves no archive, and no partial file that looks like one.
func TestAFailedHelperLeavesNoFile(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "b.tar.gz")
	_, err := backup.Write(path, backup.Manifest{}, func(w io.Writer) error {
		_, _ = w.Write(volumeTar(t)[:1024])
		return errors.New("podman run: exit 2")
	})
	if err == nil {
		t.Fatal("want an error")
	}
	if left, _ := os.ReadDir(dir); len(left) != 0 {
		t.Errorf("left %v", left)
	}
}

func TestReadManifestRefusesWhatIsNotABackup(t *testing.T) {
	path := filepath.Join(t.TempDir(), "x.tar.gz")
	_ = os.WriteFile(path, []byte("not gzip"), 0o600)
	if _, err := backup.ReadManifest(path); !errors.Is(err, backup.ErrNotABackup) {
		t.Errorf("err %v", err)
	}
}

func TestTarArgsLeaveOutOnlyWhatAStartBringsBack(t *testing.T) {
	args := strings.Join(backup.TarArgs("/data", false), " ")
	for _, want := range []string{"--exclude=./npm-cache", "--exclude=./pip-cache", "--exclude=./go-cache", "--exclude=./nuget", "--exclude=./ms-playwright",
		"--exclude=./npm-global/lib/node_modules/@anthropic-ai/claude-code", "--exclude=./npm-global/bin/claude", "--exclude=./agent-home/.grok/downloads"} {
		if !strings.Contains(args, want+" ") {
			t.Errorf("missing %s in %s", want, args)
		}
	}
	// Not reinstalled at start, so kept: agents' own binaries, other npm globals, logins.
	for _, kept := range []string{"--exclude=./bin", "--exclude=./npm-global ", "--exclude=./agent-home ", "--exclude=./agent-home/.claude", "--exclude=./agent-home/.grok "} {
		if strings.Contains(args+" ", kept) {
			t.Errorf("%s must be kept: %s", kept, args)
		}
	}
	if full := strings.Join(backup.TarArgs("/data", true), " "); strings.Contains(full, "--exclude") {
		t.Errorf("--full excludes: %s", full)
	}
}

func TestNewerComparesYawbleVersions(t *testing.T) {
	for _, c := range []struct {
		a, b              string
		newer, comparable bool
	}{
		{"2026.09.30.1", "2026.09.24.1", true, true},
		{"2026.09.24.10", "v2026.09.24.9", true, true},
		{"2026.09.24.1", "2026.09.24.1", false, true},
		{"2026.09.01.1", "2026.09.24.1", false, true},
		{"dev", "2026.09.24.1", false, false},
		{"2026.09.24.1", "", false, false},
	} {
		newer, comparable := backup.Newer(c.a, c.b)
		if newer != c.newer || comparable != c.comparable {
			t.Errorf("Newer(%q, %q) = %v, %v", c.a, c.b, newer, comparable)
		}
	}
	if v := backup.ImageVersion("ghcr.io/djlsystems/yawble:2026.09.24.1"); v != "2026.09.24.1" {
		t.Errorf("ImageVersion %q", v)
	}
	if v := backup.ImageVersion("localhost:5000/yawble"); v != "" {
		t.Errorf("ImageVersion %q", v)
	}
}

// The real GNU tar with the exact arguments the helper runs: the excludes match only at the root
// of the volume, keep logins and agents' own programs, and the extract side puts the tree back.
func TestGNUTarAppliesTheExcludesAndRestoresTheTree(t *testing.T) {
	tarPath, err := exec.LookPath("tar")
	if err != nil {
		t.Skip("no tar on this machine")
	}
	if out, _ := exec.Command(tarPath, "--version").Output(); !strings.Contains(string(out), "GNU tar") {
		t.Skip("the image's tar is GNU tar; this machine's is not")
	}
	src := t.TempDir()
	files := []string{
		"messages.db", "npm-cache/_cacache/x", "pip-cache/x", "go-cache/x", "nuget/pkg/x", "ms-playwright/chromium_headless_shell-1/x",
		"npm-global/bin/claude", "npm-global/lib/node_modules/@anthropic-ai/claude-code/cli.js",
		"npm-global/lib/node_modules/@anthropic-ai/.claude-code-Ab12/cli.js", "npm-global/lib/node_modules/@openai/codex/bin/codex",
		"npm-global/lib/node_modules/@github/copilot/index.js", "npm-global/bin/typescript", "npm-global/lib/node_modules/typescript/x",
		"bin/mytool", "agent-home/.claude/.credentials.json", "agent-home/.claude.json", "agent-home/.grok/auth.json",
		"agent-home/.grok/config.toml", "agent-home/.grok/bin/grok", "agent-home/.grok/downloads/grok-linux-x64",
		"teams/alpha/repos/app/npm-cache/kept", "go/bin/gopls",
	}
	for _, f := range files {
		p := filepath.Join(src, f)
		if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(p, []byte(f), 0o644); err != nil {
			t.Fatal(err)
		}
	}
	cmd := exec.Command(tarPath, backup.TarArgs(src, false)...)
	var stream bytes.Buffer
	cmd.Stdout = &stream
	if err := cmd.Run(); err != nil {
		t.Fatal(err)
	}
	path := filepath.Join(t.TempDir(), "b.tar.gz")
	if _, err := backup.Write(path, backup.Manifest{}, func(w io.Writer) error { _, err := w.Write(stream.Bytes()); return err }); err != nil {
		t.Fatal(err)
	}
	var plain bytes.Buffer
	if _, err := backup.Extract(path, &plain); err != nil {
		t.Fatal(err)
	}
	dst := t.TempDir()
	x := exec.Command(tarPath, "-C", dst, "--numeric-owner", "-xpf", "-")
	x.Stdin = &plain
	if out, err := x.CombinedOutput(); err != nil {
		t.Fatalf("%v: %s", err, out)
	}
	kept := map[string]bool{
		"messages.db": true, "npm-global/bin/typescript": true, "npm-global/lib/node_modules/typescript/x": true, "bin/mytool": true,
		"agent-home/.claude/.credentials.json": true, "agent-home/.claude.json": true, "agent-home/.grok/auth.json": true,
		"agent-home/.grok/config.toml": true, "teams/alpha/repos/app/npm-cache/kept": true, "go/bin/gopls": true,
	}
	for _, f := range files {
		_, err := os.Stat(filepath.Join(dst, f))
		if got := err == nil; got != kept[f] {
			t.Errorf("%s: restored %v, want %v", f, got, kept[f])
		}
	}
}
