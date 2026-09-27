// Package release reads yawble's own GitHub releases and replaces the running binary with one.
// The API base URL is a parameter so tests serve a fake release from httptest.
package release

import (
	"archive/tar"
	"archive/zip"
	"bytes"
	"compress/gzip"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"strings"
)

const (
	DefaultBaseURL = "https://api.github.com"
	Repository     = "djlsystems/Yawble"
)

// Release is the latest release as far as one target cares: its tag, the archive for this OS and
// architecture, and the checksum file.
type Release struct {
	Tag          string
	AssetName    string
	AssetURL     string
	ChecksumsURL string
}

// Latest reads the newest release and picks this target's archive. Newest is the highest version
// among the listed releases, pre-releases included: GitHub's own "latest" never is a pre-release,
// and its list is not in release order (measured: a regular release listed before a later
// pre-release). A draft (listed only to the owner) is skipped. With a
// token (GH_TOKEN, only for a private fork) the request carries it and the assets are read at
// their API URLs, the only ones a private repository serves; without one nothing changes.
func Latest(ctx context.Context, client *http.Client, baseURL, token, goos, goarch string) (Release, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, strings.TrimRight(baseURL, "/")+"/repos/"+Repository+"/releases?per_page=100", nil)
	if err != nil {
		return Release{}, err
	}
	req.Header.Set("Accept", "application/vnd.github+json")
	req.Header.Set("User-Agent", "yawble")
	if token != "" {
		req.Header.Set("Authorization", "Bearer "+token)
	}
	resp, err := unbounded(client).Do(req)
	if err != nil {
		return Release{}, fmt.Errorf("reading the latest release: %w", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		if token == "" {
			return Release{}, fmt.Errorf("reading the latest release: GitHub answered %s (check the network connection; a private fork also needs GH_TOKEN with the repo scope)", resp.Status)
		}
		return Release{}, fmt.Errorf("reading the latest release: GitHub answered %s with GH_TOKEN set (does the token have the repo scope and access to %s?)", resp.Status, Repository)
	}
	type listed struct {
		Tag    string `json:"tag_name"`
		Draft  bool   `json:"draft"`
		Assets []struct {
			Name   string `json:"name"`
			URL    string `json:"browser_download_url"`
			APIURL string `json:"url"`
		} `json:"assets"`
	}
	var releases []listed
	if err := json.NewDecoder(resp.Body).Decode(&releases); err != nil {
		return Release{}, fmt.Errorf("reading the latest release: %w", err)
	}
	var body listed
	for _, r := range releases {
		if !r.Draft && (body.Tag == "" || newer(r.Tag, body.Tag)) {
			body = r
		}
	}
	if body.Tag == "" {
		return Release{}, fmt.Errorf("reading the latest release: %s has no release yet", Repository)
	}
	rel := Release{Tag: body.Tag}
	suffix := ".tar.gz"
	if goos == "windows" {
		suffix = ".zip"
	}
	want := "yawble_" + strings.TrimPrefix(body.Tag, "v") + "_" + goos + "_" + goarch + suffix
	for _, a := range body.Assets {
		if token != "" {
			// A private repository's browser URLs answer 404 even with a token; the API URL
			// with Accept: application/octet-stream is the download.
			a.URL = a.APIURL
		}
		switch a.Name {
		case want:
			rel.AssetName, rel.AssetURL = a.Name, a.URL
		case "checksums.txt":
			rel.ChecksumsURL = a.URL
		}
	}
	if rel.AssetURL == "" {
		return rel, fmt.Errorf("release %s has no build for %s/%s", body.Tag, goos, goarch)
	}
	if rel.ChecksumsURL == "" {
		return rel, fmt.Errorf("release %s has no checksums.txt; refusing to install an unverifiable binary", body.Tag)
	}
	return rel, nil
}

// Install downloads the archive, verifies it against checksums.txt, extracts the binary and
// puts it where target is. Nothing at target changes until the checksum has matched. On Windows
// the running file cannot be overwritten, so it is moved aside to <target>.old and swept later.
func Install(ctx context.Context, client *http.Client, rel Release, token, target, goos string) error {
	archive, err := fetch(ctx, client, rel.AssetURL, token)
	if err != nil {
		return err
	}
	checksums, err := fetch(ctx, client, rel.ChecksumsURL, token)
	if err != nil {
		return err
	}
	expected := checksumFor(string(checksums), rel.AssetName)
	if expected == "" {
		return fmt.Errorf("%s is not listed in checksums.txt", rel.AssetName)
	}
	sum := sha256.Sum256(archive)
	if actual := hex.EncodeToString(sum[:]); actual != expected {
		return fmt.Errorf("checksum mismatch for %s: the release says %s, the download is %s; nothing was replaced", rel.AssetName, expected[:12], actual[:12])
	}
	binary, err := extract(archive, goos)
	if err != nil {
		return err
	}
	dir := filepath.Dir(target)
	tmp, err := os.CreateTemp(dir, ".yawble-new-*")
	if err != nil {
		return err
	}
	tmpName := tmp.Name()
	if _, err := tmp.Write(binary); err != nil {
		_ = tmp.Close()
		_ = os.Remove(tmpName)
		return err
	}
	if err := tmp.Close(); err != nil {
		_ = os.Remove(tmpName)
		return err
	}
	if err := os.Chmod(tmpName, 0o755); err != nil {
		_ = os.Remove(tmpName)
		return err
	}
	movedAside := false
	if goos == "windows" {
		old := target + ".old"
		_ = os.Remove(old)
		if err := os.Rename(target, old); err != nil && !errors.Is(err, os.ErrNotExist) {
			_ = os.Remove(tmpName)
			return fmt.Errorf("moving the running yawble.exe aside: %w", err)
		}
		movedAside = err == nil
	}
	if err := os.Rename(tmpName, target); err != nil {
		_ = os.Remove(tmpName)
		if movedAside {
			// Never leave the person with no yawble.exe at all: the old one goes back.
			_ = os.Rename(target+".old", target)
		}
		return fmt.Errorf("putting the new binary in place: %w", err)
	}
	return nil
}

// SweepOld removes the <exe>.old a Windows update left behind, if any. Called at start.
func SweepOld(exe string) { _ = os.Remove(exe + ".old") }

// unbounded is the caller's client without its overall timeout. The command layer's client is
// tuned for health polls (a few seconds); http.Client.Timeout covers reading the whole body, so
// a binary of a few megabytes would fail on any link slower than that allows. The context still
// bounds the request; the transport, cookies and TLS settings are kept.
func unbounded(client *http.Client) *http.Client {
	if client == nil {
		return &http.Client{}
	}
	if client.Timeout == 0 {
		return client
	}
	copy := *client
	copy.Timeout = 0
	return &copy
}

func fetch(ctx context.Context, client *http.Client, url, token string) ([]byte, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
	if err != nil {
		return nil, err
	}
	req.Header.Set("User-Agent", "yawble")
	c := unbounded(client)
	if token != "" {
		req.Header.Set("Authorization", "Bearer "+token)
		req.Header.Set("Accept", "application/octet-stream")
		c = withoutTokenOffOrigin(c)
	}
	resp, err := c.Do(req)
	if err != nil {
		return nil, fmt.Errorf("downloading %s: %w", url, err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("downloading %s: %s", url, resp.Status)
	}
	return io.ReadAll(io.LimitReader(resp.Body, 200<<20))
}

// withoutTokenOffOrigin is client, except that a redirect to any other origin (scheme, host
// and port) loses the Authorization header. GitHub answers an asset with a redirect to a signed
// storage URL that needs no token and must never be sent one. Go already drops the header for
// another host name; this also covers another port on the same host, and does not depend on it.
func withoutTokenOffOrigin(client *http.Client) *http.Client {
	copy := *client
	previous := client.CheckRedirect
	copy.CheckRedirect = func(req *http.Request, via []*http.Request) error {
		if req.URL.Host != via[0].URL.Host || req.URL.Scheme != via[0].URL.Scheme {
			req.Header.Del("Authorization")
		}
		if previous != nil {
			return previous(req, via)
		}
		if len(via) >= 10 {
			return errors.New("stopped after 10 redirects")
		}
		return nil
	}
	return &copy
}

// checksumFor accepts "hash  name" and coreutils' binary-mode "hash *name".
func checksumFor(checksums, asset string) string {
	for _, line := range strings.Split(checksums, "\n") {
		fields := strings.Fields(line)
		if len(fields) == 2 && (fields[1] == asset || fields[1] == "*"+asset) {
			return strings.ToLower(fields[0])
		}
	}
	return ""
}

func extract(archive []byte, goos string) ([]byte, error) {
	if goos == "windows" {
		zr, err := zip.NewReader(bytes.NewReader(archive), int64(len(archive)))
		if err != nil {
			return nil, err
		}
		for _, f := range zr.File {
			if f.Name == "yawble.exe" {
				rc, err := f.Open()
				if err != nil {
					return nil, err
				}
				defer rc.Close()
				return io.ReadAll(rc)
			}
		}
		return nil, errors.New("the archive holds no yawble.exe")
	}
	gz, err := gzip.NewReader(bytes.NewReader(archive))
	if err != nil {
		return nil, err
	}
	tr := tar.NewReader(gz)
	for {
		h, err := tr.Next()
		if err == io.EOF {
			return nil, errors.New("the archive holds no yawble")
		}
		if err != nil {
			return nil, err
		}
		if h.Name == "yawble" || strings.HasSuffix(h.Name, "/yawble") {
			return io.ReadAll(tr)
		}
	}
}

// newer says whether tag a is a later release than tag b. Tags are v<yyyy.mm.dd.N>: compared part
// by part as numbers, so .10 is after .9. A tag that is not in that form sorts before every one that is.
func newer(a, b string) bool {
	pa, pb := versionParts(a), versionParts(b)
	for i := range pa {
		if pa[i] != pb[i] {
			return pa[i] > pb[i]
		}
	}
	return false
}

func versionParts(tag string) [4]int {
	var parts [4]int
	fields := strings.Split(strings.TrimPrefix(tag, "v"), ".")
	if len(fields) != 4 {
		return [4]int{-1, -1, -1, -1}
	}
	for i, f := range fields {
		n, err := strconv.Atoi(f)
		if err != nil {
			return [4]int{-1, -1, -1, -1}
		}
		parts[i] = n
	}
	return parts
}
