package cli_test

import (
	"archive/tar"
	"bytes"
	"compress/gzip"
	"crypto/sha256"
	"encoding/hex"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/buildinfo"
	"github.com/djlsystems/yawble/cli/internal/engine"
)

const ghToken = "ghp_secretvalue123"

// updateScript is a running instance on an older image whose pinned image is not pulled yet.
func updateScript() *engine.Scripted {
	old := "ghcr.io/djlsystems/yawble:2026.09.24.1"
	s := engine.NewScripted()
	s.On("podman container inspect", engine.Result{Stdout: "running|" + old + "|" + labelFor(old) + "\n"})
	s.On("podman image exists", engine.Result{ExitCode: 1})
	return s
}

func envWith(pairs map[string]string) func(string) string {
	return func(k string) string { return pairs[k] }
}

func TestWithGHTokenSetAPullFromGhcrLogsInFirstWithTheTokenOnStdin(t *testing.T) {
	for _, variable := range []string{"GH_TOKEN", "GITHUB_TOKEN"} {
		pinBuild(t)
		s := updateScript()
		deps := stubbed(s)
		deps.Env = envWith(map[string]string{variable: ghToken})
		code, out, errOut := run(t, deps, "update")
		if code != 0 {
			t.Fatalf("%s: exit %d: %s %s", variable, code, out, errOut)
		}
		calls := strings.Join(s.Calls, "\n")
		login := strings.Index(calls, "podman login ghcr.io -u x-access-token --password-stdin")
		pull := strings.Index(calls, "podman pull "+pinned)
		if login < 0 || pull < 0 || login > pull {
			t.Errorf("%s: want a login before the pull:\n%s", variable, calls)
		}
		if strings.Contains(calls, ghToken) || strings.Contains(out+errOut, ghToken) {
			t.Errorf("%s: the token appears on a command line or in the output", variable)
		}
		if len(s.Inputs) != 1 || s.Inputs[0] != ghToken {
			t.Errorf("%s: stdin %q", variable, s.Inputs)
		}
	}
}

func TestWithNoTokenSetNothingLogsIn(t *testing.T) {
	pinBuild(t)
	s := updateScript()
	deps := stubbed(s)
	deps.Env = func(string) string { return "" }
	if code, out, errOut := run(t, deps, "update"); code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	for _, c := range s.Calls {
		if strings.Contains(c, "login") {
			t.Errorf("logged in with no token: %q", c)
		}
	}
}

// update --cli on a private repository: the API answers only with the token, and the assets
// come from their API URLs.
func TestUpdateCliWithGHTokenReadsAPrivateRelease(t *testing.T) {
	buildinfo.Version = "v0.1.0"
	t.Cleanup(func() { buildinfo.Version = "dev" })
	name := "yawble_0.2.0_linux_" + runtime.GOARCH + ".tar.gz"
	content := []byte("the private yawble")
	var archive bytes.Buffer
	gz := gzip.NewWriter(&archive)
	tw := tar.NewWriter(gz)
	_ = tw.WriteHeader(&tar.Header{Name: "yawble", Mode: 0o755, Size: int64(len(content))})
	_, _ = tw.Write(content)
	_ = tw.Close()
	_ = gz.Close()
	sum := sha256.Sum256(archive.Bytes())
	checksums := hex.EncodeToString(sum[:]) + "  " + name + "\n"

	var server *httptest.Server
	mux := http.NewServeMux()
	ok := func(r *http.Request) bool { return r.Header.Get("Authorization") == "Bearer "+ghToken }
	mux.HandleFunc("/repos/djlsystems/Yawble/releases", func(w http.ResponseWriter, r *http.Request) {
		if !ok(r) {
			http.NotFound(w, r)
			return
		}
		a := server.URL + "/repos/djlsystems/Yawble/releases/assets/"
		_, _ = w.Write([]byte(`[{"tag_name":"v0.2.0","assets":[{"name":"` + name + `","url":"` + a + `1","browser_download_url":"` + server.URL + `/public/1"},{"name":"checksums.txt","url":"` + a + `2","browser_download_url":"` + server.URL + `/public/2"}]}]`))
	})
	mux.HandleFunc("/repos/djlsystems/Yawble/releases/assets/1", func(w http.ResponseWriter, r *http.Request) {
		if !ok(r) || r.Header.Get("Accept") != "application/octet-stream" {
			http.NotFound(w, r)
			return
		}
		_, _ = w.Write(archive.Bytes())
	})
	mux.HandleFunc("/repos/djlsystems/Yawble/releases/assets/2", func(w http.ResponseWriter, r *http.Request) {
		if !ok(r) || r.Header.Get("Accept") != "application/octet-stream" {
			http.NotFound(w, r)
			return
		}
		_, _ = w.Write([]byte(checksums))
	})
	mux.HandleFunc("/public/", http.NotFound)
	server = httptest.NewServer(mux)
	t.Cleanup(server.Close)

	exe := filepath.Join(t.TempDir(), "yawble")
	_ = os.WriteFile(exe, []byte("old"), 0o755)
	deps := stubbed(engine.NewScripted())
	deps.Env = envWith(map[string]string{"GH_TOKEN": ghToken})
	deps.HTTP = server.Client()
	deps.ReleaseBaseURL = server.URL
	deps.Executable = func() (string, error) { return exe, nil }
	deps.GOOS = "linux"
	code, out, errOut := run(t, deps, "update", "--cli")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if got, _ := os.ReadFile(exe); string(got) != "the private yawble" {
		t.Errorf("binary %q", got)
	}
	if strings.Contains(out+errOut, ghToken) {
		t.Errorf("the token was printed")
	}
}
