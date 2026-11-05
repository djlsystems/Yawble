package machine

import (
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
)

// runWriteScript runs the real writeScript in sh, with the machine's /etc/systemd/system moved
// under a temporary root and stub sudo (runs its command) and systemctl (logs its arguments).
func runWriteScript(t *testing.T, root string) (int, string) {
	t.Helper()
	// The script runs inside the Podman machine, a Linux shell. On Windows the sh on PATH is
	// Git's, which cannot run the stubs (no extension) or the script's paths (C:\...), so the
	// script is checked where it runs: on Linux and macOS.
	if runtime.GOOS == "windows" {
		t.Skip("the write script is a Linux shell script; it is run for real on Linux and macOS")
	}
	sh, err := exec.LookPath("sh")
	if err != nil {
		t.Skip("no sh here")
	}
	bin := filepath.Join(root, "bin")
	if err := os.MkdirAll(bin, 0o755); err != nil {
		t.Fatal(err)
	}
	log := filepath.Join(root, "systemctl.log")
	stubs := map[string]string{
		"sudo":      "#!/bin/sh\n[ \"$1\" = -n ] && shift\nexec \"$@\"\n",
		"systemctl": "#!/bin/sh\necho \"$*\" >> " + log + "\n",
	}
	for name, body := range stubs {
		if err := os.WriteFile(filepath.Join(bin, name), []byte(body), 0o755); err != nil {
			t.Fatal(err)
		}
	}
	script := strings.ReplaceAll(writeScript, "/etc/systemd/system", filepath.Join(root, "etc/systemd/system"))
	cmd := exec.Command(sh, "-c", script)
	cmd.Env = append(os.Environ(), "PATH="+bin+string(os.PathListSeparator)+os.Getenv("PATH"))
	out, err := cmd.CombinedOutput()
	code := 0
	if ee, ok := err.(*exec.ExitError); ok {
		code = ee.ExitCode()
	} else if err != nil {
		t.Fatalf("%v: %s", err, out)
	}
	logged, _ := os.ReadFile(log)
	return code, string(logged)
}

// The write's own guard, run for real: a fresh write makes exactly yawble's file and reloads
// systemd; a rewrite over yawble's own file is fine; a file yawble did not write - even one that
// mentions the mark below its first line - is refused with exit 3 and left as it was.
func TestTheWriteScriptWritesOnlyOverItsOwnFile(t *testing.T) {
	file := func(root string) string {
		return filepath.Join(root, "etc/systemd/system/user@.service.d/delegate.conf")
	}

	t.Run("fresh", func(t *testing.T) {
		root := t.TempDir()
		code, logged := runWriteScript(t, root)
		got, err := os.ReadFile(file(root))
		if code != 0 || err != nil || string(got) != DelegateContent || logged != "daemon-reload\n" {
			t.Errorf("exit %d, file %q (%v), systemctl %q", code, got, err, logged)
		}
	})

	t.Run("over its own file", func(t *testing.T) {
		root := t.TempDir()
		if err := os.MkdirAll(filepath.Dir(file(root)), 0o755); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(file(root), []byte(DelegateMark+"\n[Service]\nDelegate=memory\n"), 0o644); err != nil {
			t.Fatal(err)
		}
		code, logged := runWriteScript(t, root)
		got, _ := os.ReadFile(file(root))
		if code != 0 || string(got) != DelegateContent || logged != "daemon-reload\n" {
			t.Errorf("exit %d, file %q, systemctl %q", code, got, logged)
		}
	})

	for name, foreign := range map[string]string{
		"foreign":                 "[Service]\nDelegate=memory pids cpu io\n",
		"mark not the first line": "[Service]\n" + DelegateMark + "\nDelegate=memory\n",
		"mark with more on it":    DelegateMark + " (edited)\n[Service]\nDelegate=memory\n",
	} {
		t.Run(name, func(t *testing.T) {
			root := t.TempDir()
			if err := os.MkdirAll(filepath.Dir(file(root)), 0o755); err != nil {
				t.Fatal(err)
			}
			if err := os.WriteFile(file(root), []byte(foreign), 0o644); err != nil {
				t.Fatal(err)
			}
			code, logged := runWriteScript(t, root)
			got, _ := os.ReadFile(file(root))
			if code != 3 || string(got) != foreign || logged != "" {
				t.Errorf("exit %d, file %q, systemctl %q", code, got, logged)
			}
		})
	}
}
