package release_test

import (
	"archive/tar"
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
	"sync"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/release"
)

const token = "ghp_secretvalue123"

// privateRelease is GitHub for a PRIVATE repository: the API answers only with the token, the
// public download URLs answer 404, and an asset is read at its API URL with
// Accept: application/octet-stream, which redirects to a storage host on another origin. That
// host records every Authorization header it is sent, which must be none.
type privateRelease struct {
	api, storage *httptest.Server
	mu           sync.Mutex
	leaked       []string
}

func newPrivateRelease(t *testing.T, tag string, content []byte) *privateRelease {
	t.Helper()
	p := &privateRelease{}
	name := "yawble_" + strings.TrimPrefix(tag, "v") + "_linux_amd64.tar.gz"
	var archive bytes.Buffer
	gz := gzip.NewWriter(&archive)
	tw := tar.NewWriter(gz)
	_ = tw.WriteHeader(&tar.Header{Name: "yawble", Mode: 0o755, Size: int64(len(content))})
	_, _ = tw.Write(content)
	_ = tw.Close()
	_ = gz.Close()
	sum := sha256.Sum256(archive.Bytes())
	checksums := hex.EncodeToString(sum[:]) + "  " + name + "\n"

	storage := http.NewServeMux()
	storage.HandleFunc("/blob/", func(w http.ResponseWriter, r *http.Request) {
		if a := r.Header.Get("Authorization"); a != "" {
			p.mu.Lock()
			p.leaked = append(p.leaked, a)
			p.mu.Unlock()
		}
		switch strings.TrimPrefix(r.URL.Path, "/blob/") {
		case "1":
			_, _ = w.Write(archive.Bytes())
		case "2":
			_, _ = w.Write([]byte(checksums))
		default:
			http.NotFound(w, r)
		}
	})
	p.storage = httptest.NewServer(storage)
	t.Cleanup(p.storage.Close)

	api := http.NewServeMux()
	authorized := func(r *http.Request) bool { return r.Header.Get("Authorization") == "Bearer "+token }
	api.HandleFunc("/repos/djlsystems/Yawble/releases", func(w http.ResponseWriter, r *http.Request) {
		if !authorized(r) {
			http.NotFound(w, r) // what GitHub answers for a private repository without a token
			return
		}
		base := p.api.URL + "/repos/djlsystems/Yawble/releases/assets/"
		_, _ = w.Write([]byte(`[{"tag_name":"` + tag + `","assets":[` +
			`{"name":"` + name + `","url":"` + base + `1","browser_download_url":"` + p.api.URL + `/public/` + name + `"},` +
			`{"name":"checksums.txt","url":"` + base + `2","browser_download_url":"` + p.api.URL + `/public/checksums.txt"}]}]`))
	})
	api.HandleFunc("/repos/djlsystems/Yawble/releases/assets/", func(w http.ResponseWriter, r *http.Request) {
		if !authorized(r) || r.Header.Get("Accept") != "application/octet-stream" {
			http.NotFound(w, r)
			return
		}
		id := strings.TrimPrefix(r.URL.Path, "/repos/djlsystems/Yawble/releases/assets/")
		http.Redirect(w, r, p.storage.URL+"/blob/"+id, http.StatusFound)
	})
	api.HandleFunc("/public/", http.NotFound)
	p.api = httptest.NewServer(api)
	t.Cleanup(p.api.Close)
	return p
}

func TestWithATokenAPrivateReleaseIsReadThroughTheAPIAndTheTokenStaysOffTheStorageHost(t *testing.T) {
	p := newPrivateRelease(t, "v0.3.0", []byte("private yawble"))
	ctx := context.Background()
	rel, err := release.Latest(ctx, http.DefaultClient, p.api.URL, token, "linux", "amd64")
	if err != nil {
		t.Fatal(err)
	}
	target := filepath.Join(t.TempDir(), "yawble")
	_ = os.WriteFile(target, []byte("old"), 0o755)
	if err := release.Install(ctx, http.DefaultClient, rel, token, target, "linux"); err != nil {
		t.Fatal(err)
	}
	if got, _ := os.ReadFile(target); string(got) != "private yawble" {
		t.Errorf("binary %q", got)
	}
	if len(p.leaked) != 0 {
		t.Errorf("the storage host was sent Authorization %d time(s)", len(p.leaked))
	}
}

func TestWithoutATokenAPrivateReleaseIsRefusedNamingTheToken(t *testing.T) {
	p := newPrivateRelease(t, "v0.3.0", []byte("x"))
	_, err := release.Latest(context.Background(), http.DefaultClient, p.api.URL, "", "linux", "amd64")
	if err == nil || !strings.Contains(err.Error(), "GH_TOKEN") {
		t.Fatalf("err %v", err)
	}
}

// With no token nothing changes: the public download URLs, and no Authorization anywhere.
func TestWithoutATokenNoAuthorizationIsSent(t *testing.T) {
	var sent []string
	server := fakeRelease(t, "v0.2.0", "linux", "amd64", []byte("bin"), false)
	client := &http.Client{Transport: roundTrip(func(r *http.Request) (*http.Response, error) {
		if a := r.Header.Get("Authorization"); a != "" {
			sent = append(sent, r.URL.String())
		}
		return http.DefaultTransport.RoundTrip(r)
	})}
	rel, err := release.Latest(context.Background(), client, server.URL, "", "linux", "amd64")
	if err != nil {
		t.Fatal(err)
	}
	target := filepath.Join(t.TempDir(), "yawble")
	if err := release.Install(context.Background(), client, rel, "", target, "linux"); err != nil {
		t.Fatal(err)
	}
	if len(sent) != 0 {
		t.Errorf("Authorization sent to %q", sent)
	}
}

type roundTrip func(*http.Request) (*http.Response, error)

func (f roundTrip) RoundTrip(r *http.Request) (*http.Response, error) { return f(r) }
