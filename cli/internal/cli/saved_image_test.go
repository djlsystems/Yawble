package cli_test

import (
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/config"
	"github.com/djlsystems/yawble/cli/internal/engine"
)

func readConfig(t *testing.T, dir string) config.Config {
	t.Helper()
	c, _, err := config.Load(dir, func(string) string { return "" })
	if err != nil {
		t.Fatal(err)
	}
	return c
}

func noMoves(t *testing.T, program string, calls []string) {
	t.Helper()
	for _, c := range calls {
		if strings.Contains(c, program+" run") || strings.Contains(c, program+" rm") || strings.Contains(c, program+" pull") {
			t.Errorf("moved something: %q", c)
		}
	}
}

// A config left pinning the previous release (an earlier move saved it) is moved by a newer
// yawble: both saved settings are rewritten to this yawble's pins, the rewrite is said, and the
// instance moves - on Podman and on Docker.
func TestUpdateRewritesASavedPreviousReleaseImageAndMovesTheInstance(t *testing.T) {
	for _, program := range []string{"podman", "docker"} {
		t.Run(program, func(t *testing.T) {
			pinBuild(t) // pins 2026.09.25.1
			old := "ghcr.io/djlsystems/yawble:2026.09.24.1"
			s := engine.NewScripted()
			s.On(program+" container inspect", engine.Result{Stdout: "running|" + old + "|" + labelFor(old) + "\n"})
			s.On(program+" image exists", engine.Result{ExitCode: 1})
			deps := stubbed(s)
			deps.Env = func(string) string { return "" }
			deps.LookPath = lookPath(program)
			deps.ConfigDir = t.TempDir()
			seedWorkerKey(t, deps.ConfigDir)
			writeConfig(t, deps.ConfigDir, "engine = \""+program+"\"\nimage = \""+old+"\"\nworkerImage = \""+old+"-worker\"\n")

			code, out, errOut := run(t, deps, "update", "--instance")
			if code != 0 {
				t.Fatalf("exit %d: %s %s", code, out, errOut)
			}
			if c := readConfig(t, deps.ConfigDir); c.Image != pinned || c.WorkerImage != pinned+"-worker" || c.Engine != program {
				t.Errorf("config %+v", c)
			}
			for _, want := range []string{
				"image " + old + " -> " + pinned + " (saved setting updated)",
				"workerImage " + old + "-worker -> " + pinned + "-worker (saved setting updated)",
				"image " + old + " -> " + pinned + "\n",
			} {
				if !strings.Contains(out, want) {
					t.Errorf("out lacks %q:\n%s", want, out)
				}
			}
			if strings.Contains(out, "nothing to update") {
				t.Errorf("out %q", out)
			}
			c := strings.Join(s.Calls, "\n")
			for _, want := range []string{program + " rm -f yawble", program + " run -d --name yawble"} {
				if !strings.Contains(c, want) {
					t.Errorf("missing %q in:\n%s", want, c)
				}
			}
			if !strings.Contains(c, pinned) || strings.Contains(c, "run -d --name yawble-worker-1 "+old) {
				t.Errorf("the instance must move to %s:\n%s", pinned, c)
			}
		})
	}
}

// A saved local dev build is the person's own choice: update leaves it, moves nothing, and says
// which setting holds the instance and how to change it - never "nothing to update".
func TestUpdateLeavesASavedDevImageAndSaysTheSettingHoldsTheInstance(t *testing.T) {
	dev := "localhost/yawble:dev-3f2a1c9"
	for _, args := range [][]string{{"update", "--instance"}, {"update"}} {
		t.Run(strings.Join(args, " "), func(t *testing.T) {
			pinBuild(t)
			server := fakeReleaseServer(t, "v0.2.0", []byte("same")) // this yawble is the latest
			s := engine.NewScripted()
			s.On("podman container inspect", engine.Result{Stdout: "running|" + dev + "|" + labelFor(dev) + "\n"})
			scriptWorker(s, "podman", "running", settingsForImage(dev), 1)
			deps := stubbed(s)
			deps.Env = func(string) string { return "" }
			deps.HTTP, deps.ReleaseBaseURL = server.Client(), server.URL
			deps.ConfigDir = t.TempDir()
			seedWorkerKey(t, deps.ConfigDir)
			writeConfig(t, deps.ConfigDir, "image = \""+dev+"\"\n")
			before, _ := os.ReadFile(filepath.Join(deps.ConfigDir, config.FileName))

			code, out, errOut := run(t, deps, args...)
			if code != 0 {
				t.Fatalf("exit %d: %s %s", code, out, errOut)
			}
			if after, _ := os.ReadFile(filepath.Join(deps.ConfigDir, config.FileName)); string(after) != string(before) {
				t.Errorf("config changed:\n%s", after)
			}
			noMoves(t, "podman", s.Calls)
			want := "the saved setting image in config.toml holds the instance on " + dev + ", not " + pinned
			if !strings.Contains(out, want) || !strings.Contains(out, "yawble config set image") {
				t.Errorf("out lacks %q and the command:\n%s", want, out)
			}
			if strings.Contains(out, "nothing to update") {
				t.Errorf("said nothing to update while a saved setting holds it:\n%s", out)
			}
		})
	}
}

// A saved release newer than this yawble's pin is never moved back.
func TestUpdateNeverMovesASavedNewerReleaseBack(t *testing.T) {
	pinBuild(t)
	newer := "ghcr.io/djlsystems/yawble:2026.09.26.1"
	s := engine.NewScripted()
	s.On("podman container inspect", engine.Result{Stdout: "running|" + newer + "|" + labelFor(newer) + "\n"})
	scriptWorker(s, "podman", "running", settingsForImage(newer), 1)
	deps := stubbed(s)
	deps.Env = func(string) string { return "" }
	deps.ConfigDir = t.TempDir()
	seedWorkerKey(t, deps.ConfigDir)
	writeConfig(t, deps.ConfigDir, "image = \""+newer+"\"\n")

	code, out, errOut := run(t, deps, "update", "--instance")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if c := readConfig(t, deps.ConfigDir); c.Image != newer {
		t.Errorf("saved image moved back to %q", c.Image)
	}
	noMoves(t, "podman", s.Calls)
	if strings.Contains(out, "-> "+pinned) || !strings.Contains(out, "never moves back") {
		t.Errorf("out %q", out)
	}
}

func TestVersionShowsTheSavedImageWhenItDiffersFromThePin(t *testing.T) {
	pinBuild(t)
	dir := t.TempDir()
	deps := stubbed(engine.NewScripted())
	deps.ConfigDir = dir

	_, out, _ := run(t, deps, "version")
	if strings.Contains(out, "saved image") {
		t.Errorf("nothing is saved, yet:\n%s", out)
	}

	writeConfig(t, dir, "image = \""+pinned+"\"\n")
	if _, out, _ = run(t, deps, "version"); strings.Contains(out, "saved image") {
		t.Errorf("the saved image is the pin, yet:\n%s", out)
	}

	old := "ghcr.io/djlsystems/yawble:2026.09.24.1"
	writeConfig(t, dir, "image = \""+old+"\"\n")
	_, out, _ = run(t, deps, "version")
	if !strings.Contains(out, "image   "+pinned) || !strings.Contains(out, "saved image "+old+" (config.toml; this is what yawble up starts)\n") {
		t.Errorf("out %q", out)
	}
}
