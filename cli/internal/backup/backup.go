// Package backup is the archive `yawble backup` writes and `yawble restore` reads: a .tar.gz whose
// first entry is manifest.json and whose data is the volume's tree under data/. It knows nothing
// about engines; the command layer hands it the tar stream a helper container produced.
//
// THE FILE IS TWO GZIP MEMBERS. The manifest carries counts only known once the data has been
// read, and it must still come first so restore can refuse a backup before extracting anything.
// So the data is written to a temporary file first, then the archive is the manifest's member
// followed by that file. Concatenated members are one valid gzip stream: gzip, tar, bsdtar and
// Go all read the file as one tar.
package backup

import (
	"archive/tar"
	"compress/gzip"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"time"
)

// FormatVersion is the archive layout this build writes and the newest it reads.
const FormatVersion = 1

const (
	ManifestName = "manifest.json"
	// DataPrefix is where the volume's tree sits in the archive: data/teams/... is /data/teams/...
	DataPrefix = "data/"
)

// Manifest is manifest.json. Every field is written, empty or not, so a reader never has to
// guess whether a field was forgotten or empty.
type Manifest struct {
	Format        int       `json:"format"`
	YawbleVersion string    `json:"yawbleVersion"`
	Image         string    `json:"image"`
	ImageVersion  string    `json:"imageVersion"`
	Engine        string    `json:"engine"`
	EngineVersion string    `json:"engineVersion"`
	Processor     string    `json:"processor"`
	CreatedAt     time.Time `json:"createdAt"`
	// SchemaSteps are the ids in the database's schema_migrations table, in the order applied;
	// empty when the database could not be read (SchemaNote says why).
	SchemaSteps []string `json:"schemaSteps"`
	SchemaNote  string   `json:"schemaNote,omitempty"`
	Full        bool     `json:"full"`
	Excluded    []string `json:"excluded"`
	Bytes       Counts   `json:"bytes"`
	// Providers are the names of the secrets `yawble secret set` held when the backup was made -
	// names only, never values. The CLI's settings are not in a backup; restore lists these so
	// the person knows what to set again.
	Providers []string `json:"providers"`
}

// Counts is what the data part holds: the bytes of every file, and how many entries.
type Counts struct {
	Data        int64 `json:"data"`
	Files       int   `json:"files"`
	Directories int   `json:"directories"`
	Links       int   `json:"links"`
}

// DefaultExcludes are left out unless --full: only what the instance brings back by itself on
// its next start. MEASURED from the entrypoint and ensure-agent-clis.sh, with the evidence in the
// team's backup-exclude-paths.md: the package caches refill on demand; the three npm-installed
// agent CLIs, Grok's binary and the headless browser are reinstalled at start when missing, and
// are processor-specific, so a copy from another machine would pass the start's check and not
// run. /data/bin and the rest of npm-global are KEPT: nothing reinstalls what an agent put there.
// Logins and settings under agent-home are kept. Patterns are tar --anchored, relative to /data.
var DefaultExcludes = []string{
	"./npm-cache",
	"./pip-cache",
	"./go-cache",
	"./nuget",
	"./ms-playwright",
	"./npm-global/bin/claude",
	"./npm-global/bin/codex",
	"./npm-global/bin/copilot",
	"./npm-global/lib/node_modules/@anthropic-ai/claude-code",
	"./npm-global/lib/node_modules/@anthropic-ai/.claude-code-*",
	"./npm-global/lib/node_modules/@openai/codex",
	"./npm-global/lib/node_modules/@openai/.codex-*",
	"./npm-global/lib/node_modules/@github/copilot",
	"./npm-global/lib/node_modules/@github/.copilot-*",
	"./agent-home/.grok/bin",
	"./agent-home/.grok/downloads",
}

// TarArgs is the helper's tar command line (after the program): the whole of root, owners as
// numbers (the users have the same ids on every engine), to stdout, the default excludes unless full.
func TarArgs(root string, full bool) []string {
	args := []string{"-C", root, "--numeric-owner"}
	if !full {
		args = append(args, "--anchored", "--wildcards")
		for _, e := range DefaultExcludes {
			args = append(args, "--exclude="+e)
		}
	}
	return append(args, "-cf", "-", ".")
}

// FileName is the default name for a backup made at t: yawble-backup-yyyyMMdd-HHmmss.tar.gz.
func FileName(t time.Time) string {
	return "yawble-backup-" + t.Format("20060102-150405") + ".tar.gz"
}

// Write makes the archive at path from the tar stream produce writes. The file is owner-only
// from its first byte and appears at path only when complete; path must not exist. The manifest
// is m with its byte counts filled in, and is returned.
func Write(path string, m Manifest, produce func(io.Writer) error) (Manifest, error) {
	if _, err := os.Lstat(path); err == nil {
		return m, fmt.Errorf("%s already exists; choose another name with --output", path)
	}
	dataPath, finalPath := path+".data.partial", path+".partial"
	defer os.Remove(dataPath)
	counts, err := writeData(dataPath, produce)
	if err != nil {
		return m, err
	}
	m.Format, m.Bytes = FormatVersion, counts
	if m.SchemaSteps == nil {
		m.SchemaSteps = []string{}
	}
	if m.Excluded == nil {
		m.Excluded = []string{}
	}
	if m.Providers == nil {
		m.Providers = []string{}
	}
	if err := writeFinal(finalPath, dataPath, m); err != nil {
		os.Remove(finalPath)
		return m, err
	}
	if err := os.Rename(finalPath, path); err != nil {
		os.Remove(finalPath)
		return m, err
	}
	return m, nil
}

func create(path string) (*os.File, error) {
	f, err := os.OpenFile(path, os.O_WRONLY|os.O_CREATE|os.O_TRUNC, 0o600)
	if err != nil {
		return nil, err
	}
	// The mode is set again in case the file was already there with a wider one.
	_ = f.Chmod(0o600)
	return f, nil
}

// writeData copies the helper's tar into a gzip member under data/, counting as it goes. The
// helper runs on the other end of a pipe; an error on either side stops both.
func writeData(path string, produce func(io.Writer) error) (Counts, error) {
	f, err := create(path)
	if err != nil {
		return Counts{}, err
	}
	defer f.Close()
	gz := gzip.NewWriter(f)
	tw := tar.NewWriter(gz)

	pr, pw := io.Pipe()
	type copied struct {
		counts Counts
		err    error
	}
	done := make(chan copied, 1)
	go func() {
		counts, err := copyEntries(tar.NewReader(pr), tw, toArchive)
		if err == nil {
			// tar pads its output to whole records after the end marker; that is drained, so the
			// helper never blocks on a full pipe. On an error the pipe is closed, which stops it.
			_, _ = io.Copy(io.Discard, pr)
		}
		pr.CloseWithError(err)
		done <- copied{counts, err}
	}()
	perr := produce(pw)
	pw.CloseWithError(perr)
	res := <-done
	if perr != nil {
		return Counts{}, perr
	}
	if res.err != nil {
		return Counts{}, fmt.Errorf("reading the volume's tar stream: %w", res.err)
	}
	if res.counts.Files+res.counts.Directories+res.counts.Links == 0 {
		return Counts{}, errors.New("the helper container produced no data")
	}
	if err := tw.Close(); err != nil {
		return Counts{}, err
	}
	if err := gz.Close(); err != nil {
		return Counts{}, err
	}
	return res.counts, f.Sync()
}

// writeFinal is the manifest's own gzip member (one tar entry, no end-of-archive marker) and then
// the data member, whose tar carries the marker.
func writeFinal(path, dataPath string, m Manifest) error {
	f, err := create(path)
	if err != nil {
		return err
	}
	defer f.Close()
	body, err := json.MarshalIndent(m, "", "  ")
	if err != nil {
		return err
	}
	body = append(body, '\n')
	gz := gzip.NewWriter(f)
	tw := tar.NewWriter(gz)
	if err := tw.WriteHeader(&tar.Header{Name: ManifestName, Mode: 0o600, Size: int64(len(body)), ModTime: m.CreatedAt, Typeflag: tar.TypeReg}); err != nil {
		return err
	}
	if _, err := tw.Write(body); err != nil {
		return err
	}
	// Flush pads the entry; Close would end the archive before the data.
	if err := tw.Flush(); err != nil {
		return err
	}
	if err := gz.Close(); err != nil {
		return err
	}
	data, err := os.Open(dataPath)
	if err != nil {
		return err
	}
	defer data.Close()
	if _, err := io.Copy(f, data); err != nil {
		return err
	}
	return f.Sync()
}

// copyEntries copies every entry from r to w with its name mapped; an entry the map answers ""
// for is skipped. Hard links name another entry, so their target is mapped too.
func copyEntries(r *tar.Reader, w *tar.Writer, rename func(string) string) (Counts, error) {
	var c Counts
	for {
		hdr, err := r.Next()
		if errors.Is(err, io.EOF) {
			return c, nil
		}
		if err != nil {
			return c, err
		}
		name := rename(hdr.Name)
		if name == "" {
			continue
		}
		hdr.Name = name
		if hdr.Typeflag == tar.TypeLink {
			hdr.Linkname = rename(hdr.Linkname)
		}
		// The writer picks the format each header needs (long names, big ids).
		hdr.Format = tar.FormatUnknown
		if err := w.WriteHeader(hdr); err != nil {
			return c, err
		}
		switch hdr.Typeflag {
		case tar.TypeDir:
			c.Directories++
		case tar.TypeSymlink, tar.TypeLink:
			c.Links++
		default:
			n, err := io.Copy(w, r)
			if err != nil {
				return c, err
			}
			c.Data += n
			c.Files++
		}
	}
}

// toArchive maps the helper's "./teams/x" to "data/teams/x", and "./" itself to "data/".
func toArchive(name string) string {
	name = strings.TrimPrefix(strings.TrimPrefix(name, "./"), "/")
	if name == "" || name == "." {
		return DataPrefix
	}
	return DataPrefix + name
}

// fromArchive maps "data/teams/x" back to "./teams/x" for tar -C /data; anything outside data/
// (the manifest) is skipped.
func fromArchive(name string) string {
	if !strings.HasPrefix(name, DataPrefix) {
		return ""
	}
	rest := strings.TrimPrefix(name, DataPrefix)
	if rest == "" {
		return "./"
	}
	return "./" + rest
}

// ErrNotABackup is a file that is not a gzip'd tar with a manifest.json.
var ErrNotABackup = errors.New("is not a yawble backup (no manifest.json)")

// ReadManifest opens a backup and answers its manifest, which yawble writes first. A file with
// none is refused, as is a layout newer than this build reads.
func ReadManifest(path string) (Manifest, error) {
	var m Manifest
	f, err := os.Open(path)
	if err != nil {
		return m, err
	}
	defer f.Close()
	gz, err := gzip.NewReader(f)
	if err != nil {
		return m, fmt.Errorf("%s %w", path, ErrNotABackup)
	}
	tr := tar.NewReader(gz)
	for {
		hdr, err := tr.Next()
		if err != nil {
			return m, fmt.Errorf("%s %w", path, ErrNotABackup)
		}
		if hdr.Name != ManifestName {
			continue
		}
		if err := json.NewDecoder(tr).Decode(&m); err != nil {
			return m, fmt.Errorf("%s: its manifest.json cannot be read: %w", path, err)
		}
		if m.Format > FormatVersion {
			return m, fmt.Errorf("%s was written in backup format %d, and this yawble reads up to %d; run `yawble update`", path, m.Format, FormatVersion)
		}
		return m, nil
	}
}

// Extract writes the data part of a backup to w as a plain tar whose names are relative to the
// volume's root, for `tar -C /data -x` in a helper container.
func Extract(path string, w io.Writer) (Counts, error) {
	f, err := os.Open(path)
	if err != nil {
		return Counts{}, err
	}
	defer f.Close()
	gz, err := gzip.NewReader(f)
	if err != nil {
		return Counts{}, err
	}
	tw := tar.NewWriter(w)
	c, err := copyEntries(tar.NewReader(gz), tw, fromArchive)
	if err != nil {
		return c, err
	}
	return c, tw.Close()
}

// Newer says whether version a is a later Yawble than b, and whether the two could be compared at
// all. Versions are yyyy.mm.dd.N with an optional v, compared part by part as numbers; anything
// else (a local build's tag) is not comparable.
func Newer(a, b string) (newer, comparable bool) {
	pa, oka := versionParts(a)
	pb, okb := versionParts(b)
	if !oka || !okb {
		return false, false
	}
	for i := range pa {
		if pa[i] != pb[i] {
			return pa[i] > pb[i], true
		}
	}
	return false, true
}

func versionParts(v string) ([4]int, bool) {
	var parts [4]int
	fields := strings.Split(strings.TrimPrefix(v, "v"), ".")
	if v == "" || len(fields) > 4 {
		return parts, false
	}
	for i, f := range fields {
		n, err := strconv.Atoi(f)
		if err != nil {
			return parts, false
		}
		parts[i] = n
	}
	return parts, true
}

// ImageVersion is the tag of an image reference ("ghcr.io/x/yawble:2026.09.24.1" -> the date
// part), or "" for a reference without one.
func ImageVersion(ref string) string {
	if i := strings.Index(ref, "@"); i >= 0 {
		ref = ref[:i]
	}
	slash := strings.LastIndex(ref, "/")
	if colon := strings.LastIndex(ref, ":"); colon > slash {
		return ref[colon+1:]
	}
	return ""
}

// RecordName is the file in yawble's config folder that remembers the newest backup written on
// this machine, for `yawble doctor`.
const RecordName = "last-backup.json"

// Record is the newest backup this machine wrote.
type Record struct {
	Path string    `json:"path"`
	At   time.Time `json:"at"`
}

// Remember records path as the newest backup. It is a note for the doctor, so a failure to write
// it never fails a backup.
func Remember(configDir, path string, at time.Time) {
	if abs, err := filepath.Abs(path); err == nil {
		path = abs
	}
	body, _ := json.Marshal(Record{Path: path, At: at})
	if err := os.MkdirAll(configDir, 0o700); err != nil {
		return
	}
	_ = os.WriteFile(filepath.Join(configDir, RecordName), body, 0o600)
}

// Last is the newest backup recorded on this machine; ok false when none was.
func Last(configDir string) (Record, bool) {
	var r Record
	body, err := os.ReadFile(filepath.Join(configDir, RecordName))
	if errors.Is(err, fs.ErrNotExist) || err != nil {
		return r, false
	}
	if json.Unmarshal(body, &r) != nil || r.Path == "" {
		return r, false
	}
	return r, true
}

// ProviderOf names the provider a secret's key is for, or "" for a name yawble does not know.
func ProviderOf(name string) string {
	switch name {
	case "ANTHROPIC_API_KEY":
		return "Anthropic (Claude)"
	case "OPENAI_API_KEY":
		return "OpenAI (Codex)"
	case "XAI_API_KEY":
		return "xAI (Grok)"
	case "GEMINI_API_KEY", "GOOGLE_API_KEY":
		return "Google (Gemini)"
	case "GH_TOKEN", "GITHUB_TOKEN":
		return "GitHub"
	}
	return ""
}
