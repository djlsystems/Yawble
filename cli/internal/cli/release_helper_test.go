package cli_test

import (
	"archive/tar"
	"bytes"
	"compress/gzip"
	"crypto/sha256"
	"encoding/hex"
	"net/http"
	"net/http/httptest"
	"runtime"
	"strings"
	"testing"
	"time"
)

// fakeReleaseServer serves a linux/<this arch> release the way GitHub does. The cli tests set
// Deps.GOOS to linux, so the tarball form is what they exercise.
func fakeReleaseServer(t *testing.T, tag string, content []byte) *httptest.Server {
	t.Helper()
	return slowReleaseServer(t, tag, content, 0)
}

// slowReleaseServer is the same with a pause before each download body, so a client timeout
// that is too short shows itself.
func slowReleaseServer(t *testing.T, tag string, content []byte, delay time.Duration) *httptest.Server {
	t.Helper()
	name := "yawble_" + strings.TrimPrefix(tag, "v") + "_linux_" + runtime.GOARCH + ".tar.gz"
	var archive bytes.Buffer
	gz := gzip.NewWriter(&archive)
	tw := tar.NewWriter(gz)
	_ = tw.WriteHeader(&tar.Header{Name: "yawble", Mode: 0o755, Size: int64(len(content))})
	_, _ = tw.Write(content)
	_ = tw.Close()
	_ = gz.Close()
	sum := sha256.Sum256(archive.Bytes())
	checksums := hex.EncodeToString(sum[:]) + "  " + name + "\n"
	mux := http.NewServeMux()
	var server *httptest.Server
	mux.HandleFunc("/repos/djlsystems/Yawble/releases", func(w http.ResponseWriter, r *http.Request) {
		_, _ = w.Write([]byte(`[{"tag_name":"` + tag + `","assets":[{"name":"` + name + `","browser_download_url":"` + server.URL + `/dl/` + name + `"},{"name":"checksums.txt","browser_download_url":"` + server.URL + `/dl/checksums.txt"}]}]`))
	})
	mux.HandleFunc("/dl/"+name, func(w http.ResponseWriter, r *http.Request) {
		time.Sleep(delay)
		_, _ = w.Write(archive.Bytes())
	})
	mux.HandleFunc("/dl/checksums.txt", func(w http.ResponseWriter, r *http.Request) {
		time.Sleep(delay)
		_, _ = w.Write([]byte(checksums))
	})
	server = httptest.NewServer(mux)
	t.Cleanup(server.Close)
	return server
}
