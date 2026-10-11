package config

import (
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"io/fs"
	"os"
	"path/filepath"
	"strings"
)

// WorkerKeyFileName holds the key control and every worker share, as one env-file line. It
// reaches a container only as --env-file, never on a command line.
const WorkerKeyFileName = "worker.env"

// workerKeyVariable is the line's name; the entrypoint moves its value to a file and unsets it.
const workerKeyVariable = "HARNESS_WORKER_KEY"

// WorkerKeyFile is where the worker key is kept, beside the env file.
func WorkerKeyFile(dir string) string { return filepath.Join(dir, WorkerKeyFileName) }

// WorkerKeyHash is a digest of the kept key, never the key: the first 12 hex characters of its
// SHA-256, what the settings label records. "" with no error when there is no key yet.
func WorkerKeyHash(dir string) (string, error) {
	key, err := readWorkerKey(dir)
	if err != nil || key == "" {
		return "", err
	}
	return keyHash(key), nil
}

// EnsureWorkerKey makes the worker key once - 32 random bytes as 64 lowercase hex characters -
// and keeps it after: every later call answers the same key's hash. The file is owner-only and
// written whole through a rename, so no reader ever sees half a key.
func EnsureWorkerKey(dir string) (hash string, made bool, err error) {
	key, err := readWorkerKey(dir)
	if err != nil {
		return "", false, err
	}
	if key != "" {
		return keyHash(key), false, nil
	}
	raw := make([]byte, 32)
	if _, err := rand.Read(raw); err != nil {
		return "", false, err
	}
	key = hex.EncodeToString(raw)
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return "", false, err
	}
	tmp, err := os.CreateTemp(dir, ".worker.env-*")
	if err != nil {
		return "", false, err
	}
	defer os.Remove(tmp.Name())
	if err := tmp.Chmod(0o600); err != nil {
		tmp.Close()
		return "", false, err
	}
	if _, err := tmp.WriteString(workerKeyVariable + "=" + key + "\n"); err != nil {
		tmp.Close()
		return "", false, err
	}
	if err := tmp.Close(); err != nil {
		return "", false, err
	}
	if err := os.Rename(tmp.Name(), WorkerKeyFile(dir)); err != nil {
		return "", false, err
	}
	return keyHash(key), true, nil
}

func readWorkerKey(dir string) (string, error) {
	data, err := os.ReadFile(WorkerKeyFile(dir))
	if errors.Is(err, fs.ErrNotExist) {
		return "", nil
	}
	if err != nil {
		return "", fmt.Errorf("reading %s: %w", WorkerKeyFile(dir), err)
	}
	for _, line := range strings.Split(string(data), "\n") {
		if value, ok := strings.CutPrefix(strings.TrimSpace(line), workerKeyVariable+"="); ok && value != "" {
			return value, nil
		}
	}
	return "", fmt.Errorf("%s holds no %s line; remove it and run yawble up to make a new key", WorkerKeyFile(dir), workerKeyVariable)
}

func keyHash(key string) string {
	sum := sha256.Sum256([]byte(key))
	return hex.EncodeToString(sum[:])[:12]
}
