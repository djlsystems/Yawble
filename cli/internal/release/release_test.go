package release_test

import (
	"archive/tar"
	"archive/zip"
	"bytes"
	"compress/gzip"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/release"
)

// fakeRelease serves what GitHub does: the latest-release JSON, the archive for one target and
// checksums.txt. It answers the binary's bytes as `content`.
func fakeRelease(t *testing.T, tag, goos, goarch string, content []byte, corruptChecksum bool) *httptest.Server {
	t.Helper()
	bare := strings.TrimPrefix(tag, "v")
	var archive bytes.Buffer
	name := "yawble_" + bare + "_" + goos + "_" + goarch
	if goos == "windows" {
		name += ".zip"
		zw := zip.NewWriter(&archive)
		f, _ := zw.Create("yawble.exe")
		_, _ = f.Write(content)
		_ = zw.Close()
	} else {
		name += ".tar.gz"
		gz := gzip.NewWriter(&archive)
		tw := tar.NewWriter(gz)
		_ = tw.WriteHeader(&tar.Header{Name: "yawble", Mode: 0o755, Size: int64(len(content))})
		_, _ = tw.Write(content)
		_ = tw.Close()
		_ = gz.Close()
	}
	sum := sha256.Sum256(archive.Bytes())
	digest := hex.EncodeToString(sum[:])
	if corruptChecksum {
		digest = strings.Repeat("0", 64)
	}
	checksums := digest + "  " + name + "\n"
	mux := http.NewServeMux()
	var server *httptest.Server
	mux.HandleFunc("/repos/djlsystems/Yawble/releases/latest", func(w http.ResponseWriter, r *http.Request) {
		_, _ = w.Write([]byte(`{"tag_name":"` + tag + `","assets":[{"name":"` + name + `","browser_download_url":"` + server.URL + `/dl/` + name + `"},{"name":"checksums.txt","browser_download_url":"` + server.URL + `/dl/checksums.txt"}]}`))
	})
	mux.HandleFunc("/dl/"+name, func(w http.ResponseWriter, r *http.Request) { _, _ = w.Write(archive.Bytes()) })
	mux.HandleFunc("/dl/checksums.txt", func(w http.ResponseWriter, r *http.Request) { _, _ = w.Write([]byte(checksums)) })
	server = httptest.NewServer(mux)
	t.Cleanup(server.Close)
	return server
}

func TestLatestReadsTheTagAndTheTwoAssetsForThisTarget(t *testing.T) {
	server := fakeRelease(t, "v0.2.0", "linux", "amd64", []byte("bin"), false)
	rel, err := release.Latest(context.Background(), server.Client(), server.URL, "", "linux", "amd64")
	if err != nil {
		t.Fatal(err)
	}
	if rel.Tag != "v0.2.0" || !strings.HasSuffix(rel.AssetURL, "yawble_0.2.0_linux_amd64.tar.gz") || !strings.HasSuffix(rel.ChecksumsURL, "checksums.txt") {
		t.Errorf("%+v", rel)
	}
	if _, err := release.Latest(context.Background(), server.Client(), server.URL, "", "plan9", "mips"); err == nil || !strings.Contains(err.Error(), "plan9/mips") {
		t.Errorf("a target with no asset: %v", err)
	}
}

func TestInstallReplacesTheBinaryAfterVerifyingTheChecksum(t *testing.T) {
	for _, goos := range []string{"linux", "windows"} {
		server := fakeRelease(t, "v0.2.0", goos, "amd64", []byte("new binary "+goos), false)
		dir := t.TempDir()
		target := filepath.Join(dir, "yawble")
		if goos == "windows" {
			target += ".exe"
		}
		if err := os.WriteFile(target, []byte("old"), 0o755); err != nil {
			t.Fatal(err)
		}
		rel, _ := release.Latest(context.Background(), server.Client(), server.URL, "", goos, "amd64")
		if err := release.Install(context.Background(), server.Client(), rel, "", target, goos); err != nil {
			t.Fatalf("%s: %v", goos, err)
		}
		got, _ := os.ReadFile(target)
		if string(got) != "new binary "+goos {
			t.Errorf("%s: binary is %q", goos, got)
		}
	}
}

// Review Focus 2: a checksum that does not match leaves the binary alone.
func TestAChecksumMismatchReplacesNothing(t *testing.T) {
	server := fakeRelease(t, "v0.2.0", "linux", "amd64", []byte("evil"), true)
	dir := t.TempDir()
	target := filepath.Join(dir, "yawble")
	_ = os.WriteFile(target, []byte("old"), 0o755)
	rel, _ := release.Latest(context.Background(), server.Client(), server.URL, "", "linux", "amd64")
	err := release.Install(context.Background(), server.Client(), rel, "", target, "linux")
	if err == nil || !strings.Contains(err.Error(), "checksum") {
		t.Fatalf("err %v", err)
	}
	got, _ := os.ReadFile(target)
	if string(got) != "old" {
		t.Errorf("binary changed to %q", got)
	}
	entries, _ := os.ReadDir(dir)
	if len(entries) != 1 {
		t.Errorf("temp files left: %v", entries)
	}
}
