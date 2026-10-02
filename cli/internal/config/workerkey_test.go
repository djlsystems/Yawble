package config_test

import (
	"crypto/sha256"
	"encoding/hex"
	"os"
	"path/filepath"
	"regexp"
	"runtime"
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/config"
)

func TestTheWorkerKeyIsMadeOnceOwnerOnlyAndOnlyItsHashIsAnswered(t *testing.T) {
	dir := filepath.Join(t.TempDir(), "yawble")
	if h, err := config.WorkerKeyHash(dir); err != nil || h != "" {
		t.Fatalf("before: %q %v", h, err)
	}
	hash, made, err := config.EnsureWorkerKey(dir)
	if err != nil || !made {
		t.Fatalf("%v %v", made, err)
	}
	data, err := os.ReadFile(config.WorkerKeyFile(dir))
	if err != nil {
		t.Fatal(err)
	}
	m := regexp.MustCompile(`^HARNESS_WORKER_KEY=([0-9a-f]{64})\n$`).FindStringSubmatch(string(data))
	if m == nil {
		t.Fatalf("file %q", data)
	}
	sum := sha256.Sum256([]byte(m[1]))
	if hash != hex.EncodeToString(sum[:])[:12] || strings.Contains(hash, m[1]) {
		t.Errorf("hash %q", hash)
	}
	// File modes are not Unix modes on Windows: there the file is private by the user profile's ACL.
	if info, _ := os.Stat(config.WorkerKeyFile(dir)); runtime.GOOS != "windows" && info.Mode().Perm() != 0o600 {
		t.Errorf("mode %v", info.Mode())
	}
	again, made, err := config.EnsureWorkerKey(dir)
	if err != nil || made || again != hash {
		t.Errorf("second: %q %v %v", again, made, err)
	}
	if h, _ := config.WorkerKeyHash(dir); h != hash {
		t.Errorf("WorkerKeyHash %q", h)
	}
	entries, _ := os.ReadDir(dir)
	if len(entries) != 1 {
		t.Errorf("left beside the key: %v", entries)
	}
}

func TestAWorkerKeyFileWithNoKeyIsNamedNotReplaced(t *testing.T) {
	dir := t.TempDir()
	if err := os.WriteFile(config.WorkerKeyFile(dir), []byte("OTHER=1\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	if _, _, err := config.EnsureWorkerKey(dir); err == nil || !strings.Contains(err.Error(), "holds no HARNESS_WORKER_KEY line") {
		t.Errorf("err %v", err)
	}
	if data, _ := os.ReadFile(config.WorkerKeyFile(dir)); string(data) != "OTHER=1\n" {
		t.Errorf("the file was replaced: %q", data)
	}
}

func TestWorkersIsOneOrMoreAndDefaultsToOne(t *testing.T) {
	var c config.Config
	if v, _ := c.Get("workers"); v != "1" || c.WorkerCount() != 1 {
		t.Errorf("default %q", v)
	}
	for _, bad := range []string{"0", "-2", "x"} {
		if err := c.Set("workers", bad); err == nil {
			t.Errorf("%q accepted", bad)
		}
	}
	if err := c.Set("workers", "3"); err != nil || c.WorkerCount() != 3 {
		t.Errorf("3: %v %d", err, c.WorkerCount())
	}
	if err := c.Set("workerImage", "ghcr.io/me/w:1"); err != nil || c.WorkerImage != "ghcr.io/me/w:1" {
		t.Errorf("workerImage %v", err)
	}
	dir := t.TempDir()
	if err := os.WriteFile(config.Path(dir), []byte("workers = 0\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	if c, _, err := config.Load(dir, func(k string) string { return map[string]string{"YAWBLE_WORKERS": "2"}[k] }); err != nil || c.WorkerCount() != 2 {
		t.Errorf("env override: %+v %v", c, err)
	}
	if err := os.WriteFile(config.Path(dir), []byte("workers = -1\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	if _, _, err := config.Load(dir, func(string) string { return "" }); err == nil {
		t.Error("a hand-edited workers = -1 was accepted")
	}
}
